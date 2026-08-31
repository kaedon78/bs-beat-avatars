using System;
using System.Collections;
using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.MenuButtons;
using HMUI;
using UnityEngine;

namespace BeatAvatarBody.UI
{
    /// <summary>
    /// Registers the "Beat Avatar Body" button on the main menu and presents the tuning panel.
    ///
    /// A dedicated menu button and flow coordinator, NOT a Mod Settings tab. Mod Settings is a
    /// narrow modal -- this repo's notes put its usable body at about 90 units, with checkbox
    /// labels ellipsizing near 54 characters and a half-width slider collapsing its label to a
    /// single character -- and, more importantly, it fills the space in front of the player, which
    /// is exactly where a preview of your own body has to go. CustomAvatars reaches the same
    /// conclusion and for the same reason: its Avatars button opens its own flow coordinator with
    /// the mirror in the world and the settings beside it.
    /// </summary>
    internal sealed class BeatAvatarBodyMenu : MonoBehaviour
    {
        private MenuButton _menuButton;
        private BeatAvatarBodyFlowCoordinator _flowCoordinator;

        private IEnumerator Start()
        {
            // MenuButtons.Instance throws until the menu container exists -- the same
            // "Tried getting DiContainer too early!" that other mods log during boot. Wait for it
            // rather than racing it.
            while (true)
            {
                try
                {
                    if (BeatSaberUI.DiContainer != null && !BeatSaberUI.DiContainer.IsInstalling)
                    {
                        Register();
                        yield break;
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error("AVBODY menu registration failed: " + ex);
                    yield break;
                }

                yield return new WaitForSeconds(0.5f);
            }
        }

        private void Register()
        {
            _flowCoordinator = BeatSaberUI.CreateFlowCoordinator<BeatAvatarBodyFlowCoordinator>();
            _menuButton = new MenuButton(
                "Beat Avatar Body",
                "Size and grip of your first-person body.",
                () => BeatSaberUI.MainFlowCoordinator.PresentFlowCoordinator(_flowCoordinator));

            MenuButtons.Instance.RegisterButton(_menuButton);
            Plugin.Log.Info("AVBODY menu button registered");
        }

        private void OnDestroy()
        {
            try
            {
                if (_menuButton != null) MenuButtons.Instance.UnregisterButton(_menuButton);
            }
            catch (Exception)
            {
                // The menu container is already gone during shutdown; nothing to unregister from.
            }
        }
    }

    internal sealed class BeatAvatarBodyFlowCoordinator : FlowCoordinator
    {
        private BeatAvatarBodySettingsViewController _settingsViewController;

        protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            if (!firstActivation) return;

            SetTitle("Beat Avatar Body");
            showBackButton = true;

            _settingsViewController = BeatSaberUI.CreateViewController<BeatAvatarBodySettingsViewController>();
            ProvideInitialViewControllers(_settingsViewController);
        }

        protected override void BackButtonWasPressed(ViewController topViewController)
        {
            BeatSaberUI.MainFlowCoordinator.DismissFlowCoordinator(this);
        }
    }
}
