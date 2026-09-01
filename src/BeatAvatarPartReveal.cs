using System.Reflection;
using BeatSaber.BeatAvatarSDK;
using UnityEngine;

namespace BeatAvatars
{
    /// <summary>
    /// Turns on the head parts the avatar prefabs ship switched off.
    ///
    /// Glasses, Mouth and FacialHair are inactive in every prefab reachable through
    /// InstantiateAvatar. UpdateAvatarVisual fills their mesh or sprite anyway and never activates
    /// them, and nothing in BeatSaber.BeatAvatarSDK calls SetActive, so they are populated and
    /// permanently invisible.
    ///
    /// The test is whether the visual update put anything in the slot, not whether the saved id is
    /// "None" -- the mesh is what decides if there is anything to draw. Mouth stays off under that
    /// same rule, because every entry in the mouth collection has a null sprite.
    /// </summary>
    internal sealed class BeatAvatarPartReveal : MonoBehaviour
    {
        private const BindingFlags kNonPublicInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private static readonly FieldInfo kGlassesField =
            typeof(BeatAvatarVisualController).GetField("_glassesMeshFilter", kNonPublicInstance);

        private static readonly FieldInfo kFacialHairField =
            typeof(BeatAvatarVisualController).GetField("_facialHairMeshFilter", kNonPublicInstance);

        private static readonly FieldInfo kMouthField =
            typeof(BeatAvatarVisualController).GetField("_mouthSprite", kNonPublicInstance);

        private MeshFilter _glasses;
        private MeshFilter _facialHair;
        private SpriteRenderer _mouth;

        internal void Bind(BeatAvatarVisualController visualController)
        {
            _glasses = kGlassesField?.GetValue(visualController) as MeshFilter;
            _facialHair = kFacialHairField?.GetValue(visualController) as MeshFilter;
            _mouth = kMouthField?.GetValue(visualController) as SpriteRenderer;
        }

        /// <summary>
        /// Must run AFTER the avatar has applied the visual data, since it reads what that wrote.
        /// The avatar subscribes to the provider inside SetVisualDataProvider, so subscribing
        /// later puts this second in the invocation list.
        /// </summary>
        internal void Apply()
        {
            SetActive(_glasses, _glasses != null && _glasses.sharedMesh != null);
            SetActive(_facialHair, _facialHair != null && _facialHair.sharedMesh != null);
            SetActive(_mouth, _mouth != null && _mouth.sprite != null);
        }

        private static void SetActive(Component component, bool active)
        {
            if (component == null) return;
            if (component.gameObject.activeSelf != active) component.gameObject.SetActive(active);
        }

    }
}
