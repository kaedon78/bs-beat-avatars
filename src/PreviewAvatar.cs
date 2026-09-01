using System;
using BeatSaber.AvatarCore;
using UnityEngine;

namespace BeatAvatars
{
    /// <summary>
    /// A second avatar in front of the player, mirroring them, so size and grip can be tuned while
    /// watching the result.
    ///
    /// The reflection is a container with a NEGATIVE Z SCALE, fed the player's poses unchanged.
    /// Mirroring the poses instead cannot work: a reflection is orientation-reversing, so a
    /// mirrored right hand is a LEFT hand and no rotation expresses that. It looks correct on the
    /// torso, whose orientation is yaw-only, and wrong on the hands.
    ///
    /// With the container at z = d, a bone at local z lands at d - z, so the apparent mirror sits
    /// at HALF the container distance.
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
        /// Re-runs the part reveal, so an avatar edit reaches the mirror as well as the body.
        /// </summary>
        internal void ApplyReveal() => _reveal?.Apply();

        internal static PreviewAvatar Create(
            Avatar avatar,
            LocalPlayerPoseProvider source,
            Transform space,
            Vector3 containerOffset,
            LocalAvatarVisualProvider visualProvider,
            BeatAvatarsConfig config)
        {
            var container = new GameObject("BeatAvatarsPreview");
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

        internal void ApplyConfig(BeatAvatarsConfig config)
        {
            ApplyScales(_avatar, config);

            if (_container != null)
                _container.transform.localPosition = BeatAvatarsConfig.Offset.ToVector3(config.previewPosition);
        }

        private static void ApplyScales(Avatar avatar, BeatAvatarsConfig config)
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
