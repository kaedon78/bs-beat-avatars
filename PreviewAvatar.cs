using System;
using BeatSaber.AvatarCore;
using UnityEngine;

namespace BeatAvatarBody
{
    /// <summary>
    /// A second avatar standing in front of the player, mirroring them, so hand position, rotation
    /// and size can be tuned while watching the result.
    ///
    /// The reflection is done by a container with a NEGATIVE Z SCALE, and the clone is then fed the
    /// player's poses completely unchanged. This is CustomAvatars' approach and it is not merely
    /// simpler than mirroring the poses -- it is the only one that is right.
    ///
    /// The first attempt here did mirror the poses, reflecting each position and rebuilding each
    /// rotation from a reflected forward and up. The body looked correct and the hands did not, and
    /// that split is the tell: the body's orientation is yaw-only, derived from the head by
    /// BeatAvatarPoseController, so almost any plausible mirror gets it right. The hands carry full
    /// 3D orientation AND chirality, and a reflection is orientation-reversing -- a mirrored right
    /// hand is a LEFT hand, which no rotation can express. A negative scale can, because it is an
    /// actual reflection.
    ///
    /// Note the plane: with the container at z = d and the player at the origin, a bone at local z
    /// lands at d - z, which is a reflection about z = d/2. So the apparent mirror sits at HALF the
    /// container distance.
    /// </summary>
    internal sealed class PreviewAvatar : IDisposable
    {
        private readonly GameObject _container;
        private readonly Avatar _avatar;
        private readonly BeatAvatarPartReveal _reveal;

        private PreviewAvatar(GameObject container, Avatar avatar, BeatAvatarPartReveal reveal)
        {
            _container = container;
            _avatar = avatar;
            _reveal = reveal;
        }

        /// <summary>
        /// Re-runs the part reveal. Needed because the parts the prefab ships switched off are
        /// exactly the ones our own pickers change, so the mirror has to follow the edit as well
        /// as the body does.
        /// </summary>
        internal void ApplyReveal() => _reveal?.Apply();

        internal static PreviewAvatar Create(
            Avatar avatar,
            LocalPlayerPoseProvider source,
            Transform space,
            Vector3 containerOffset,
            LocalAvatarVisualProvider visualProvider,
            BeatAvatarBodyConfig config)
        {
            var container = new GameObject("BeatAvatarBodyPreview");
            container.transform.SetParent(space, false);
            container.transform.localPosition = containerOffset;
            container.transform.localRotation = Quaternion.identity;
            container.transform.localScale = new Vector3(1f, 1f, -1f);

            avatar.transform.SetParent(container.transform, false);
            avatar.transform.localPosition = Vector3.zero;
            avatar.transform.localRotation = Quaternion.identity;

            // The preview is only ever looked AT, so it keeps its head: no first-person hiding.
            AvatarLayers.Apply(avatar, false);
            ApplyScales(avatar, config);

            if (visualProvider != null) avatar.SetVisualDataProvider(visualProvider);

            // The SAME provider the player's own avatar uses. Avatar.SetPoseDataProvider only
            // subscribes to an event, so two avatars can share one source, and sharing guarantees
            // the preview can never drift out of step with the body it is previewing.
            avatar.SetPoseDataProvider(source);

            var reveal = avatar.gameObject.AddComponent<BeatAvatarPartReveal>();
            var visualController = avatar.GetComponentInChildren<BeatSaber.BeatAvatarSDK.BeatAvatarVisualController>(true);
            if (visualController != null)
            {
                reveal.Bind(visualController);
                reveal.Apply();
            }

            return new PreviewAvatar(container, avatar, reveal);
        }

        internal void ApplyConfig(BeatAvatarBodyConfig config)
        {
            ApplyScales(_avatar, config);

            if (_container != null)
                _container.transform.localPosition = BeatAvatarBodyConfig.Offset.ToVector3(config.previewPosition);
        }

        private static void ApplyScales(Avatar avatar, BeatAvatarBodyConfig config)
        {
            BeatSaber.BeatAvatarSDK.BeatAvatarPoseController bones = AvatarBones.PoseController(avatar);
            AvatarBones.SetScale(AvatarBones.LeftHand(bones), config.handScale);
            AvatarBones.SetScale(AvatarBones.RightHand(bones), config.handScale);
            AvatarBones.SetScale(AvatarBones.Head(bones), config.headScale);
            AvatarBones.SetScale(AvatarBones.Body(bones), config.bodyScale);
            AvatarBones.SetVerticalOffset(AvatarBones.Head(bones), config.headVerticalOffset);
            AvatarBones.SetVerticalOffset(AvatarBones.Body(bones), config.bodyVerticalOffset);
        }

        public void Dispose()
        {
            // Destroying the container takes the avatar with it, and Avatar.OnDestroy unsubscribes
            // it from the shared pose provider.
            if (_container != null) UnityEngine.Object.Destroy(_container);
        }
    }
}
