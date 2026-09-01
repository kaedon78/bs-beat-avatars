using System;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.ViewControllers;
using UnityEngine;

namespace BeatAvatars.UI
{
    /// <summary>
    /// The tuning panel. Every value writes straight through to the live config and pushes it onto
    /// the avatar already in the scene, so a slider drag is visible on the mirror as it moves.
    ///
    /// Nothing here respawns the avatar: reloading the Addressable prefab mid-drag would make the
    /// preview flicker on every tick. The one exception is the head-hiding toggle, which changes a
    /// layer assignment made at spawn and says so in its hint.
    /// </summary>
    [ViewDefinition("BeatAvatars.UI.Views.Settings.bsml")]
    internal class BeatAvatarsSettingsViewController : BSMLAutomaticViewController
    {
        private BeatAvatarsConfig Config => BeatAvatarsController.Instance?.Config;

        /// <summary>
        /// What everything was when the panel opened. Each slider's undo button restores from this,
        /// NOT from the built-in defaults -- the two differ the moment a value has been tuned and
        /// saved, and "put it back how it was" is the one you want after an experiment went wrong.
        /// Retaken on every activation, so a second visit undoes to the second visit's start.
        /// </summary>
        private BeatAvatarsConfig _openedWith;

        // Slider increments here are 0.005 and 1, so a difference this small is a float artefact
        // rather than an edit the player made.
        private const float kEpsilon = 1e-4f;

        private static bool Differs(float a, float b) => Mathf.Abs(a - b) > kEpsilon;

        private bool Ready => Config != null && _openedWith != null;

        private void Changed(string undoProperty)
        {
            BeatAvatarsController.Instance?.ApplyConfigToCurrentAvatar();

            // Only the affected button plus the summary, not all of them: a slider drag raises a
            // value change every frame, and re-evaluating every undo button on each one is work
            // for nothing.
            NotifyPropertyChanged(undoProperty);
            NotifyPropertyChanged(nameof(canUndoAny));
        }

        protected float handScale
        {
            get => Config?.handScale ?? 1f;
            set { if (Config != null) { Config.handScale = value; Changed(nameof(canUndoHandScale)); } }
        }

        protected float headScale
        {
            get => Config?.headScale ?? 1f;
            set { if (Config != null) { Config.headScale = value; Changed(nameof(canUndoHeadScale)); } }
        }

        protected float bodyScale
        {
            get => Config?.bodyScale ?? 1f;
            set { if (Config != null) { Config.bodyScale = value; Changed(nameof(canUndoBodyScale)); } }
        }

        protected float headVerticalOffset
        {
            get => Config?.headVerticalOffset ?? 0f;
            set { if (Config != null) { Config.headVerticalOffset = value; Changed(nameof(canUndoHeadVerticalOffset)); } }
        }

        protected float bodyVerticalOffset
        {
            get => Config?.bodyVerticalOffset ?? 0f;
            set { if (Config != null) { Config.bodyVerticalOffset = value; Changed(nameof(canUndoBodyVerticalOffset)); } }
        }

        protected float handOffsetX
        {
            get => Config?.handPositionOffset?.x ?? 0f;
            set { if (Config?.handPositionOffset != null) { Config.handPositionOffset.x = value; Changed(nameof(canUndoHandOffsetX)); } }
        }

        protected float handOffsetY
        {
            get => Config?.handPositionOffset?.y ?? 0f;
            set { if (Config?.handPositionOffset != null) { Config.handPositionOffset.y = value; Changed(nameof(canUndoHandOffsetY)); } }
        }

        protected float handOffsetZ
        {
            get => Config?.handPositionOffset?.z ?? 0f;
            set { if (Config?.handPositionOffset != null) { Config.handPositionOffset.z = value; Changed(nameof(canUndoHandOffsetZ)); } }
        }

        protected float handRotationX
        {
            get => Config?.handRotationOffset?.x ?? 0f;
            set { if (Config?.handRotationOffset != null) { Config.handRotationOffset.x = value; Changed(nameof(canUndoHandRotationX)); } }
        }

        protected float handRotationY
        {
            get => Config?.handRotationOffset?.y ?? 0f;
            set { if (Config?.handRotationOffset != null) { Config.handRotationOffset.y = value; Changed(nameof(canUndoHandRotationY)); } }
        }

        protected float handRotationZ
        {
            get => Config?.handRotationOffset?.z ?? 0f;
            set { if (Config?.handRotationOffset != null) { Config.handRotationOffset.z = value; Changed(nameof(canUndoHandRotationZ)); } }
        }

        protected bool useControllerOffsets
        {
            get => Config?.useControllerOffsets ?? true;
            set { if (Config != null) { Config.useControllerOffsets = value; Changed(nameof(canUndoAny)); } }
        }

        protected bool hideHeadInFirstPerson
        {
            get => Config?.hideHeadInFirstPerson ?? true;
            set { if (Config != null) Config.hideHeadInFirstPerson = value; }
        }

        // Each undo button's interactable state. False means the setting is already what it was
        // when the panel opened, so the button would do nothing and should not invite a press.
        protected bool canUndoHandScale => Ready && Differs(Config.handScale, _openedWith.handScale);
        protected bool canUndoHeadScale => Ready && Differs(Config.headScale, _openedWith.headScale);
        protected bool canUndoBodyScale => Ready && Differs(Config.bodyScale, _openedWith.bodyScale);
        protected bool canUndoHeadVerticalOffset => Ready && Differs(Config.headVerticalOffset, _openedWith.headVerticalOffset);
        protected bool canUndoBodyVerticalOffset => Ready && Differs(Config.bodyVerticalOffset, _openedWith.bodyVerticalOffset);
        protected bool canUndoHandOffsetX => Ready && Differs(Config.handPositionOffset.x, _openedWith.handPositionOffset.x);
        protected bool canUndoHandOffsetY => Ready && Differs(Config.handPositionOffset.y, _openedWith.handPositionOffset.y);
        protected bool canUndoHandOffsetZ => Ready && Differs(Config.handPositionOffset.z, _openedWith.handPositionOffset.z);
        protected bool canUndoHandRotationX => Ready && Differs(Config.handRotationOffset.x, _openedWith.handRotationOffset.x);
        protected bool canUndoHandRotationY => Ready && Differs(Config.handRotationOffset.y, _openedWith.handRotationOffset.y);
        protected bool canUndoHandRotationZ => Ready && Differs(Config.handRotationOffset.z, _openedWith.handRotationOffset.z);

        protected bool canUndoAny =>
            canUndoHandScale || canUndoHeadScale || canUndoBodyScale ||
            canUndoHeadVerticalOffset || canUndoBodyVerticalOffset ||
            canUndoHandOffsetX || canUndoHandOffsetY || canUndoHandOffsetZ ||
            canUndoHandRotationX || canUndoHandRotationY || canUndoHandRotationZ ||
            (Ready && Config.useControllerOffsets != _openedWith.useControllerOffsets);

        private void Revert(string valueProperty, string undoProperty, Action<BeatAvatarsConfig, BeatAvatarsConfig> restore)
        {
            if (!Ready) return;

            restore(Config, _openedWith);
            BeatAvatarsController.Instance?.ApplyConfigToCurrentAvatar();

            NotifyPropertyChanged(valueProperty);
            NotifyPropertyChanged(undoProperty);
            NotifyPropertyChanged(nameof(canUndoAny));
        }

        [UIAction("RevertHandScale")]
        internal void RevertHandScale() => Revert(nameof(handScale), nameof(canUndoHandScale), (c, o) => c.handScale = o.handScale);

        [UIAction("RevertHeadScale")]
        internal void RevertHeadScale() => Revert(nameof(headScale), nameof(canUndoHeadScale), (c, o) => c.headScale = o.headScale);

        [UIAction("RevertBodyScale")]
        internal void RevertBodyScale() => Revert(nameof(bodyScale), nameof(canUndoBodyScale), (c, o) => c.bodyScale = o.bodyScale);

        [UIAction("RevertHeadVerticalOffset")]
        internal void RevertHeadVerticalOffset() => Revert(nameof(headVerticalOffset), nameof(canUndoHeadVerticalOffset), (c, o) => c.headVerticalOffset = o.headVerticalOffset);

        [UIAction("RevertBodyVerticalOffset")]
        internal void RevertBodyVerticalOffset() => Revert(nameof(bodyVerticalOffset), nameof(canUndoBodyVerticalOffset), (c, o) => c.bodyVerticalOffset = o.bodyVerticalOffset);

        [UIAction("RevertHandOffsetZ")]
        internal void RevertHandOffsetZ() => Revert(nameof(handOffsetZ), nameof(canUndoHandOffsetZ), (c, o) => c.handPositionOffset.z = o.handPositionOffset.z);

        [UIAction("RevertHandOffsetY")]
        internal void RevertHandOffsetY() => Revert(nameof(handOffsetY), nameof(canUndoHandOffsetY), (c, o) => c.handPositionOffset.y = o.handPositionOffset.y);

        [UIAction("RevertHandOffsetX")]
        internal void RevertHandOffsetX() => Revert(nameof(handOffsetX), nameof(canUndoHandOffsetX), (c, o) => c.handPositionOffset.x = o.handPositionOffset.x);

        [UIAction("RevertHandRotationX")]
        internal void RevertHandRotationX() => Revert(nameof(handRotationX), nameof(canUndoHandRotationX), (c, o) => c.handRotationOffset.x = o.handRotationOffset.x);

        [UIAction("RevertHandRotationY")]
        internal void RevertHandRotationY() => Revert(nameof(handRotationY), nameof(canUndoHandRotationY), (c, o) => c.handRotationOffset.y = o.handRotationOffset.y);

        [UIAction("RevertHandRotationZ")]
        internal void RevertHandRotationZ() => Revert(nameof(handRotationZ), nameof(canUndoHandRotationZ), (c, o) => c.handRotationOffset.z = o.handRotationOffset.z);

        /// <summary>Puts every slider back to what it was when the panel opened.</summary>
        [UIAction("RevertAllPressed")]
        internal void RevertAllPressed()
        {
            if (!Ready) return;

            Config.handScale = _openedWith.handScale;
            Config.headScale = _openedWith.headScale;
            Config.bodyScale = _openedWith.bodyScale;
            Config.headVerticalOffset = _openedWith.headVerticalOffset;
            Config.bodyVerticalOffset = _openedWith.bodyVerticalOffset;
            Config.handPositionOffset = BeatAvatarsConfig.Offset.Copy(_openedWith.handPositionOffset);
            Config.handRotationOffset = BeatAvatarsConfig.Offset.Copy(_openedWith.handRotationOffset);
            Config.useControllerOffsets = _openedWith.useControllerOffsets;

            BeatAvatarsController.Instance?.ApplyConfigToCurrentAvatar();
            NotifyEverything();
        }

        [UIAction("ResetPressed")]
        internal void ResetPressed()
        {
            if (Config == null) return;

            var defaults = new BeatAvatarsConfig();
            Config.handScale = defaults.handScale;
            Config.headScale = defaults.headScale;
            Config.bodyScale = defaults.bodyScale;
            Config.headVerticalOffset = defaults.headVerticalOffset;
            Config.bodyVerticalOffset = defaults.bodyVerticalOffset;
            Config.handPositionOffset = defaults.handPositionOffset;
            Config.handRotationOffset = defaults.handRotationOffset;
            Config.useControllerOffsets = defaults.useControllerOffsets;

            BeatAvatarsController.Instance?.ApplyConfigToCurrentAvatar();
            NotifyEverything();
        }

        [UIAction("SavePressed")]
        internal void SavePressed()
        {
            Config?.Save();
        }

        /// <summary>
        /// Pushes every value AND every undo button's state back to the widgets. Without this the
        /// sliders keep showing the old numbers while the avatar has already moved.
        /// </summary>
        private void NotifyEverything()
        {
            NotifyPropertyChanged(nameof(handScale));
            NotifyPropertyChanged(nameof(headScale));
            NotifyPropertyChanged(nameof(bodyScale));
            NotifyPropertyChanged(nameof(headVerticalOffset));
            NotifyPropertyChanged(nameof(bodyVerticalOffset));
            NotifyPropertyChanged(nameof(handOffsetX));
            NotifyPropertyChanged(nameof(handOffsetY));
            NotifyPropertyChanged(nameof(handOffsetZ));
            NotifyPropertyChanged(nameof(handRotationX));
            NotifyPropertyChanged(nameof(handRotationY));
            NotifyPropertyChanged(nameof(handRotationZ));
            NotifyPropertyChanged(nameof(useControllerOffsets));

            NotifyPropertyChanged(nameof(canUndoHandScale));
            NotifyPropertyChanged(nameof(canUndoHeadScale));
            NotifyPropertyChanged(nameof(canUndoBodyScale));
            NotifyPropertyChanged(nameof(canUndoHeadVerticalOffset));
            NotifyPropertyChanged(nameof(canUndoBodyVerticalOffset));
            NotifyPropertyChanged(nameof(canUndoHandOffsetX));
            NotifyPropertyChanged(nameof(canUndoHandOffsetY));
            NotifyPropertyChanged(nameof(canUndoHandOffsetZ));
            NotifyPropertyChanged(nameof(canUndoHandRotationX));
            NotifyPropertyChanged(nameof(canUndoHandRotationY));
            NotifyPropertyChanged(nameof(canUndoHandRotationZ));
            NotifyPropertyChanged(nameof(canUndoAny));
        }

        public override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            // Snapshot BEFORE base, so the widgets bind against a snapshot that already exists and
            // every undo button starts out disabled rather than flickering enabled for a frame.
            _openedWith = Config?.Clone();

            base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);

            NotifyEverything();
            BeatAvatarsController.Instance?.ShowPreviewAsync();
        }

        public override void DidDeactivate(bool removedFromHierarchy, bool screenSystemDisabling)
        {
            // Saving here rather than on every slider change: a drag raises a value change per
            // frame, and rewriting the file at that rate is real disk traffic for no benefit.
            Config?.Save();
            BeatAvatarsController.Instance?.HidePreview();
            base.DidDeactivate(removedFromHierarchy, screenSystemDisabling);
        }
    }
}
