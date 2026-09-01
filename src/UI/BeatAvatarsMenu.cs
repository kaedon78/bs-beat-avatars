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
    /// A dedicated button and flow coordinator, NOT a Mod Settings tab: that panel fills the space
    /// in front of the player, which is exactly where the body preview has to go.
    ///
    /// Registration REPEATS. BSML binds MenuButtons into the MENU container, so every menu rebuild
    /// starts with an empty button list and a once-only registration silently stops working --
    /// applying anything in the game's Settings is enough, and nothing is logged when it happens.
    /// Containers are only rebuilt while loading a scene, so a short burst of retries after each
    /// load covers it and costs nothing during a song.
    ///
    /// The flow coordinator has the same lifetime problem: CreateFlowCoordinator puts it on a plain
    /// GameObject in the current scene, so it is created on demand rather than cached. The Unity
    /// null check below is true for a destroyed object as well as an absent one.
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
        /// BeatSaberUI.DiContainer's getter LOGS AN ERROR every time it is read while still null --
        /// the "Tried getting DiContainer too early!" line seen during boot. Retrying through the
        /// property therefore fills someone else's log with our noise; the backing field is silent.
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
