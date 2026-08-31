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

        /// <summary>The container the avatar prefab is injected with. Exposed for the probe.</summary>
        internal DiContainer Container => _container;

        /// <summary>Exposed for the probe's tracking heartbeat.</summary>
        internal LocalPlayerPoseProvider PoseProvider => _poseProvider;

        /// <summary>How the current player space was found, for the log.</summary>
        internal static string PlayerSpaceSource { get; private set; } = "none";

        private DiContainer _container;
        private LocalAvatarVisualProvider _visualProvider;
        private LocalPlayerPoseProvider _poseProvider;
        private BeatAvatarPartReveal _partReveal;
        private PreviewAvatar _preview;
        private bool _spawning;

        // Debug levers, off by default. BSMU_AVATARBODY_OFFSET moves the avatar away from your
        // head so a flat fpfc capture can actually see it, and _YAW turns it around to face you.
        // Neither belongs in normal use -- they exist because "is it spawning?" and "does it look
        // right?" are different questions, and the first is answerable without a headset.
        private Vector3 _debugOffset;
        private float _debugYaw;

        // BSMU_AVATARBODY_NOMASKS=1 leaves every camera and mirror mask untouched. It exists so
        // the probe can read what the BASE GAME culls: this component rewrites those masks twice a
        // second, so any dump taken after it starts is measuring us, not the game.
        private bool _noMasks;

        internal BeatAvatarBodyConfig Config { get; private set; }

        private void Awake()
        {
            Instance = this;
            Config = BeatAvatarBodyConfig.Load();
            _noMasks = Environment.GetEnvironmentVariable("BSMU_AVATARBODY_NOMASKS") == "1";
            if (_noMasks) Plugin.Log.Warn("AVBODY camera/mirror mask writes DISABLED");
            _debugOffset = ParseVector(Environment.GetEnvironmentVariable("BSMU_AVATARBODY_OFFSET"));
            float.TryParse(Environment.GetEnvironmentVariable("BSMU_AVATARBODY_YAW"), out _debugYaw);

            if (_debugOffset != Vector3.zero || _debugYaw != 0f)
                Plugin.Log.Warn("AVBODY debug placement active offset=" + _debugOffset + " yaw=" + _debugYaw);
        }

        private IEnumerator Start()
        {
            // AvatarSystemCollection is NOT in the project container. AvatarsAsyncInstaller is an
            // AddressablesAsyncInstaller and the log shows it installed onto 'AppCoreSceneContext'
            // (BGLib.AppFlow.Initialization.AsyncSceneContext) -- a SCENE context, a child of
            // ProjectContext. Resolving from the project container therefore returns null forever,
            // which is exactly what the first probe run did. Scan the contexts instead: a child
            // container also resolves everything its parents bind, so whichever one answers is a
            // container we can inject the avatar prefab with.
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
                    Plugin.Log.Info("AVBODY resolved AvatarSystemCollection from context '"
                                    + context.name + "' (" + context.GetType().FullName + ")");
                    break;
                }

                if (Collection != null) break;

                yield return new WaitForSeconds(0.5f);
                waited += 0.5f;

                // Never spin in silence: the first run of this probe waited forever and logged
                // nothing, which reads as "the plugin did not load" rather than "the binding is
                // somewhere else".
                if (waited % 5f == 0f)
                    Plugin.Log.Warn("AVBODY still waiting for AvatarSystemCollection after "
                                    + waited + "s; contexts="
                                    + FindObjectsByType<Context>(FindObjectsSortMode.None).Length);
            }

            while (AvatarSystem == null)
            {
                AvatarSystem = Collection.GetAvatarSystem("BeatAvatarSystem");
                if (AvatarSystem == null)
                {
                    Plugin.Log.Warn("AVBODY BeatAvatarSystem not registered yet");
                    yield return new WaitForSeconds(0.5f);
                }
            }

            Plugin.Log.Info("AVBODY resolved BeatAvatarSystem");

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
            if (_poseProvider != null)
            {
                string recovered = _poseProvider.EnsureRig();
                if (recovered != null) Plugin.Log.Info("AVBODY rig recovered: " + recovered);
            }

            if (_noMasks) return;

            // ONLY the HMD camera. An earlier version walked Camera.allCameras and forced the mask
            // on every one of them, which is wrong twice over:
            //
            //   * Camera2 already implements this exact convention and owns its cameras. Its
            //     ApplyLayerBitmask adds layer 10 plus layer 3 to a Positionable camera and layer
            //     10 plus layer 6 to a FirstPerson one, whenever that camera's Avatar visibility is
            //     not Hidden. Forcing layer 3 on made every Camera2 first-person view show the
            //     player's own head.
            //   * Camera2's Cam2_WindowOwner camera exists to present the desktop window and
            //     carries cullingMask = 0 deliberately -- its own comment records that a non-empty
            //     mask stops it presenting at all. ORing a layer into that is a way to lose the
            //     desktop window.
            //
            // Everything else that wants the head asks for layer 3 itself. Scenes rebuild cameras,
            // so this is re-applied rather than done once; it is idempotent.
            Camera hmdCamera = Camera.main;
            if (hmdCamera != null) AvatarLayers.ApplyCameraMask(hmdCamera, true);

            AvatarLayers.AddToMirrorMask();

            // URP filters layers again after the camera does, so a layer absent here renders on no
            // camera at all -- see EnsureRenderPipelineLayers.
            AvatarLayers.EnsureRenderPipelineLayers(true);
        }

        /// <summary>
        /// VRCenterAdjust is the room-offset transform the game applies the player's height and
        /// position settings to, and is what CustomAvatars parents to as well. Falling back to
        /// the main camera's parent covers scenes that have no VRCenterAdjust.
        /// </summary>
        private static Transform FindPlayerSpace()
        {
            VRCenterAdjust center = FindFirstObjectByType<VRCenterAdjust>();
            if (center != null)
            {
                PlayerSpaceSource = "VRCenterAdjust";
                return center.transform;
            }

            Camera camera = Camera.main;
            if (camera != null && camera.transform.parent != null)
            {
                PlayerSpaceSource = "Camera.main.parent";
                return camera.transform.parent;
            }

            PlayerSpaceSource = "none";
            return null;
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
                    Plugin.Log.Error("AVBODY InstantiateAvatar returned null");
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
                poseProvider.debugOffset = _debugOffset;
                poseProvider.debugYaw = _debugYaw;
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

                Plugin.Log.Info("AVBODY spawned"
                    + " scene=" + SceneManager.GetActiveScene().name
                    + " spaceFrom=" + PlayerSpaceSource
                    + " avatar=" + avatar.GetType().Name
                    + " under=" + space.name
                    + " headBone=" + (head == null ? "NOT FOUND" : head.name)
                    + " poseSpace=" + PoseSpace.name
                    + " handScale=" + Config.handScale
                    + " gripOffset=" + poseProvider.handPositionOffset
                    + " head=" + (poseProvider.head == null ? "NONE" : poseProvider.head.name)
                    + " leftHand=" + Describe(poseProvider.leftHand, poseProvider.leftHandAnchor)
                    + " rightHand=" + Describe(poseProvider.rightHand, poseProvider.rightHandAnchor)
                    + " parts[" + (_partReveal == null ? "no visual controller" : _partReveal.Describe()) + "]");

                AvatarSystemProbe.NotifySpawned(avatar);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error("AVBODY spawn failed: " + ex);
            }
            finally
            {
                _spawning = false;
            }
        }

        private void HandleVisualDataChanged(MultiplayerAvatarsData data)
        {
            if (_partReveal == null) return;

            _partReveal.Apply();
            _preview?.ApplyReveal();

            // Logged because "the id changed" and "the part is now on screen" are different
            // claims, and the prefabs ship these objects switched off -- the reveal is the step
            // between them, so it is the one worth seeing.
            Plugin.Log.Info("AVBODY parts after edit: " + _partReveal.Describe());
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

                Plugin.Log.Info("AVBODY preview shown");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error("AVBODY preview failed: " + ex);
            }
        }

        internal void HidePreview()
        {
            if (_preview == null) return;

            _preview.Dispose();
            _preview = null;
            Plugin.Log.Info("AVBODY preview hidden");
        }

        private static string Describe(VRController controller, Transform anchor)
        {
            if (controller == null) return "none";
            return anchor == null ? "ok(no anchor)" : "ok(" + anchor.name + ")";
        }

        private static Vector3 ParseVector(string value)
        {
            if (string.IsNullOrEmpty(value)) return Vector3.zero;

            string[] parts = value.Split(',');
            if (parts.Length != 3) return Vector3.zero;

            return float.TryParse(parts[0], out float x) &&
                   float.TryParse(parts[1], out float y) &&
                   float.TryParse(parts[2], out float z)
                ? new Vector3(x, y, z)
                : Vector3.zero;
        }
    }
}
