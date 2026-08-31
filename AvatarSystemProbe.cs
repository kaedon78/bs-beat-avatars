using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using BeatSaber.AvatarCore;
using BeatSaber.BeatAvatarAdapter;
using BeatSaber.BeatAvatarSDK;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BeatAvatarBody
{
    /// <summary>
    /// One-shot diagnostic pass, enabled with BSMU_AVATARPROBE=1.
    ///
    /// It answers the questions the layer work depends on and that the DLLs cannot: what layer the
    /// avatar prefab actually instantiates on, what its renderer hierarchy is, what the cameras
    /// and the mirror cull, and whether the player's saved AvatarData has loaded by the time the
    /// system is asked for it.
    ///
    /// Output goes to a FILE, not only to the BSIPA log, because the log drops lines under a fast
    /// burst (CLAUDE.md) and a hierarchy dump is exactly such a burst -- a truncated dump would
    /// read as a short hierarchy rather than as a lost one. The log gets a summary and the path.
    /// </summary>
    internal sealed class AvatarSystemProbe : MonoBehaviour
    {
        private static AvatarSystemProbe _instance;

        private int _dumpIndex;
        private bool _environmentDumped;

        internal static void NotifySpawned(Avatar avatar)
        {
            if (_instance != null) _instance.DumpAvatar(avatar);
        }

        private void Awake() => _instance = this;

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        private IEnumerator Start()
        {
            // The environment dump wants the menu built, not just the app initialised.
            yield return new WaitForSeconds(8f);

            while (BeatAvatarBodyController.Instance == null ||
                   BeatAvatarBodyController.Instance.AvatarSystem == null)
            {
                yield return new WaitForSeconds(0.5f);
            }

            DumpEnvironment(BeatAvatarBodyController.Instance);

            StartCoroutine(TrackingHeartbeat());
        }

        /// <summary>
        /// One line every five seconds saying where the avatar thinks the player is.
        ///
        /// A gameplay run cannot be driven by the harness -- the operator plays it -- so the log
        /// has to carry the evidence instead of a screenshot. Frozen poses and a live avatar look
        /// identical in a single frame; consecutive samples do not. Deliberately 0.2 Hz and a
        /// single formatted line: the diagnostics that made maps unplayable in this repo were the
        /// ones doing per-frame work.
        /// </summary>
        private IEnumerator TrackingHeartbeat()
        {
            var lastScene = string.Empty;

            while (true)
            {
                yield return new WaitForSeconds(5f);

                BeatAvatarBodyController controller = BeatAvatarBodyController.Instance;
                if (controller == null || controller.CurrentAvatar == null) continue;

                LocalPlayerPoseProvider pose = controller.PoseProvider;
                if (pose == null) continue;

                string scene = SceneManager.GetActiveScene().name;
                AvatarPoseData current = pose.currentPose;

                Plugin.Log.Info("AVBODY track scene=" + scene
                    + (scene == lastScene ? "" : " (SCENE CHANGED)")
                    + " space=" + BeatAvatarBodyController.PlayerSpaceSource
                    + " head=" + current.headPose.position.ToString("F2")
                    + " L=" + current.leftHandPose.position.ToString("F2")
                    + " R=" + current.rightHandPose.position.ToString("F2")
                    + " handsResolved=" + (pose.leftHand != null) + "/" + (pose.rightHand != null)
                    + " headRef=" + (pose.head == null ? "NONE" : pose.head.name));

                lastScene = scene;
            }
        }

        private void DumpEnvironment(BeatAvatarBodyController controller)
        {
            if (_environmentDumped) return;
            _environmentDumped = true;

            var sb = new StringBuilder();
            sb.AppendLine("=== BeatAvatarBody environment probe ===");
            sb.AppendLine("time=" + DateTime.Now.ToString("s"));
            sb.AppendLine();

            sb.AppendLine("--- layers ---");
            for (var i = 0; i < 32; i++)
            {
                string name = LayerMask.LayerToName(i);
                if (!string.IsNullOrEmpty(name)) sb.AppendLine("  " + i + "  " + name);
            }
            sb.AppendLine();

            sb.AppendLine("--- cameras ---");
            // FindObjectsByType including inactive, NOT Camera.allCameras: Camera2 keeps its
            // cameras disabled and renders them by hand, so allCameras cannot see them and the
            // masks we most need to read are exactly the ones it would miss. Layer 6 is reported
            // because Camera2 uses it for "avatar visible in a first-person view".
            foreach (Camera camera in FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                sb.AppendLine("  " + Path(camera.transform)
                    + " tag=" + camera.tag
                    + " enabled=" + camera.enabled
                    + " depth=" + camera.depth
                    + " cullingMask=0x" + camera.cullingMask.ToString("X8")
                    + " avatar(10)=" + Bit(camera.cullingMask, AvatarLayers.kAlwaysVisible)
                    + " thirdPerson(3)=" + Bit(camera.cullingMask, AvatarLayers.kOnlyInThirdPerson)
                    + " cam2FirstPerson(6)=" + Bit(camera.cullingMask, 6)
                    + " mirror(7)=" + Bit(camera.cullingMask, AvatarLayers.kMirror));
            }
            sb.AppendLine();

            sb.AppendLine("--- URP renderer layer masks ---");
            // The filter a camera-level census cannot see. If layer 3 is missing from these, the
            // head renders on NO camera, whatever any culling mask says.
            AvatarLayers.EnsureRenderPipelineLayers(false);
            if (!AvatarLayers.RenderPipelineOriginalsCaptured)
            {
                sb.AppendLine("  no UniversalRenderPipelineAsset found");
            }
            else
            {
                sb.AppendLine("  ORIGINAL opaque=0x" + AvatarLayers.OriginalOpaqueMask.ToString("X8")
                    + " avatar(10)=" + Bit(AvatarLayers.OriginalOpaqueMask, AvatarLayers.kAlwaysVisible)
                    + " thirdPerson(3)=" + Bit(AvatarLayers.OriginalOpaqueMask, AvatarLayers.kOnlyInThirdPerson)
                    + " cam2FirstPerson(6)=" + Bit(AvatarLayers.OriginalOpaqueMask, 6));
                sb.AppendLine("  ORIGINAL transparent=0x" + AvatarLayers.OriginalTransparentMask.ToString("X8")
                    + " avatar(10)=" + Bit(AvatarLayers.OriginalTransparentMask, AvatarLayers.kAlwaysVisible)
                    + " thirdPerson(3)=" + Bit(AvatarLayers.OriginalTransparentMask, AvatarLayers.kOnlyInThirdPerson)
                    + " cam2FirstPerson(6)=" + Bit(AvatarLayers.OriginalTransparentMask, 6));
                sb.AppendLine("  CURRENT  opaque=0x" + AvatarLayers.CurrentOpaqueMask.ToString("X8")
                    + " transparent=0x" + AvatarLayers.CurrentTransparentMask.ToString("X8"));
            }
            sb.AppendLine();

            sb.AppendLine("--- mirrors ---");
            FieldInfo reflectLayers = typeof(MirrorRendererSO)
                .GetField("_reflectLayers", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (MirrorRendererSO renderer in Resources.FindObjectsOfTypeAll<MirrorRendererSO>())
            {
                var mask = (LayerMask)reflectLayers.GetValue(renderer);
                sb.AppendLine("  " + renderer.name
                    + " type=" + renderer.mirrorType
                    + " reflectLayers=0x" + mask.value.ToString("X8")
                    + " avatar(10)=" + Bit(mask.value, AvatarLayers.kAlwaysVisible)
                    + " thirdPerson(3)=" + Bit(mask.value, AvatarLayers.kOnlyInThirdPerson)
                    + " mirror(7)=" + Bit(mask.value, AvatarLayers.kMirror));
            }
            sb.AppendLine();

            sb.AppendLine("--- VR controllers ---");
            // The first run reported leftHand=none rightHand=none while the hands still rendered,
            // because the pose provider fell back to a rest pose. That is a resolution failure
            // wearing a working avatar's clothes, so census the controllers rather than infer.
            foreach (VRController vrController in FindObjectsByType<VRController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                sb.AppendLine("  " + Path(vrController.transform)
                    + " node=" + vrController.node
                    + " nodeIndex=" + vrController.nodeIndex
                    + " activeInHierarchy=" + vrController.gameObject.activeInHierarchy
                    + " enabled=" + vrController.enabled
                    + " active(prop)=" + vrController.active
                    + " poseValid=" + vrController.poseValid
                    + " mouseMode=" + vrController.mouseMode);
            }
            sb.AppendLine();

            sb.AppendLine("--- avatar systems ---");
            // availableAvatarSystems is internal; selectableAvatarSystems is the public view and
            // is the one a user could pick from anyway.
            foreach (IAvatarSystemMetadata metadata in controller.Collection.selectableAvatarSystems)
                sb.AppendLine("  selectable: " + metadata.typeIdentifier.value + " hash=" + metadata.typeIdentifier.hash);
            sb.AppendLine();

            string path = Write("avatar-environment", sb.ToString());
            Plugin.Log.Info("AVBODY probe wrote environment dump: " + path);

            DumpAvatarDataAsync(controller.AvatarSystem);

            DumpPartsModelAsync(controller);

            if (Environment.GetEnvironmentVariable("BSMU_AVATARPROBE_PREFABS") == "1")
                SurveyPrefabsAsync(controller);

            if (Environment.GetEnvironmentVariable("BSMU_AVATARPROBE_PARTS") == "1")
                SurveyPartRevealAsync(controller);
        }

        /// <summary>
        /// Proves BeatAvatarPartReveal against an avatar that actually HAS glasses and facial hair.
        ///
        /// The operator's saved avatar has both set to None, so running the reveal against it
        /// cannot distinguish "works" from "does nothing" -- the correct outcome is invisible
        /// either way. This builds a synthetic AvatarData with the first non-empty entry from each
        /// collection and applies it to a throwaway avatar parked under the floor. The saved data
        /// is never touched.
        /// </summary>
        private async void SurveyPartRevealAsync(BeatAvatarBodyController controller)
        {
            GameObject parkingLot = null;
            Avatar avatar = null;

            try
            {
                var partsModel = controller.Container.TryResolve<AvatarPartsModel>();
                if (partsModel == null)
                {
                    Plugin.Log.Warn("AVBODY part-reveal probe: no AvatarPartsModel");
                    return;
                }

                AvatarData data = (await controller.AvatarSystem.GetMultiplayerAvatarsData()).CreateAvatarData().Clone();

                string glassesId = FirstNonEmptyMesh(partsModel.glassesCollection);
                string facialHairId = FirstNonEmptyMesh(partsModel.facialHairCollection);
                if (glassesId != null) data.glassesId = glassesId;
                if (facialHairId != null) data.facialHairId = facialHairId;

                var sb = new StringBuilder();
                sb.AppendLine("=== BeatAvatarBody part reveal ===");
                sb.AppendLine("time=" + DateTime.Now.ToString("s"));
                sb.AppendLine("synthetic glassesId=" + Describe(glassesId) + " facialHairId=" + Describe(facialHairId));
                sb.AppendLine("(the saved avatar has both set to None; this data is synthetic and never saved)");
                sb.AppendLine();

                parkingLot = new GameObject("BeatAvatarBodyPartReveal");
                parkingLot.transform.position = new Vector3(0f, -1000f, 0f);

                avatar = await controller.AvatarSystem.InstantiateAvatar(
                    AvatarDisplayContext.MultiplayerGameplay, 0, controller.Container);
                avatar.transform.SetParent(parkingLot.transform, false);

                var visualData = new MultiplayerAvatarsData(
                    new System.Collections.Generic.List<MultiplayerAvatarData> { data.CreateMultiplayerAvatarsData() },
                    controller.AvatarSystem.supportedOptionalAvatarDataTypes);

                avatar.SetVisualDataProvider(new StaticAvatarVisualDataProvider(visualData));
                await Task.Delay(250);

                sb.AppendLine("-- visual data applied, BEFORE the reveal --");
                AppendHierarchy(sb, avatar.transform, 0);
                sb.AppendLine();

                var visualController = avatar.GetComponentInChildren<BeatAvatarVisualController>(true);
                var reveal = avatar.gameObject.AddComponent<BeatAvatarPartReveal>();
                reveal.Bind(visualController);
                reveal.Apply();
                await Task.Delay(250);

                sb.AppendLine("-- AFTER the reveal --");
                sb.AppendLine("reveal reported: " + reveal.Describe());
                AppendHierarchy(sb, avatar.transform, 0);

                string path = Write("avatar-partreveal", sb.ToString());
                Plugin.Log.Info("AVBODY probe wrote part reveal dump: " + path);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error("AVBODY part-reveal probe failed: " + ex);
            }
            finally
            {
                if (avatar != null) Destroy(avatar.gameObject);
                if (parkingLot != null) Destroy(parkingLot);
            }
        }

        private static string FirstNonEmptyMesh(AvatarPartCollection<AvatarMeshPartSO> collection)
        {
            foreach (AvatarMeshPartSO part in collection.parts)
                if (part.mesh != null) return part.id;
            return null;
        }

        /// <summary>
        /// Dumps every avatar part collection against the ids in the player's save data.
        ///
        /// The prefab survey showed all three prefabs ship Mouth, Glasses and FacialHair inactive,
        /// which explains why no mouth renders -- but not why the mouth SPRITE is null after
        /// UpdateAvatarVisual assigned it, nor why an eyes id of Eyes11 yields a sprite asset named
        /// Eyes4. GetById falls back to GetDefault on a miss, so a failed lookup and a
        /// deliberately-empty part look identical from outside. Printing the collection says which.
        /// </summary>
        private async void DumpPartsModelAsync(BeatAvatarBodyController controller)
        {
            try
            {
                var partsModel = controller.Container.TryResolve<AvatarPartsModel>();
                if (partsModel == null)
                {
                    Plugin.Log.Warn("AVBODY probe could not resolve AvatarPartsModel");
                    return;
                }

                AvatarData data = (await controller.AvatarSystem.GetMultiplayerAvatarsData()).CreateAvatarData();

                var sb = new StringBuilder();
                sb.AppendLine("=== BeatAvatarBody parts model ===");
                sb.AppendLine("time=" + DateTime.Now.ToString("s"));
                sb.AppendLine();

                AppendSpriteCollection(sb, "eyes", partsModel.eyesCollection, data.eyesId);
                AppendSpriteCollection(sb, "mouth", partsModel.mouthCollection, data.mouthId);
                AppendMeshCollection(sb, "headTop", partsModel.headTopCollection, data.headTopId);
                AppendMeshCollection(sb, "glasses", partsModel.glassesCollection, data.glassesId);
                AppendMeshCollection(sb, "facialHair", partsModel.facialHairCollection, data.facialHairId);
                AppendMeshCollection(sb, "hands", partsModel.handsCollection, data.handsId);
                AppendMeshCollection(sb, "clothes", partsModel.clothesCollection, data.clothesId);

                string path = Write("avatar-parts", sb.ToString());
                Plugin.Log.Info("AVBODY probe wrote parts model dump: " + path);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error("AVBODY probe parts model dump failed: " + ex);
            }
        }

        private static void AppendSpriteCollection(StringBuilder sb, string label,
            AvatarPartCollection<AvatarSpritePartSO> collection, string chosenId)
        {
            AvatarSpritePartSO hit = collection.GetById(chosenId);
            AvatarSpritePartSO used = hit ?? collection.GetDefault();

            sb.AppendLine("--- " + label + " --- saved id=" + Describe(chosenId)
                + " count=" + collection.count
                + " lookup=" + (hit != null ? "HIT" : "MISS (fell back to GetDefault)")
                + " usedId=" + (used == null ? "null" : used.id)
                + " usedSprite=" + (used == null || used.sprite == null ? "null" : used.sprite.name));
            foreach (AvatarSpritePartSO part in collection.parts)
                sb.AppendLine("    id=" + part.id + " sprite=" + (part.sprite == null ? "null" : part.sprite.name));
            sb.AppendLine();
        }

        private static void AppendMeshCollection(StringBuilder sb, string label,
            AvatarPartCollection<AvatarMeshPartSO> collection, string chosenId)
        {
            AvatarMeshPartSO hit = collection.GetById(chosenId);
            AvatarMeshPartSO used = hit ?? collection.GetDefault();

            sb.AppendLine("--- " + label + " --- saved id=" + Describe(chosenId)
                + " count=" + collection.count
                + " lookup=" + (hit != null ? "HIT" : "MISS (fell back to GetDefault)")
                + " usedId=" + (used == null ? "null" : used.id)
                + " usedMesh=" + (used == null || used.mesh == null ? "null" : used.mesh.name));
            foreach (AvatarMeshPartSO part in collection.parts)
                sb.AppendLine("    id=" + part.id + " mesh=" + (part.mesh == null ? "null" : part.mesh.name));
            sb.AppendLine();
        }

        /// <summary>
        /// Instantiates every AvatarDisplayContext and dumps what came back, twice: as the prefab
        /// ships, and again after the player's own AvatarData is applied.
        ///
        /// Two dumps because one cannot tell them apart. The gameplay prefab has Mouth, Glasses and
        /// FacialHair inactive with a null mouth sprite even though the player's data names a
        /// mouth -- and UpdateAvatarVisual assigns all of them unconditionally, so the question is
        /// whether the prefab ships that way or the visual update turned them off. The pre-visual
        /// dump answers it.
        ///
        /// Gated behind BSMU_AVATARPROBE_PREFABS=1: it loads and destroys six avatars, which is a
        /// heavier intervention than the rest of the probe and does not belong in every run.
        /// </summary>
        private async void SurveyPrefabsAsync(BeatAvatarBodyController controller)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== BeatAvatarBody prefab survey ===");
            sb.AppendLine("time=" + DateTime.Now.ToString("s"));
            sb.AppendLine();
            sb.AppendLine("BeatAvatarSystem.InstantiateAvatar switches on the display context:");
            sb.AppendLine("  MultiplayerBigAvatar -> avatarHologramPrefab");
            sb.AppendLine("  MultiplayerGameplay  -> avatarGameplayPrefab");
            sb.AppendLine("  MultiplayerResults   -> avatarResultsPrefab");
            sb.AppendLine("  everything else      -> avatarGameplayPrefab (the switch's default arm)");
            sb.AppendLine();
            sb.AppendLine("So six enum values reach at most three distinct prefabs. The clone name");
            sb.AppendLine("below is what actually came back, not what the switch claims.");
            sb.AppendLine();

            // Park the survey avatars well below the floor: they are instantiated live and must not
            // appear in a capture taken during the run.
            var parkingLot = new GameObject("BeatAvatarBodyPrefabSurvey");
            parkingLot.transform.position = new Vector3(0f, -1000f, 0f);

            LocalAvatarVisualProvider visualProvider = null;

            try
            {
                visualProvider = await LocalAvatarVisualProvider.CreateAsync(controller.AvatarSystem, controller.Container.TryResolve<AvatarDataModel>());

                foreach (AvatarDisplayContext context in Enum.GetValues(typeof(AvatarDisplayContext)))
                {
                    sb.AppendLine("################  " + context + "  ################");

                    Avatar avatar = null;
                    try
                    {
                        avatar = await controller.AvatarSystem.InstantiateAvatar(context, 0, controller.Container);
                        if (avatar == null)
                        {
                            sb.AppendLine("  InstantiateAvatar returned null");
                            sb.AppendLine();
                            continue;
                        }

                        avatar.transform.SetParent(parkingLot.transform, false);

                        sb.AppendLine("clone=" + avatar.gameObject.name);
                        sb.AppendLine("type=" + avatar.GetType().FullName);
                        sb.AppendLine("rootLayer=" + avatar.gameObject.layer
                            + " (" + LayerMask.LayerToName(avatar.gameObject.layer) + ")");
                        sb.AppendLine("rootComponents=" + Components(avatar.gameObject));
                        sb.AppendLine();

                        sb.AppendLine("-- as instantiated, before any visual data --");
                        AppendHierarchy(sb, avatar.transform, 0);
                        sb.AppendLine();

                        avatar.SetVisualDataProvider(visualProvider);

                        // Let the frame turn over before reading. The gameplay prefab settles
                        // immediately (a dump 6 s later was byte-identical), but proving that for
                        // one prefab is not proving it for all of them.
                        await Task.Delay(250);

                        sb.AppendLine("-- after the player's own AvatarData is applied --");
                        AppendHierarchy(sb, avatar.transform, 0);
                        sb.AppendLine();
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine("  FAILED: " + ex);
                        sb.AppendLine();
                    }
                    finally
                    {
                        if (avatar != null) Destroy(avatar.gameObject);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error("AVBODY prefab survey failed: " + ex);
                sb.AppendLine("SURVEY ABORTED: " + ex);
            }
            finally
            {
                visualProvider?.Dispose();
                Destroy(parkingLot);
            }

            string path = Write("avatar-prefabs", sb.ToString());
            Plugin.Log.Info("AVBODY probe wrote prefab survey: " + path);
        }

        private static string Components(GameObject go)
        {
            var names = new System.Collections.Generic.List<string>();
            foreach (Component component in go.GetComponents<Component>())
                names.Add(component == null ? "<missing>" : component.GetType().Name);
            return string.Join(", ", names.ToArray());
        }

        /// <summary>
        /// Answers the second open question: whether the player's saved avatar has loaded by the
        /// time we ask. AvatarDataModel.Init fires LoadAsync fire-and-forget, so avatarData can be
        /// null right after the binding resolves; avatarCreated is the gate that says otherwise.
        /// </summary>
        private async void DumpAvatarDataAsync(IAvatarSystem system)
        {
            try
            {
                var metadata = system as IAvatarSystemMetadata;
                bool created = metadata != null && await metadata.avatarCreated;

                MultiplayerAvatarData packed = await system.GetMultiplayerAvatarsData();
                AvatarData data = packed.CreateAvatarData();

                Plugin.Log.Info("AVBODY probe avatarCreated=" + created
                    + " packedBytes=" + (packed.data == null ? -1 : packed.data.Length)
                    + " typeHash=" + packed.avatarTypeIdentifierHash);

                Plugin.Log.Info("AVBODY probe avatarData"
                    + " headTop=" + Describe(data.headTopId)
                    + " glasses=" + Describe(data.glassesId)
                    + " facialHair=" + Describe(data.facialHairId)
                    + " hands=" + Describe(data.handsId)
                    + " clothes=" + Describe(data.clothesId)
                    + " eyes=" + Describe(data.eyesId)
                    + " mouth=" + Describe(data.mouthId)
                    + " skin=" + Describe(data.skinColorId));
            }
            catch (Exception ex)
            {
                Plugin.Log.Error("AVBODY probe avatar data read failed: " + ex);
            }
        }

        /// <summary>
        /// Dumps the spawned avatar's whole hierarchy: every transform's path, layer, active state
        /// and the renderers on it. This is the measurement that decides which objects the
        /// first-person pass has to move, and it is only available at runtime -- the avatar is an
        /// Addressable prefab, invisible to any static read of the assemblies.
        /// </summary>
        private void DumpAvatar(Avatar avatar)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== BeatAvatarBody spawned avatar ===");
            sb.AppendLine("type=" + avatar.GetType().FullName);
            sb.AppendLine("root=" + Path(avatar.transform));
            sb.AppendLine("rootLayer=" + avatar.gameObject.layer + " (" + LayerMask.LayerToName(avatar.gameObject.layer) + ")");

            var poseController = avatar.GetComponentInChildren<BeatAvatarPoseController>(true);
            if (poseController != null)
            {
                sb.AppendLine("poseController=" + Path(poseController.transform));
                foreach (string field in new[] { "_headTransform", "_leftHandTransform", "_rightHandTransform", "_bodyTransform" })
                {
                    FieldInfo info = typeof(BeatAvatarPoseController)
                        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
                    var bone = info?.GetValue(poseController) as Transform;
                    sb.AppendLine("  " + field + " = " + (bone == null ? "null" : Path(bone))
                        + (bone != null && bone.parent != null ? "   parent=" + Path(bone.parent) : ""));
                }
            }
            else
            {
                sb.AppendLine("poseController=NOT FOUND");
            }

            sb.AppendLine();
            sb.AppendLine("--- hierarchy (path | layer | active | renderers) ---");
            AppendHierarchy(sb, avatar.transform, 0);

            string path = Write("avatar-hierarchy-" + (++_dumpIndex), sb.ToString());
            Plugin.Log.Info("AVBODY probe wrote hierarchy dump: " + path);

            // The first dump is taken the instant the providers are attached. Some parts read as
            // inactive with a null mesh there (Mouth, Glasses, FacialHair) while the player's own
            // data names a mouth, so take a second reading once the scene has settled rather than
            // conclude anything from the first.
            if (_dumpIndex == 1) StartCoroutine(DumpLater(avatar, 6f));
        }

        private IEnumerator DumpLater(Avatar avatar, float seconds)
        {
            yield return new WaitForSeconds(seconds);
            if (avatar != null) DumpAvatar(avatar);
        }

        private static void AppendHierarchy(StringBuilder sb, Transform transform, int depth)
        {
            sb.Append(new string(' ', depth * 2))
              .Append(transform.name)
              .Append(" | layer=").Append(transform.gameObject.layer)
              .Append(':').Append(LayerMask.LayerToName(transform.gameObject.layer))
              .Append(" | active=").Append(transform.gameObject.activeSelf);

            var renderer = transform.GetComponent<Renderer>();
            if (renderer != null)
            {
                sb.Append(" | ").Append(renderer.GetType().Name)
                  .Append(" enabled=").Append(renderer.enabled)
                  .Append(" materials=").Append(renderer.sharedMaterials.Length);

                var spriteRenderer = renderer as SpriteRenderer;
                if (spriteRenderer != null)
                    sb.Append(" sprite=").Append(spriteRenderer.sprite == null ? "null" : spriteRenderer.sprite.name);
            }

            var meshFilter = transform.GetComponent<MeshFilter>();
            if (meshFilter != null)
                sb.Append(" | mesh=").Append(meshFilter.sharedMesh == null ? "null" : meshFilter.sharedMesh.name);

            sb.AppendLine();

            for (var i = 0; i < transform.childCount; i++)
                AppendHierarchy(sb, transform.GetChild(i), depth + 1);
        }

        private static string Describe(string id) => string.IsNullOrEmpty(id) ? "<empty>" : id;

        private static string Bit(int mask, int layer) => (mask & (1 << layer)) != 0 ? "yes" : "no";

        private static string Path(Transform transform)
        {
            var sb = new StringBuilder(transform.name);
            for (Transform parent = transform.parent; parent != null; parent = parent.parent)
                sb.Insert(0, parent.name + "/");
            return sb.ToString();
        }

        private static string Write(string name, string contents)
        {
            string directory = System.IO.Path.Combine(Application.dataPath, "..", "Logs", "probe");
            Directory.CreateDirectory(directory);

            string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, name + ".txt"));
            File.WriteAllText(path, contents);
            return path;
        }
    }
}
