using System;
using System.Collections;
using BeatSaber.AvatarCore;
using BeatSaber.BeatAvatarSDK;
using BeatAvatarSDK = BeatSaber.BeatAvatarSDK;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zenject;

namespace BeatAvatarBody
{
    /// <summary>
    /// Spawns the player's Beat Avatar into whatever scene they are in, and keeps it spawned.
    ///
    /// Scene names are deliberately not used to decide when to act. The thing that matters is the
    /// player-space transform: when it changes, the old avatar died with the old scene and a new
    /// one is needed. Polling that is a couple of lines and cannot go stale the way a list of
    /// scene names does.
    /// </summary>
    internal sealed class BeatAvatarBodyController : MonoBehaviour
    {
        internal static BeatAvatarBodyController Instance { get; private set; }

        internal IAvatarSystem AvatarSystem { get; private set; }
        internal AvatarSystemCollection Collection { get; private set; }
        internal Avatar CurrentAvatar { get; private set; }
        internal Transform SpawnedUnder { get; private set; }
        internal Transform PoseSpace { get; private set; }

        private DiContainer _container;
        private LocalAvatarVisualProvider _visualProvider;
        private LocalPlayerPoseProvider _poseProvider;
        private BeatAvatarPartReveal _partReveal;
        private PreviewAvatar _preview;
        private bool _spawning;

        internal BeatAvatarBodyConfig Config { get; private set; }

        private void Awake()
        {
            Instance = this;
            Config = BeatAvatarBodyConfig.Load();
        }

        private IEnumerator Start()
        {
            // AvatarSystemCollection is NOT in the project container: AvatarsAsyncInstaller binds
            // it onto AppCoreSceneContext, a scene context and a child of ProjectContext, so
            // resolving from the project container returns null forever. Scan the contexts instead;
            // a child container resolves everything its parents bind, so whichever one answers is
            // also a container the avatar prefab can be injected with.
            var waited = 0f;
            while (Collection == null)
            {
                foreach (Context context in FindObjectsByType<Context>(FindObjectsSortMode.None))
                {
                    DiContainer container = context.Container;
                    if (container == null) continue;

                    var collection = container.TryResolve<AvatarSystemCollection>();
                    if (collection == null) continue;

                    _container = container;
                    Collection = collection;
                    break;
                }

                if (Collection != null) break;

                yield return new WaitForSeconds(0.5f);
                waited += 0.5f;

                // Never spin in silence. Waiting forever with no log reads as "the plugin did not
                // load" rather than "the binding is somewhere else", and those need different fixes.
                if (waited % 15f == 0f)
                    Plugin.Log.Warn("Still waiting for the avatar system after " + waited + "s.");
            }

            while (AvatarSystem == null)
            {
                AvatarSystem = Collection.GetAvatarSystem("BeatAvatarSystem");
                if (AvatarSystem == null) yield return new WaitForSeconds(0.5f);
            }

            while (true)
            {
                Tick();
                yield return new WaitForSeconds(0.5f);
            }
        }

        private void Tick()
        {
            Transform space = FindPlayerSpace();
            if (space == null) return;

            // Do not spawn into a scene that has no camera yet -- the pose provider would have
            // nothing to follow and the avatar would sit frozen at its origin.
            if (Camera.main == null) return;

            if (!_spawning && (CurrentAvatar == null || SpawnedUnder != space)) SpawnAsync(space);

            if (CurrentAvatar == null) return;

            // Re-resolve whatever the scene has since rebuilt. Cheap while everything is present:
            // EnsureRig only scans when something is actually missing.
            _poseProvider?.EnsureRig();

            // ONLY the HMD camera. Never walk Camera.allCameras, for two reasons:
            //
            //   * Camera2 implements this same convention and owns its cameras -- ApplyLayerBitmask
            //     gives a Positionable camera layer 3 and a FirstPerson one layer 6. Forcing layer 3
            //     on makes every Camera2 first-person view show the player their own head.
            //   * Camera2's Cam2_WindowOwner presents the desktop window and carries
            //     cullingMask = 0 deliberately; a non-empty mask stops it presenting at all.
            //
            // Anything else that wants the head asks for layer 3 itself. Scenes rebuild cameras, so
            // this is re-applied rather than done once; all three calls are idempotent.
            Camera hmdCamera = Camera.main;
            if (hmdCamera != null) AvatarLayers.ApplyCameraMask(hmdCamera, true);

            AvatarLayers.AddToMirrorMask();

            // URP filters layers again after the camera does, so a layer absent from the renderer's
            // masks renders on no camera at all.
            AvatarLayers.EnsureRenderPipelineLayers();
        }

        /// <summary>
        /// VRCenterAdjust is the room-offset transform the game applies the player's height and
        /// position settings to, and is what CustomAvatars parents to as well. Falling back to
        /// the main camera's parent covers scenes that have no VRCenterAdjust.
        /// </summary>
        private static Transform FindPlayerSpace()
        {
            VRCenterAdjust center = FindFirstObjectByType<VRCenterAdjust>();
            if (center != null) return center.transform;

            Camera camera = Camera.main;
            return camera != null ? camera.transform.parent : null;
        }

        private async void SpawnAsync(Transform space)
        {
            _spawning = true;
            try
            {
                Despawn();

                Avatar avatar = await AvatarSystem.InstantiateAvatar(
                    AvatarDisplayContext.MultiplayerGameplay, 0, _container);

                if (avatar == null)
                {
                    Plugin.Log.Error("InstantiateAvatar returned null");
                    return;
                }

                // The scene can change while the Addressable loads.
                if (space == null)
                {
                    Destroy(avatar.gameObject);
                    return;
                }

                avatar.transform.SetParent(space, false);
                avatar.transform.localPosition = Vector3.zero;
                avatar.transform.localRotation = Quaternion.identity;

                Transform head = AvatarLayers.Apply(avatar, Config.hideHeadInFirstPerson);

                // Sizing. The Beat Avatar is drawn for a multiplayer lobby, not for your own eyes.
                BeatSaber.BeatAvatarSDK.BeatAvatarPoseController bones = AvatarBones.PoseController(avatar);
                AvatarBones.SetScale(AvatarBones.LeftHand(bones), Config.handScale);
                AvatarBones.SetScale(AvatarBones.RightHand(bones), Config.handScale);
                AvatarBones.SetScale(head, Config.headScale);
                AvatarBones.SetScale(AvatarBones.Body(bones), Config.bodyScale);
                AvatarBones.SetVerticalOffset(head, Config.headVerticalOffset);
                AvatarBones.SetVerticalOffset(AvatarBones.Body(bones), Config.bodyVerticalOffset);

                // Poses go in the space of the head bone's PARENT, not the avatar root: BeatAvatar
                // writes them with SetLocalPositionAndRotation on the bones themselves, so
                // anything the prefab nests between root and bone would otherwise offset the whole
                // body silently.
                PoseSpace = head != null ? head.parent : avatar.transform;

                LocalPlayerPoseProvider poseProvider = avatar.gameObject.AddComponent<LocalPlayerPoseProvider>();
                _poseProvider = poseProvider;
                poseProvider.space = PoseSpace;
                poseProvider.head = Camera.main != null ? Camera.main.transform : null;
                poseProvider.useControllerOffsets = Config.useControllerOffsets;
                poseProvider.handPositionOffset = BeatAvatarBodyConfig.Offset.ToVector3(Config.handPositionOffset);
                poseProvider.handRotationOffset = BeatAvatarBodyConfig.Offset.ToVector3(Config.handRotationOffset);
                poseProvider.ResolveHands();
                poseProvider.Sample();

                // AvatarDataModel is bound AsSingle by BeatAvatarAdapterInstallerSO in the same
                // container, and carries the only live "the player edited their avatar" signal.
                var avatarDataModel = _container.TryResolve<BeatAvatarSDK.AvatarDataModel>();
                _visualProvider = await LocalAvatarVisualProvider.CreateAsync(AvatarSystem, avatarDataModel);
                avatar.SetVisualDataProvider(_visualProvider);
                avatar.SetPoseDataProvider(poseProvider);

                // After SetVisualDataProvider, never before: the reveal reads the meshes that call
                // just assigned. Subscribing here also puts it after the avatar in the provider's
                // invocation list, so an avatar edit re-applies in the right order.
                var visualController = avatar.GetComponentInChildren<BeatAvatarVisualController>(true);
                if (visualController != null)
                {
                    _partReveal = avatar.gameObject.AddComponent<BeatAvatarPartReveal>();
                    _partReveal.Bind(visualController);
                    _partReveal.Apply();
                    _visualProvider.visualDataDidChangeEvent += HandleVisualDataChanged;
                }

                CurrentAvatar = avatar;
                SpawnedUnder = space;

                StartCoroutine(ReportRigWhenSettled(poseProvider, SceneManager.GetActiveScene().name));
            }
            catch (Exception ex)
            {
                Plugin.Log.Error("Avatar spawn failed: " + ex);
            }
            finally
            {
                _spawning = false;
            }
        }

        /// <summary>
        /// One line per scene, once the rig has had a chance to settle.
        ///
        /// Deliberately not logged at spawn. The spawn instant is the worst moment to judge the
        /// rig: in VR the scene is still building and the controllers do not exist yet, so a
        /// spawn-time reading says MISSING for hands that resolve a tick later and are fine. That
        /// reads as a fault when nothing is wrong, and buries the case where something is.
        /// EnsureRig runs twice a second, so three seconds is many chances.
        /// </summary>
        private IEnumerator ReportRigWhenSettled(LocalPlayerPoseProvider provider, string scene)
        {
            yield return new WaitForSeconds(3f);

            // A scene change during the wait replaces the provider; that spawn reports for itself.
            if (provider == null || _poseProvider != provider) yield break;

            if (provider.head != null && provider.leftHand != null && provider.rightHand != null)
            {
                Plugin.Log.Info("Avatar tracking in " + scene + ": head and both hands resolved.");
                yield break;
            }

            Plugin.Log.Warn("Avatar tracking in " + scene + " is incomplete: head "
                + (provider.head != null ? "ok" : "MISSING") + ", hands "
                + (provider.leftHand != null ? "ok" : "MISSING") + "/"
                + (provider.rightHand != null ? "ok" : "MISSING")
                + ". The avatar will not follow you properly.");
        }

        private void HandleVisualDataChanged(MultiplayerAvatarsData data)
        {
            if (_partReveal == null) return;

            _partReveal.Apply();
            _preview?.ApplyReveal();
        }

        private void Despawn()
        {
            HidePreview();

            if (_visualProvider != null) _visualProvider.visualDataDidChangeEvent -= HandleVisualDataChanged;
            _visualProvider?.Dispose();
            _visualProvider = null;
            _partReveal = null;

            if (CurrentAvatar != null) Destroy(CurrentAvatar.gameObject);
            CurrentAvatar = null;
            _poseProvider = null;
            SpawnedUnder = null;
        }

        private void OnDestroy()
        {
            Despawn();
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// Pushes the current config onto the avatar already in the scene, without respawning it.
        ///
        /// The settings panel calls this on every slider change, so it must be cheap and must not
        /// destroy anything: a respawn would reload the Addressable prefab mid-drag and the
        /// preview would flicker on every tick of a slider.
        /// </summary>
        internal void ApplyConfigToCurrentAvatar()
        {
            if (CurrentAvatar != null)
            {
                BeatSaber.BeatAvatarSDK.BeatAvatarPoseController bones = AvatarBones.PoseController(CurrentAvatar);
                AvatarBones.SetScale(AvatarBones.LeftHand(bones), Config.handScale);
                AvatarBones.SetScale(AvatarBones.RightHand(bones), Config.handScale);
                AvatarBones.SetScale(AvatarBones.Head(bones), Config.headScale);
                AvatarBones.SetScale(AvatarBones.Body(bones), Config.bodyScale);
                AvatarBones.SetVerticalOffset(AvatarBones.Head(bones), Config.headVerticalOffset);
                AvatarBones.SetVerticalOffset(AvatarBones.Body(bones), Config.bodyVerticalOffset);
            }

            if (_poseProvider != null)
            {
                _poseProvider.useControllerOffsets = Config.useControllerOffsets;
                _poseProvider.handPositionOffset = BeatAvatarBodyConfig.Offset.ToVector3(Config.handPositionOffset);
                _poseProvider.handRotationOffset = BeatAvatarBodyConfig.Offset.ToVector3(Config.handRotationOffset);
                _poseProvider.ResolveHands();
            }

            _preview?.ApplyConfig(Config);
        }

        /// <summary>Spawns the tuning mirror. Idempotent.</summary>
        internal async void ShowPreviewAsync()
        {
            if (_preview != null || AvatarSystem == null || _poseProvider == null) return;

            try
            {
                Transform space = SpawnedUnder;
                if (space == null) return;

                Avatar avatar = await AvatarSystem.InstantiateAvatar(
                    AvatarDisplayContext.MultiplayerGameplay, 0, _container);
                if (avatar == null) return;

                // The scene can change while the Addressable loads.
                if (SpawnedUnder != space || _poseProvider == null)
                {
                    Destroy(avatar.gameObject);
                    return;
                }

                _preview = PreviewAvatar.Create(
                    avatar, _poseProvider, space,
                    BeatAvatarBodyConfig.Offset.ToVector3(Config.previewPosition),
                    _visualProvider, Config);

            }
            catch (Exception ex)
            {
                Plugin.Log.Error("Preview failed: " + ex);
            }
        }

        internal void HidePreview()
        {
            if (_preview == null) return;

            _preview.Dispose();
            _preview = null;
        }

    }
}
