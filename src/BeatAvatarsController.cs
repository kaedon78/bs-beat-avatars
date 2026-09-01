using System;
using System.Collections;
using System.Reflection;
using BeatSaber.AvatarCore;
using BeatSaber.BeatAvatarSDK;
using BeatAvatarSDK = BeatSaber.BeatAvatarSDK;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zenject;

namespace BeatAvatars
{
    /// <summary>
    /// Spawns the player's Beat Avatar into whatever scene they are in, and keeps it spawned.
    ///
    /// Scene names are not used to decide when to act. What matters is the player-space transform:
    /// when it changes, the old avatar died with the old scene. That cannot go stale the way a
    /// list of scene names does.
    /// </summary>
    internal sealed class BeatAvatarsController : MonoBehaviour
    {
        internal static BeatAvatarsController Instance { get; private set; }

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
        private bool _layersSettled;
        private float _lastVisualRecovery = float.NegativeInfinity;
        private int _visualRecoveryAttempts;
        private bool _visualRecoveryAbandoned;
        private const float kVisualRecoveryCooldown = 5f;
        private const int kMaxVisualRecoveries = 2;

        /// <summary>The avatar system to ask the collection for. Same id on 1.40.5 and 1.45.0.</summary>
        private const string kAvatarSystemId = "BeatAvatarSystem";

        /// <summary>
        /// What the shared parts model resolves the player's own ids to -- the question a respawn
        /// cannot answer, since a model handing out empty parts makes every avatar built from it
        /// empty too.
        /// </summary>
        private string PartsModelState()
        {
            var parts = _container?.TryResolve<BeatAvatarSDK.AvatarPartsModel>();
            var model = _container?.TryResolve<BeatAvatarSDK.AvatarDataModel>();
            if (parts == null || model?.avatarData == null) return "partsModel=unavailable";

            // GetById returning null means the id is not in the collection at all, which is a
            // different fault from a part whose mesh was destroyed. Counts tell them apart, and
            // comparing against a freshly scanned container says whether the one cached at
            // start-up has simply gone stale.
            var live = FindLiveContainer();
            var liveParts = live?.TryResolve<BeatAvatarSDK.AvatarPartsModel>();

            return "partsModel hands=" + model.avatarData.handsId
                 + " handsCount=" + parts.handsCollection.count
                 + " clothesCount=" + parts.clothesCollection.count
                 + " headTopCount=" + parts.headTopCollection.count
                 + "; cachedContainerIsLive=" + ReferenceEquals(_container, live)
                 + " livePartsIsSame=" + ReferenceEquals(parts, liveParts)
                 + " livePartsHandsCount=" + (liveParts == null ? -1 : liveParts.handsCollection.count);
        }

        /// <summary>
        /// Whatever the collection actually holds, read reflectively. The id it is keyed by is the
        /// one thing a stall here cannot tell us, and the type exposes no public enumerator.
        /// </summary>
        private string DescribeCollection()
        {
            if (Collection == null) return "null";

            try
            {
                var parts = new System.Collections.Generic.List<string>();

                foreach (FieldInfo field in Collection.GetType()
                             .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    object value = field.GetValue(Collection);
                    if (value is System.Collections.IEnumerable items && !(value is string))
                    {
                        foreach (object item in items)
                            parts.Add(field.Name + "[" + (item == null ? "null" : item.ToString()) + "]");
                    }
                    else
                    {
                        parts.Add(field.Name + "=" + (value == null ? "null" : value.ToString()));
                    }
                }

                return parts.Count == 0 ? "(no fields)" : string.Join(", ", parts.ToArray());
            }
            catch (Exception ex)
            {
                return "unreadable: " + ex.Message;
            }
        }

        private const int kAlwaysVisibleMask = 1 << 10;
        private const int kOnlyInThirdPersonMask = 1 << 3;

        internal BeatAvatarsConfig Config { get; private set; }

        private void Awake()
        {
            Instance = this;
            Config = BeatAvatarsConfig.Load();
        }

        private IEnumerator Start()
        {
            // AvatarSystemCollection is NOT in the project container: AvatarsAsyncInstaller binds
            // it onto AppCoreSceneContext, a scene context and a child of ProjectContext, so
            // resolving from the project container returns null forever. Scan the contexts instead;
            // a child container resolves everything its parents bind, so whichever one answers is
            // also a container the avatar prefab can be injected with.
            Plugin.Log.Info("Looking for the avatar system.");

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

            Plugin.Log.Info("Avatar system collection resolved after " + waited + "s.");

            while (AvatarSystem == null)
            {
                AvatarSystem = Collection.GetAvatarSystem(kAvatarSystemId);
                if (AvatarSystem != null) break;

                // The same rule the loop above follows: never spin in silence. Without this the
                // whole mod stalls here with no output at all, which reads as "the plugin did not
                // load" -- a different fault needing a different fix.
                if (waited % 15f == 0f)
                    Plugin.Log.Warn("No \"" + kAvatarSystemId + "\" avatar system after " + waited
                        + "s. Collection holds: " + DescribeCollection());

                yield return new WaitForSeconds(0.5f);
                waited += 0.5f;
            }

            Plugin.Log.Info("Avatar system \"" + kAvatarSystemId + "\" ready after " + waited + "s.");

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

            if (space != SpawnedUnder)
            {
                _visualRecoveryAttempts = 0;
                _visualRecoveryAbandoned = false;
            }

            // Do not spawn into a scene that has no camera yet -- the pose provider would have
            // nothing to follow and the avatar would sit frozen at its origin.
            if (Camera.main == null) return;

            if (!_spawning && (CurrentAvatar == null || SpawnedUnder != space)) SpawnAsync(space);

            if (CurrentAvatar == null) return;

            // Re-resolve whatever the scene has since rebuilt. Cheap while everything is present:
            // EnsureRig only scans when something is actually missing.
            _poseProvider?.EnsureRig();
            RecoverLostVisuals();

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
            bool cameraFixed = hmdCamera != null && AvatarLayers.ApplyCameraMask(hmdCamera, true);
            bool mirrorFixed = AvatarLayers.AddToMirrorMask() > 0;

            // URP filters layers again after the camera does, so a layer absent from the renderer's
            // masks renders on no camera at all.
            bool pipelineFixed = AvatarLayers.EnsureRenderPipelineLayers();

            ReportLayerReset(cameraFixed, mirrorFixed, pipelineFixed);
        }

        /// <summary>
        /// Respawns the avatar when its meshes have gone out from under it.
        ///
        /// A safety net, not the fix: the cause of that was a stale container, handled in
        /// SpawnAsync. It stays because the failure is silent -- the avatar tracks perfectly and
        /// wears nothing -- and because the respawn re-resolves the container, so it can heal a
        /// case this does not yet know about. Capped: an avatar that comes back empty would
        /// otherwise respawn forever.
        /// </summary>
        private void RecoverLostVisuals()
        {
            if (_spawning || CurrentAvatar == null) return;
            if (Time.realtimeSinceStartup - _lastVisualRecovery < kVisualRecoveryCooldown) return;
            if (!AvatarBones.HasLostVisuals(CurrentAvatar)) return;

            // A reload that comes back empty proves the meshes are gone from the SOURCE, not from
            // this instance, so retrying cannot help. Give up loudly rather than churn Addressable
            // instantiations forever.
            if (_visualRecoveryAttempts >= kMaxVisualRecoveries)
            {
                if (!_visualRecoveryAbandoned)
                {
                    _visualRecoveryAbandoned = true;
                    Plugin.Log.Error("Avatar meshes are gone and reloading does not bring them back, "
                        + "so the assets themselves have been released. Giving up until the next scene. "
                        + PartsModelState());
                }

                return;
            }

            _lastVisualRecovery = Time.realtimeSinceStartup;
            _visualRecoveryAttempts++;
            Plugin.Log.Warn("Avatar lost its meshes; respawning it (attempt "
                + _visualRecoveryAttempts + "). " + PartsModelState());

            Transform space = SpawnedUnder;
            Despawn();
            if (space != null) SpawnAsync(space);
        }

        /// <summary>
        /// Says so, once, when something outside this mod resets the layer state it depends on.
        ///
        /// Silent in steady state, since the appliers only report a value that was actually wrong.
        /// Blind to a reset that arrives WITH a respawn, because the first pass after one is
        /// legitimately set-up -- so this catches a mid-scene reset and nothing else.
        /// </summary>
        private void ReportLayerReset(bool camera, bool mirror, bool pipeline)
        {
            if (!camera && !mirror && !pipeline) return;

            // The first application after a spawn is not a reset, it is set-up.
            if (!_layersSettled)
            {
                _layersSettled = true;
                return;
            }

            // The values, not just the fact. "Only the head stays" is an inversion -- the head is
            // on the layer meant to be culled from the player's own view and the body and hands are
            // on the one meant to be visible -- and no reading of the code explains it, so the
            // actual numbers at the moment of the reset are what is needed.
            Camera main = Camera.main;
            BeatSaber.BeatAvatarSDK.BeatAvatarPoseController bones = AvatarBones.PoseController(CurrentAvatar);
            Transform head = AvatarBones.Head(bones);
            Transform body = AvatarBones.Body(bones);

            Plugin.Log.Warn("Avatar layers were reset and re-applied ("
                + (camera ? "camera " : "") + (mirror ? "mirror " : "") + (pipeline ? "pipeline" : "") + "). "
                + "mainCamera=" + (main == null ? "none" : main.name + " mask=0x" + main.cullingMask.ToString("X8")
                    + " avatar10=" + ((main.cullingMask & kAlwaysVisibleMask) != 0)
                    + " head3=" + ((main.cullingMask & kOnlyInThirdPersonMask) != 0))
                + "; urpOpaque=0x" + AvatarLayers.LastOpaqueMask.ToString("X8")
                + "; avatarRoot=" + CurrentAvatar.gameObject.layer
                + " bodyLayer=" + (body == null ? -1 : body.gameObject.layer)
                + " headLayer=" + (head == null ? -1 : head.gameObject.layer)
                + " bodyActive=" + (body != null && body.gameObject.activeInHierarchy)
                + " cameras=" + Camera.allCamerasCount);
        }

        /// <summary>
        /// Points this component at the container that is live NOW, re-resolving everything taken
        /// from it. Cheap and rare: called once per spawn, which is once per scene.
        /// </summary>
        private bool RefreshContainer()
        {
            DiContainer live = FindLiveContainer();
            if (live == null) return _container != null;

            if (!ReferenceEquals(live, _container))
            {
                _container = live;
                Collection = live.TryResolve<AvatarSystemCollection>();
                AvatarSystem = Collection?.GetAvatarSystem(kAvatarSystemId);
            }

            return AvatarSystem != null;
        }

        /// <summary>The container currently able to resolve the avatar system, rescanned fresh.</summary>
        private static DiContainer FindLiveContainer()
        {
            foreach (Context context in FindObjectsByType<Context>(FindObjectsSortMode.None))
            {
                DiContainer container = context.Container;
                if (container?.TryResolve<AvatarSystemCollection>() != null) return container;
            }

            return null;
        }

        /// <summary>
        /// VRCenterAdjust is the room-offset transform the game applies the player's height and
        /// position settings to. The main camera's parent covers scenes that have none.
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

                // Re-resolve BEFORE instantiating. The container captured at start-up does not
                // survive a context rebuild -- applying anything in the game's Settings replaces
                // it -- and a stale one still resolves, which is what makes this so quiet. The
                // avatar comes out fully formed and correctly tracked, injected with a parts model
                // built from destroyed ScriptableObjects: the collections still have their counts,
                // every id lookup misses, and every mesh lands null.
                if (!RefreshContainer())
                {
                    Plugin.Log.Warn("No live container to spawn the avatar with.");
                    return;
                }

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

                Transform head = AvatarLayers.Apply(avatar, true);

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
                poseProvider.handPositionOffset = BeatAvatarsConfig.Offset.ToVector3(Config.handPositionOffset);
                poseProvider.handRotationOffset = BeatAvatarsConfig.Offset.ToVector3(Config.handRotationOffset);
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
                _layersSettled = false;

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
        /// One line per scene, once the rig has settled.
        ///
        /// Not logged at spawn: the scene is still being built then, so a spawn-time reading
        /// reports MISSING for hands that resolve a tick later and are fine. That cries fault when
        /// there is none, and buries the case where there is one.
        /// </summary>
        private IEnumerator ReportRigWhenSettled(LocalPlayerPoseProvider provider, string scene)
        {
            yield return new WaitForSeconds(3f);

            // A scene change during the wait replaces the provider; that spawn reports for itself.
            if (provider == null || _poseProvider != provider) yield break;

            if (provider.head != null && provider.leftHand != null && provider.rightHand != null)
            {
                Plugin.Log.Info("Avatar in " + scene + ": tracking ok. " + DescribeVisibility());
                yield break;
            }

            Plugin.Log.Warn("Avatar tracking in " + scene + " is incomplete: head "
                + (provider.head != null ? "ok" : "MISSING") + ", hands "
                + (provider.leftHand != null ? "ok" : "MISSING") + "/"
                + (provider.rightHand != null ? "ok" : "MISSING")
                + ". The avatar will not follow you properly. " + DescribeVisibility());
        }

        /// <summary>
        /// What the avatar is actually wearing -- the one piece of state that has gone wrong in
        /// service, and which nothing else in the log would show. Layers, masks, activation and
        /// scale were checked the same way and were never at fault, so they are not reported.
        /// </summary>
        private string DescribeVisibility()
        {
            return AvatarBones.DescribeMeshes(CurrentAvatar);
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
                _poseProvider.handPositionOffset = BeatAvatarsConfig.Offset.ToVector3(Config.handPositionOffset);
                _poseProvider.handRotationOffset = BeatAvatarsConfig.Offset.ToVector3(Config.handRotationOffset);
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
                    BeatAvatarsConfig.Offset.ToVector3(Config.previewPosition),
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
