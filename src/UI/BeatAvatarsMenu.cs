using System;
using System.Collections;
using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.MenuButtons;
using HMUI;
using UnityEngine;

namespace BeatAvatars.UI
{
    /// <summary>
    /// Registers the "Beat Avatars" button on the main menu and presents the tuning panel.
    ///
    /// A dedicated menu button and flow coordinator, NOT a Mod Settings tab. Mod Settings is a
    /// narrow modal -- its usable body is about 90 units, with checkbox labels ellipsizing near 54
    /// characters and a half-width slider collapsing its label to a single character -- and, more
    /// importantly, it fills the space in front of the player, which is exactly where a preview of
    /// your own body has to go. CustomAvatars reaches the same conclusion for the same reason.
    ///
    /// Registration REPEATS, and both halves of that are load-bearing.
    ///
    /// BSML binds MenuButtons AsSingle into the MENU container, not the app container, so every
    /// menu rebuild produces a fresh instance with an empty button list. Registering once at
    /// start-up therefore works until the first rebuild and then silently stops: the button simply
    /// is not in the list any more. Applying anything in the game's own Settings is enough to
    /// trigger it, and nothing is logged when it happens.
    ///
    /// The flow coordinator has the same lifetime problem from the other direction.
    /// BeatSaberUI.CreateFlowCoordinator puts it on a plain GameObject in the current scene, so a
    /// cached one is destroyed by that same rebuild. It is created on demand instead, and the
    /// Unity null check below is true for a destroyed object as well as an absent one.
    /// </summary>
    internal sealed class BeatAvatarsMenu : MonoBehaviour
    {
        private MenuButton _menuButton;
        private BeatAvatarsFlowCoordinator _flowCoordinator;

        private IEnumerator Start()
        {
            _menuButton = new MenuButton(
                "Beat Avatars",
                "Size and grip of your first-person body.",
                ShowPanel);

            while (true)
            {
                TryRegister();
                yield return new WaitForSeconds(2f);
            }
        }

        private void TryRegister()
        {
            // MenuButtons.Instance throws until the menu container exists -- the same
            // "Tried getting DiContainer too early!" other mods log during boot -- so check the
            // container first rather than throwing once every couple of seconds through a menu.
            if (BeatSaberUI.DiContainer == null || BeatSaberUI.DiContainer.IsInstalling) return;

            try
            {
                // RegisterButton no-ops when a button with this text is already present, so this
                // is safe to call repeatedly; it only does anything after a rebuild.
                MenuButtons.Instance.RegisterButton(_menuButton);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error("Menu registration failed: " + ex);
            }
        }

        private void ShowPanel()
        {
            if (_flowCoordinator == null)
                _flowCoordinator = BeatSaberUI.CreateFlowCoordinator<BeatAvatarsFlowCoordinator>();

            BeatSaberUI.MainFlowCoordinator.PresentFlowCoordinator(_flowCoordinator);
        }
    }

    internal sealed class BeatAvatarsFlowCoordinator : FlowCoordinator
    {
        private BeatAvatarsSettingsViewController _settingsViewController;

        protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            if (!firstActivation) return;

            SetTitle("Beat Avatars");
            showBackButton = true;

            _settingsViewController = BeatSaberUI.CreateViewController<BeatAvatarsSettingsViewController>();
            ProvideInitialViewControllers(_settingsViewController);
        }

        protected override void BackButtonWasPressed(ViewController topViewController)
        {
            BeatSaberUI.MainFlowCoordinator.DismissFlowCoordinator(this);
        }
    }
}
