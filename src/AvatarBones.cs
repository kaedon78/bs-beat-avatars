using System.Reflection;
using BeatSaber.AvatarCore;
using BeatSaber.BeatAvatarSDK;
using UnityEngine;

namespace BeatAvatars
{
    /// <summary>
    /// The four bones BeatAvatarPoseController drives, read off the component by reflection.
    ///
    /// Its own serialized fields rather than object names: the field is what the game actually
    /// poses, so it cannot disagree with what moves.
    /// </summary>
    internal static class AvatarBones
    {
        private const BindingFlags kNonPublicInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private static readonly FieldInfo kHead = Field("_headTransform");
        private static readonly FieldInfo kLeftHand = Field("_leftHandTransform");
        private static readonly FieldInfo kRightHand = Field("_rightHandTransform");
        private static readonly FieldInfo kBody = Field("_bodyTransform");

        private static FieldInfo Field(string name) =>
            typeof(BeatAvatarPoseController).GetField(name, kNonPublicInstance);

        internal static BeatAvatarPoseController PoseController(Avatar avatar) =>
            avatar == null ? null : avatar.GetComponentInChildren<BeatAvatarPoseController>(true);

        internal static Transform Head(BeatAvatarPoseController poseController) => Get(kHead, poseController);
        internal static Transform LeftHand(BeatAvatarPoseController poseController) => Get(kLeftHand, poseController);
        internal static Transform RightHand(BeatAvatarPoseController poseController) => Get(kRightHand, poseController);
        internal static Transform Body(BeatAvatarPoseController poseController) => Get(kBody, poseController);

        private static Transform Get(FieldInfo field, BeatAvatarPoseController poseController)
        {
            if (field == null || poseController == null) return null;
            return field.GetValue(poseController) as Transform;
        }

        /// <summary>
        /// Scales a bone. The pose controller rewrites the bone's local position every frame and a
        /// transform's own scale does not affect that, so the mesh shrinks around the tracked point.
        /// Scaling the avatar root instead would move every bone.
        /// </summary>
        internal static void SetScale(Transform bone, float scale)
        {
            if (bone == null || scale <= 0f) return;
            bone.localScale = Vector3.one * scale;
        }

        /// <summary>
        /// Raises or lowers a bone's VISUAL child, not the bone.
        ///
        /// A position set on the bone is gone by the next pose event; the child is never touched.
        /// Using the child also keeps the head independent of the body, which UpdateBodyPosition
        /// derives from the head BONE. The offset is in the bone's local space, so the bone's own
        /// scale applies to it.
        /// </summary>
        /// <summary>
        /// What the visual controller currently has in its mesh slots. The head sphere is fixed on
        /// the prefab while these come from AvatarData, so an avatar wearing nothing still has a
        /// head and looks, from outside, like a spawn that half worked.
        /// </summary>
        /// <summary>
        /// True when the avatar has lost the meshes it should be wearing. The hands are the
        /// sentinel: both entries in that collection carry a mesh, so a null there is never a valid
        /// choice, unlike an empty headTop or glasses slot.
        /// </summary>
        internal static bool HasLostVisuals(Avatar avatar)
        {
            var visual = avatar == null
                ? null
                : avatar.GetComponentInChildren<BeatAvatarVisualController>(true);
            if (visual == null) return false;

            return MeshName(visual, "_leftHandsHairMeshFilter") == "NULL";
        }

        internal static string DescribeMeshes(Avatar avatar)
        {
            var visual = avatar == null
                ? null
                : avatar.GetComponentInChildren<BeatAvatarVisualController>(true);
            if (visual == null) return "meshes=no visual controller";

            return "meshes headTop=" + MeshName(visual, "_headTopMeshFilter")
                 + " clothes=" + MeshName(visual, "_bodyMeshFilter")
                 + " hands=" + MeshName(visual, "_leftHandsHairMeshFilter");
        }

        private static string MeshName(BeatAvatarVisualController visual, string fieldName)
        {
            FieldInfo field = typeof(BeatAvatarVisualController)
                .GetField(fieldName, kNonPublicInstance);

            var filter = field?.GetValue(visual) as MeshFilter;
            if (filter == null) return "<no field>";
            return filter.sharedMesh == null ? "NULL" : filter.sharedMesh.name;
        }

        internal static void SetVerticalOffset(Transform bone, float offset)
        {
            if (bone == null || bone.childCount == 0) return;

            Transform visual = bone.GetChild(0);
            Vector3 position = visual.localPosition;
            visual.localPosition = new Vector3(position.x, offset, position.z);
        }
    }
}
