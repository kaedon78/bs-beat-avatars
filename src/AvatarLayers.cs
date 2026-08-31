using System.Reflection;
using BeatSaber.AvatarCore;
using BeatSaber.BeatAvatarSDK;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace BeatAvatarBody
{
    /// <summary>
    /// Layer bookkeeping. This is where first-person body presence actually lives or dies: the
    /// avatar is a multiplayer avatar, so nothing about it expects to be inside the local
    /// player's head.
    ///
    /// The layer numbers are the game's own, read out of the TagManager in globalgamemanagers on
    /// 1.45.0 -- 10 is "Avatar", 3 and 7 are unnamed and free. They match the constants
    /// CustomAvatars has used for years, which is what keeps mods that already cull layer 3 (and
    /// third-person camera mods that re-add it) compatible with this one.
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

        private static readonly FieldInfo kRendererDataListField = typeof(UniversalRenderPipelineAsset)
            .GetField("m_RendererDataList", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo kReflectLayersField = typeof(MirrorRendererSO)
            .GetField("_reflectLayers", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// Puts the avatar on the Avatar layer and, if asked, moves the head bone's whole subtree
        /// onto the third-person-only layer.
        ///
        /// Taking the head from BeatAvatarPoseController's own <c>_headTransform</c> rather than
        /// matching renderer names is the point: the SDK names five head part fields
        /// (head top, glasses, facial hair, eyes, mouth) but the head mesh itself is not among
        /// them, so a field-by-field pass leaves a face floating in your view. Everything parented
        /// under the head bone is head, by construction.
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
        internal static void ApplyCameraMask(Camera camera, bool firstPerson)
        {
            var mask = camera.cullingMask | kAlwaysVisibleMask;
            if (firstPerson) mask &= ~kOnlyInThirdPersonMask;
            else mask |= kOnlyInThirdPersonMask;
            camera.cullingMask = mask;
        }

        /// <summary>
        /// Adds the avatar layers to every loaded mirror's reflect mask.
        ///
        /// <c>_reflectLayers</c> is a private LayerMask on a ScriptableObject, so this is a
        /// reflection write to shared game state; it is idempotent and additive, but it does
        /// persist for the process lifetime.
        /// </summary>
        /// <summary>
        /// Adds the avatar layers to URP's own layer masks.
        ///
        /// A camera's culling mask is NOT the only filter under URP: the ScriptableRenderer applies
        /// opaqueLayerMask and transparentLayerMask on top of it, so a layer missing from those
        /// renders on NO camera however that camera is configured. This is invisible to any
        /// camera-level check, which is why a mask that says the head should be drawn can sit
        /// alongside a third-person view that does not draw it.
        ///
        /// This is global state for the process lifetime, and idempotent: it only ever adds layers.
        /// </summary>
        internal static void EnsureRenderPipelineLayers()
        {
            var asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (asset == null) return;

            // The public rendererDataList returns ReadOnlySpan<T>, which net472 has no definition
            // for, so read the backing array field instead.
            var dataList = kRendererDataListField?.GetValue(asset) as ScriptableRendererData[];
            if (dataList == null) return;

            foreach (ScriptableRendererData data in dataList)
            {
                var universal = data as UniversalRendererData;
                if (universal == null) continue;

                int opaque = universal.opaqueLayerMask.value;
                int transparent = universal.transparentLayerMask.value;
                int wanted = kAlwaysVisibleMask | kOnlyInThirdPersonMask;

                if ((opaque & wanted) != wanted) universal.opaqueLayerMask = opaque | wanted;
                if ((transparent & wanted) != wanted) universal.transparentLayerMask = transparent | wanted;
            }
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
