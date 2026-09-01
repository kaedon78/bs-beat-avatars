using System;
using BeatSaber.AvatarCore;
using UnityEngine;
using UnityEngine.XR;

namespace BeatAvatars
{
    /// <summary>
    /// Feeds the local player's head and hands to an <see cref="Avatar"/> in place of the network
    /// peer poses a multiplayer avatar normally gets.
    ///
    /// The poses must be LOCAL to the bones' own parent, because BeatAvatar forwards them straight
    /// into SetLocalPositionAndRotation. World poses work only while that parent sits at the origin
    /// unrotated, and break the moment the room offset or a 360 map turns it.
    /// </summary>
    internal sealed class LocalPlayerPoseProvider : MonoBehaviour, IAvatarPoseDataProvider
    {
        private AvatarPoseData _pose;

        public event Action<AvatarPoseData> poseDidChangeEvent;

        public AvatarPoseData currentPose => _pose;

        /// <summary>The space poses are expressed in -- the head bone's parent.</summary>
        internal Transform space;

        internal Transform head;
        internal VRController leftHand;
        internal VRController rightHand;

        // The transforms actually followed. See ResolveHands: VRController.transform carries the
        // RAW tracked node pose, while the player's controller position and rotation settings live
        // on a child anchor.
        internal Transform leftHandAnchor;
        internal Transform rightHandAnchor;

        /// <summary>False follows the raw controller pose, ignoring the player's grip settings.</summary>
        internal bool useControllerOffsets = true;

        /// <summary>Grip offset from the anchor, in the anchor's local space. See the config.</summary>
        internal Vector3 handPositionOffset;

        /// <summary>Extra hand rotation in degrees, in the anchor's local frame.</summary>
        internal Vector3 handRotationOffset;

        // Where an absent or untracked hand is parked, relative to the head. VRController has its
        // own kLeftControllerDefaultPosition for this but it is private, and reflecting a constant
        // is not worth it.
        private static readonly Vector3 kLeftHandRestPosition = new Vector3(-0.2f, -0.4f, 0.3f);
        private static readonly Vector3 kRightHandRestPosition = new Vector3(0.2f, -0.4f, 0.3f);

        /// <summary>
        /// Resolves the hands by scanning for <see cref="VRController"/> and matching XR node.
        ///
        /// Not via PlayerVRControllersManager or MenuPlayerController: those are different
        /// components in different scenes, and node is what actually identifies a hand.
        /// </summary>
        internal void ResolveHands()
        {
            leftHand = null;
            rightHand = null;
            leftHandAnchor = null;
            rightHandAnchor = null;

            foreach (VRController controller in FindObjectsByType<VRController>(FindObjectsSortMode.None))
            {
                // activeInHierarchy, NOT isActiveAndEnabled. The menu's controllers are active and
                // poseValid with the VRController COMPONENT disabled -- fpfc drives their
                // transforms itself -- so requiring enabled rejects both hands, and the avatar goes
                // on rendering them at their fallback rest pose rather than looking broken.
                if (!controller.gameObject.activeInHierarchy) continue;

                if (controller.node == XRNode.LeftHand && leftHand == null) leftHand = controller;
                else if (controller.node == XRNode.RightHand && rightHand == null) rightHand = controller;
            }

            leftHandAnchor = Anchor(leftHand);
            rightHandAnchor = Anchor(rightHand);
        }

        /// <summary>
        /// The transform to follow for a hand: the saber anchor, not the controller.
        ///
        /// VRController.Update writes the RAW tracked node pose onto its own transform, and
        /// position/rotation return that -- the player's grip settings are not in it. Those land on
        /// _viewAnchorTransform, a child, which is where the saber is mounted. Follow the controller
        /// instead and the hand sits subtly askew of the saber in it.
        ///
        /// In mouse mode the anchor is local identity, so this degrades to the raw pose and fpfc is
        /// unaffected.
        /// </summary>
        private Transform Anchor(VRController controller)
        {
            if (controller == null) return null;
            if (!useControllerOffsets) return controller.transform;

            return controller.viewAnchorTransform != null ? controller.viewAnchorTransform : controller.transform;
        }

        /// <summary>
        /// Re-resolves the head or hands if either has gone missing.
        ///
        /// Resolving once at spawn is not enough: a scene can be rebuilt around the avatar, and at
        /// the instant it respawns Camera.main and the controllers may not exist yet. A rig left
        /// unresolved freezes the avatar in place with its hands at their rest pose, which reads as
        /// a rendering fault rather than a tracking one, because a frozen avatar is still fully
        /// drawn.
        /// </summary>
        internal void EnsureRig()
        {
            if (head == null)
            {
                Camera camera = Camera.main;
                if (camera != null) head = camera.transform;
            }

            if (leftHand == null || rightHand == null) ResolveHands();
        }

        /// <summary>
        /// Takes one reading without raising the event. Avatar.SetPoseDataProvider immediately
        /// applies currentPose, so without this the avatar folds onto its origin for a frame.
        /// </summary>
        internal void Sample()
        {
            if (head == null) return;

            _pose = new AvatarPoseData(
                Place(head.position, head.rotation),
                HandPose(leftHand, leftHandAnchor, kLeftHandRestPosition),
                HandPose(rightHand, rightHandAnchor, kRightHandRestPosition));
        }

        private void LateUpdate()
        {
            if (head == null) return;

            Sample();
            poseDidChangeEvent?.Invoke(_pose);
        }

        private Pose HandPose(VRController controller, Transform anchor, Vector3 fallbackLocalPosition)
        {
            // A missing or untracked controller must not collapse the hand onto the avatar's
            // origin -- that reads as a broken avatar rather than as an absent controller. Park it
            // where the game itself parks an inactive controller instead.
            if (controller == null || !controller.gameObject.activeInHierarchy)
                return Place(head.position + head.rotation * fallbackLocalPosition, head.rotation);

            Transform followed = anchor != null ? anchor : controller.transform;

            // The offset is applied in the anchor's frame, not the world's, so it stays along the
            // handle however the controller is turned.
            Vector3 gripPosition = followed.position + followed.rotation * handPositionOffset;
            Quaternion gripRotation = followed.rotation * Quaternion.Euler(handRotationOffset);
            return Place(gripPosition, gripRotation);
        }

        private Pose Place(Vector3 worldPosition, Quaternion worldRotation)
        {
            Vector3 position = worldPosition;
            Quaternion rotation = worldRotation;

            if (space != null)
            {
                position = space.InverseTransformPoint(worldPosition);
                rotation = Quaternion.Inverse(space.rotation) * worldRotation;
            }

            return new Pose(position, rotation);
        }
    }
}
