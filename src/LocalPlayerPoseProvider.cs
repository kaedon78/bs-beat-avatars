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
    /// The poses must be LOCAL to the bones' own parent: BeatAvatar forwards them straight into
    /// Transform.SetLocalPositionAndRotation (BeatAvatarPoseController.UpdateTransforms), exactly
    /// as MultiplayerAvatarPoseController does with network poses. Feeding world poses works only
    /// by accident, when that parent happens to sit at the origin unrotated, and breaks the moment
    /// the room offset or a 360 map rotates it.
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
        /// Deliberately not via PlayerVRControllersManager or MenuPlayerController: those are two
        /// different components in two different scenes, and this has to work in both plus
        /// whatever a mod adds. VRController.node is public and is the thing that actually
        /// identifies a hand.
        /// </summary>
        internal void ResolveHands()
        {
            leftHand = null;
            rightHand = null;
            leftHandAnchor = null;
            rightHandAnchor = null;

            foreach (VRController controller in FindObjectsByType<VRController>(FindObjectsSortMode.None))
            {
                // activeInHierarchy, NOT isActiveAndEnabled. Measured 2026-08-31: the menu's
                // ControllerLeft/ControllerRight are active and poseValid, with the VRController
                // COMPONENT disabled -- fpfc drives their transforms itself. Requiring enabled
                // rejected both hands while the avatar still rendered them at their fallback rest
                // pose, so the failure looked like a working avatar rather than a resolution bug.
                if (!controller.gameObject.activeInHierarchy) continue;

                if (controller.node == XRNode.LeftHand && leftHand == null) leftHand = controller;
                else if (controller.node == XRNode.RightHand && rightHand == null) rightHand = controller;
            }

            leftHandAnchor = Anchor(leftHand);
            rightHandAnchor = Anchor(rightHand);
        }

        /// <summary>
        /// The transform to follow for a hand.
        ///
        /// VRController.Update writes the raw tracked node pose straight onto its own transform,
        /// and VRController.position/rotation just return that -- the player's controller position
        /// and rotation settings are NOT in it. Those are applied by TryGetControllerOffset onto
        /// _viewAnchorTransform, a child, which is what the saber is mounted on. Following the
        /// anchor is therefore what makes the avatar's hand sit in the same place and at the same
        /// angle as the saber the player is actually holding; following the controller transform
        /// gives a hand that is subtly rotated away from their own grip.
        ///
        /// In mouse mode the anchor is reset to local identity, so this degrades to the raw pose
        /// on its own and fpfc is unaffected.
        /// </summary>
        private Transform Anchor(VRController controller)
        {
            if (controller == null) return null;
            if (!useControllerOffsets) return controller.transform;

            return controller.viewAnchorTransform != null ? controller.viewAnchorTransform : controller.transform;
        }

        /// <summary>
        /// Re-resolves anything that went missing, and reports what it fixed.
        ///
        /// Resolving once at spawn is not enough. Measured in VR 2026-08-31: dismissing the health
        /// warning reloads the menu scene, the avatar is respawned into the half-built scene, and
        /// Camera.main and the MenuControllers do not exist yet at that instant. The avatar then
        /// froze in place with both hands parked at their rest pose for the rest of the session --
        /// and it looked like a rendering bug rather than a resolution one, because a frozen avatar
        /// is still a fully drawn avatar. In fpfc the transition is fast enough that this never
        /// happened, so only a VR run could find it.
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
