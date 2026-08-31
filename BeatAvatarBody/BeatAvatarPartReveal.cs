using System.Reflection;
using BeatSaber.BeatAvatarSDK;
using UnityEngine;

namespace BeatAvatarBody
{
    /// <summary>
    /// Turns on the head parts the avatar prefabs ship switched off.
    ///
    /// Measured 2026-08-31 across all three prefabs reachable through InstantiateAvatar
    /// (BeatAvatar, BeatAvatarResults, BeatAvatarHologram): Glasses, Mouth and FacialHair are
    /// inactive in the prefab itself, before any visual data is applied.
    /// <c>BeatAvatarVisualController.UpdateAvatarVisual</c> assigns their mesh or sprite anyway and
    /// never activates them, and there is no SetActive call anywhere in BeatSaber.BeatAvatarSDK --
    /// so nothing the game does could ever make them render. They are populated and invisible.
    ///
    /// The test is "did the visual update put anything in it", not "is the saved id the string
    /// None". Both agree today -- the None entries in the glasses and facial-hair collections carry
    /// null meshes -- but the mesh is the thing that decides whether there is anything to draw, and
    /// it stays right if a future version adds an id that resolves to nothing.
    ///
    /// Mouth is handled by the same rule and stays off as a result: all twelve entries in the mouth
    /// collection have a null sprite in 1.45.0, so there is no mouth art to reveal. If a later
    /// version ships some, this starts working without a code change.
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

        /// <summary>Describes what this component decided, for the probe and the log.</summary>
        internal string Describe()
        {
            return "glasses=" + State(_glasses, _glasses != null ? _glasses.sharedMesh : null)
                 + " facialHair=" + State(_facialHair, _facialHair != null ? _facialHair.sharedMesh : null)
                 + " mouth=" + State(_mouth, _mouth != null ? _mouth.sprite : null);
        }

        private static string State(Component component, Object asset)
        {
            if (component == null) return "<field missing>";
            return (asset == null ? "empty" : asset.name) + "/" + (component.gameObject.activeSelf ? "on" : "off");
        }
    }
}
