using System.Reflection;
using BeatSaber.AvatarCore;
using BeatSaber.BeatAvatarSDK;
using UnityEngine;

namespace BeatAvatars
{
    /// <summary>
    /// The four bones BeatAvatarPoseController drives, read off the component by reflection.
    ///
    /// They are serialized private fields, and going through them beats matching object names:
    /// the names happen to be Head / LeftHand / RightHand / Clothes today, but the component's own
    /// field is what the game actually poses, so it cannot disagree with what moves.
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
        /// Scales a bone in place. Safe against the pose controller, which writes the bone's LOCAL
        /// position every frame -- a transform's own scale does not affect its own local position,
        /// so the mesh shrinks around the tracked point instead of drifting off it. Scaling the
        /// avatar ROOT would not have this property: the bone positions are expressed in the root's
        /// space, so a scaled root moves every bone.
        /// </summary>
        internal static void SetScale(Transform bone, float scale)
        {
            if (bone == null || scale <= 0f) return;
            bone.localScale = Vector3.one * scale;
        }

        /// <summary>
        /// Raises or lowers a bone's VISUAL child rather than the bone itself.
        ///
        /// The bone cannot be moved: BeatAvatarPoseController writes its local position every
        /// frame, so anything set on it is gone by the next pose event. Its child is never touched,
        /// which makes this stick -- the same reason SetScale works on the bone but a position
        /// would not.
        ///
        /// Using the child also decouples the head from the body. UpdateBodyPosition derives the
        /// body from the head BONE, so moving the head bone would drag the torso with it; moving
        /// the head's visuals leaves the body exactly where it was.
        ///
        /// The offset is in the bone's local space, which the bone's own scale applies to -- at a
        /// head scale of 0.75 a 0.1 offset reads as 0.075. Both are tuned by eye against the
        /// mirror, so this is left uncompensated rather than made surprising in the other
        /// direction.
        /// </summary>
        internal static void SetVerticalOffset(Transform bone, float offset)
        {
            if (bone == null || bone.childCount == 0) return;

            Transform visual = bone.GetChild(0);
            Vector3 position = visual.localPosition;
            visual.localPosition = new Vector3(position.x, offset, position.z);
        }
    }
}
