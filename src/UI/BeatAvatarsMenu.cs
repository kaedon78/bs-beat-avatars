using System;
using System.Collections;
using System.Reflection;
using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.MenuButtons;
using HMUI;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zenject;

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
    /// Registration has to REPEAT, and it is driven by scene loads rather than by a standing timer.
    ///
    /// BSML binds MenuButtons AsSingle into the MENU container, not the app container, so every
    /// menu rebuild produces a fresh instance with an empty button list. Registering once at
    /// start-up works until the first rebuild and then silently stops: the button is simply not in
    /// the list any more. Applying anything in the game's own Settings is enough to cause it, and
    /// nothing is logged when it happens.
    ///
    /// A container is only ever rebuilt as part of loading a scene, so sceneLoaded is the event
    /// that matters. Retrying in a short burst after each load, instead of polling forever, means
    /// no work at all during a song -- which is where spending anything repeatedly is least
    /// welcome.
    ///
    /// The flow coordinator has the same lifetime problem from the other side.
    /// BeatSaberUI.CreateFlowCoordinator puts it on a plain GameObject in the current scene, so a
    /// cached one is destroyed by that same rebuild. It is created on demand instead, and the
    /// Unity null check is true for a destroyed object as well as an absent one.
    /// </summary>
    internal sealed class BeatAvatarsMenu : MonoBehaviour
    {
        // A menu container is installed during the scene load, so the first attempt can land too
        // early. Ten tries at half a second covers the gap without becoming a standing poll.
        private const int kAttempts = 10;
        private const float kRetryDelay = 0.5f;

        private static readonly FieldInfo kDiContainerField = typeof(BeatSaberUI)
            .GetField("diContainer", BindingFlags.Static | BindingFlags.NonPublic);

        private MenuButton _menuButton;
        private BeatAvatarsFlowCoordinator _flowCoordinator;
        private Coroutine _registering;

        private void Start()
        {
            _menuButton = new MenuButton(
                "Beat Avatars",
                "Size and grip of your first-person body.",
                ShowPanel);

            SceneManager.sceneLoaded += HandleSceneLoaded;

            // The menu may already be up if this plugin started late.
            Restart();
        }

        private void OnDestroy() => SceneManager.sceneLoaded -= HandleSceneLoaded;

        private void HandleSceneLoaded(Scene scene, LoadSceneMode mode) => Restart();

        private void Restart()
        {
            if (_registering != null) StopCoroutine(_registering);
            _registering = StartCoroutine(RegisterWhenContainerExists());
        }

        private IEnumerator RegisterWhenContainerExists()
        {
            for (var attempt = 0; attempt < kAttempts; attempt++)
            {
                if (TryRegister())
                {
                    _registering = null;
                    yield break;
                }

                yield return new WaitForSeconds(kRetryDelay);
            }

            // Not an error: most scene loads are not menu loads, and those simply never have a
            // container to register with.
            _registering = null;
        }

        /// <summary>
        /// BSML's menu container, or null, WITHOUT asking BSML for it.
        ///
        /// BeatSaberUI.DiContainer is a property whose getter logs an error every time it is read
        /// while still null -- the "Tried getting DiContainer too early!" line other mods produce
        /// during boot. Polling that property is therefore not free: measured, a burst of retries
        /// on every scene load took the count in one launch from 6 to 17, all of them noise this
        /// plugin caused in someone else's logger. Read the backing field instead and stay silent
        /// until there is genuinely something to register with.
        /// </summary>
        private static DiContainer MenuContainerOrNull()
        {
            if (kDiContainerField == null) return BeatSaberUI.DiContainer;
            return kDiContainerField.GetValue(null) as DiContainer;
        }

        private bool TryRegister()
        {
            DiContainer container = MenuContainerOrNull();
            if (container == null || container.IsInstalling) return false;

            try
            {
                // RegisterButton no-ops when a button with this text is already present, so this
                // is safe on a container that already has ours; it only does anything after a
                // rebuild.
                MenuButtons.Instance.RegisterButton(_menuButton);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.Error("Menu registration failed: " + ex);
                return false;
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
