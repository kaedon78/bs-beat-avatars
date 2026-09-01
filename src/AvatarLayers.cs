using System;
using System.Reflection;
using BeatSaber.AvatarCore;
using BeatSaber.BeatAvatarSDK;
using UnityEngine;
using UnityEngine.Rendering;

namespace BeatAvatars
{
    /// <summary>
    /// Layer bookkeeping, which is where the avatar as your own body lives or dies: this is a
    /// multiplayer avatar, and nothing about it expects to be inside the local player's head.
    ///
    /// The numbers are the game's own -- 10 is "Avatar", 3 and 7 are unnamed and free -- and match
    /// what CustomAvatars and Camera2 already use, which is what keeps mods that cull layer 3, or
    /// re-add it for a third-person view, compatible with this one.
    /// </summary>
    internal static class AvatarLayers
    {
        /// <summary>Layer 10, "Avatar" -- rendered by every camera including your own.</summary>
        internal const int kAlwaysVisible = 10;

        /// <summary>Layer 3, unnamed -- culled from the HMD camera, so heads exist only for other cameras.</summary>
        internal const int kOnlyInThirdPerson = 3;

        /// <summary>Layer 7, unnamed -- reflected by the mirror.</summary>
        internal const int kMirror = 7;

        internal const int kAlwaysVisibleMask = 1 << kAlwaysVisible;
        internal const int kOnlyInThirdPersonMask = 1 << kOnlyInThirdPerson;
        internal const int kMirrorMask = 1 << kMirror;

        private static FieldInfo _rendererDataListField;
        private static PropertyInfo _opaqueMaskProperty;
        private static PropertyInfo _transparentMaskProperty;
        private static Type _rendererDataType;
        private static bool _reportedPipeline;

        private static readonly FieldInfo kReflectLayersField = typeof(MirrorRendererSO)
            .GetField("_reflectLayers", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// Puts the avatar on the Avatar layer and, if asked, the head bone's whole subtree on the
        /// third-person-only layer. The player's own avatar always hides its head -- seeing it from
        /// inside is never wanted -- while the tuning mirror passes false so it keeps one.
        ///
        /// The head comes from the pose controller's own _headTransform, not from renderer names:
        /// the SDK names five head part fields and the head mesh is not among them, so a
        /// field-by-field pass leaves a face floating in your view.
        /// </summary>
        /// <returns>The head transform, or null if the avatar is not a BeatAvatar.</returns>
        internal static Transform Apply(Avatar avatar, bool hideHead)
        {
            SetLayerRecursively(avatar.gameObject, kAlwaysVisible);

            BeatAvatarPoseController poseController = AvatarBones.PoseController(avatar);
            if (poseController == null) return null;

            Transform head = AvatarBones.Head(poseController);
            if (head != null && hideHead) SetLayerRecursively(head.gameObject, kOnlyInThirdPerson);

            return head;
        }

        internal static void SetLayerRecursively(GameObject go, int layer)
        {
            go.layer = layer;
            for (var i = 0; i < go.transform.childCount; i++)
                SetLayerRecursively(go.transform.GetChild(i).gameObject, layer);
        }

        /// <summary>
        /// Makes a camera render the avatar. <paramref name="firstPerson"/> is the HMD camera --
        /// the one you must not show a head to. Everything else (Camera2 rigs, the spectator
        /// camera, fpfc) wants the head back.
        /// </summary>
        /// <returns>True when the camera's mask was wrong and had to be corrected.</returns>
        internal static bool ApplyCameraMask(Camera camera, bool firstPerson)
        {
            int mask = camera.cullingMask | kAlwaysVisibleMask;
            if (firstPerson) mask &= ~kOnlyInThirdPersonMask;
            else mask |= kOnlyInThirdPersonMask;

            if (mask == camera.cullingMask) return false;

            camera.cullingMask = mask;
            return true;
        }

        /// <summary>
        /// Adds the avatar layers to every loaded mirror's reflect mask. A reflection write to
        /// shared game state: additive and idempotent, but it persists for the process lifetime.
        /// </summary>
        /// <summary>
        /// Adds the avatar layers to URP's own layer masks.
        ///
        /// A camera's culling mask is NOT the only filter under URP: the ScriptableRenderer ANDs
        /// opaqueLayerMask and transparentLayerMask on top, so a layer missing from those renders
        /// on NO camera however that camera is configured. No camera-level check can see this.
        ///
        /// Global state for the process lifetime, and idempotent: it only ever adds layers.
        /// </summary>
        /// <summary>The renderer opaque mask as last observed, for diagnosis of a reset.</summary>
        internal static int LastOpaqueMask { get; private set; }

        /// <returns>True when the renderer's masks were missing our layers and had to be fixed.</returns>
        internal static bool EnsureRenderPipelineLayers()
        {
            // Every URP type here is reached by reflection rather than named. Builds before
            // Unity 6 run the built-in pipeline and ship no URP assembly at all, so a compile-time
            // reference is one those games cannot satisfy -- and currentRenderPipeline is null
            // there, which is the correct answer rather than a failure.
            RenderPipelineAsset asset = GraphicsSettings.currentRenderPipeline;
            if (asset == null)
            {
                ReportOnce("no scriptable render pipeline; the camera's culling mask is the only filter.");
                return false;
            }

            if (_rendererDataListField == null)
            {
                // The public rendererDataList returns ReadOnlySpan<T>, which net472 has no
                // definition for, so read the backing array field instead.
                _rendererDataListField = FindPrivateField(asset.GetType(), "m_RendererDataList");
                if (_rendererDataListField == null)
                {
                    ReportOnce("no m_RendererDataList on " + asset.GetType().Name + "; layer "
                        + kOnlyInThirdPerson + " cannot be added, so the head will render on NO camera.", true);
                    return false;
                }
            }

            // ScriptableRendererData is a class, so its array casts to object[] directly.
            var dataList = _rendererDataListField.GetValue(asset) as object[];
            if (dataList == null)
            {
                ReportOnce("m_RendererDataList is not an object[]; layer masks left alone.", true);
                return false;
            }

            var changed = false;

            foreach (object data in dataList)
            {
                if (data == null) continue;

                if (!ResolveMaskProperties(data.GetType()))
                {
                    ReportOnce("no opaque/transparent layer mask on " + data.GetType().Name
                        + "; layer " + kOnlyInThirdPerson + " cannot be added, so the head will render "
                        + "on NO camera.", true);
                    continue;
                }

                int opaque = ((LayerMask)_opaqueMaskProperty.GetValue(data, null)).value;
                LastOpaqueMask = opaque;
                int transparent = ((LayerMask)_transparentMaskProperty.GetValue(data, null)).value;
                int wanted = kAlwaysVisibleMask | kOnlyInThirdPersonMask;

                if ((opaque & wanted) != wanted)
                {
                    _opaqueMaskProperty.SetValue(data, (LayerMask)(opaque | wanted), null);
                    changed = true;
                }

                if ((transparent & wanted) != wanted)
                {
                    _transparentMaskProperty.SetValue(data, (LayerMask)(transparent | wanted), null);
                    changed = true;
                }

                ReportOnce(data.GetType().Name + " opaque 0x" + opaque.ToString("X8") + " -> 0x"
                    + (opaque | wanted).ToString("X8") + ", transparent 0x" + transparent.ToString("X8")
                    + " -> 0x" + (transparent | wanted).ToString("X8"));
            }

            return changed;
        }

        /// <summary>
        /// Says what the render pipeline actually did with our layers, exactly once per process.
        ///
        /// Every URP type here is reached by reflection, so a lookup that silently returns nothing
        /// is indistinguishable from a pipeline that needed no fixing -- and the two look identical
        /// from inside the headset, because the head is culled from the player's own view either
        /// way. The only visible symptom of the failure is the head missing from every OTHER
        /// camera, which nobody checks by accident.
        /// </summary>
        private static void ReportOnce(string what, bool broken = false)
        {
            if (_reportedPipeline) return;
            _reportedPipeline = true;

            if (broken) Plugin.Log.Warn("Render pipeline: " + what);
            else Plugin.Log.Info("Render pipeline: " + what);
        }

        /// <summary>
        /// Finds a private instance field, walking up the hierarchy. GetField does not search base
        /// types for non-public members, so a derived pipeline asset would otherwise report none.
        /// </summary>
        private static FieldInfo FindPrivateField(Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                FieldInfo field = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null) return field;
            }

            return null;
        }

        /// <summary>Resolves and caches both mask properties for a renderer data type.</summary>
        private static bool ResolveMaskProperties(Type dataType)
        {
            if (!ReferenceEquals(dataType, _rendererDataType))
            {
                _rendererDataType = dataType;
                _opaqueMaskProperty = dataType.GetProperty("opaqueLayerMask");
                _transparentMaskProperty = dataType.GetProperty("transparentLayerMask");
            }

            return _opaqueMaskProperty != null && _transparentMaskProperty != null;
        }

        internal static int AddToMirrorMask()
        {
            if (kReflectLayersField == null) return -1;

            var changed = 0;
            foreach (var renderer in Resources.FindObjectsOfTypeAll<MirrorRendererSO>())
            {
                var mask = (LayerMask)kReflectLayersField.GetValue(renderer);
                var updated = mask.value | kAlwaysVisibleMask | kOnlyInThirdPersonMask;
                if (updated == mask.value) continue;

                kReflectLayersField.SetValue(renderer, (LayerMask)updated);
                changed++;
            }

            return changed;
        }
    }
}
