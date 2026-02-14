using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Discord;
using Network;
using Network.Services;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.UIElements;
using Game.UI;
using Game.Rendering;
using UnityEngine.Rendering;
using Cursor = UnityEngine.Cursor;

namespace Game.Menu {
    /// <summary>
    /// Main coordinator for the main menu system.
    /// Delegates UI, session, and gamemode management to specialized sub-managers.
    /// Steamworks Integrated.
    /// </summary>
    public class MainMenuManager : UIElementBase {
        #region Serialized Fields

        [Header("Audio")]
        [SerializeField] private AudioMixer audioMixer;

        [Header("Options")]
        [SerializeField] private OptionsMenuManager optionsMenuManager;

        [Header("Character Customization")]
        [SerializeField] private CharacterCustomizationManager characterCustomizationManager;

        [Header("Sub-Managers")]
        [SerializeField] private MainMenuUIManager uiManager;

        [SerializeField] private MainMenuSessionManager sessionManager;
        [SerializeField] private MainMenuGamemodeManager gamemodeManager;
        [SerializeField] private VoiceOverlayManager voiceOverlayManager;

        [Header("References")]
        [SerializeField] private Camera mainCamera;
        [SerializeField] private MenuBlurVolumeController menuBlurController;
        [SerializeField] private Volume optionsBlurVolume;

        #endregion

        #region UI Elements - Panels
        public VisualElement MainMenuPanel { get; private set; }
        private VisualElement _gamemodePanel;
        private VisualElement _lobbyPanel;
        private VisualElement _loadoutPanel;
        private VisualElement _optionsPanel;
        private VisualElement _creditsPanel;
        private List<VisualElement> _panels;
        private const float PanelFadeDuration = 0.08f;
        private bool _isPrivateMatchIntent;

        // Shared UI navigation helper for main menu panels
        private UINavigator _navigator;
        private bool _navigatorMissingLogged;

        #endregion

        #region Unity Lifecycle

        protected override void Start() {
            base.Start();
            InitializeMenuBlurController();
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;

            InitializeSubManagers();
            SetupOptionsMenuManager();
            LoadSettings();

            if(uiManager != null) MainMenuUIManager.InitializeGameMenuVisibility();
            if(sessionManager != null) sessionManager.Initialize();
            if(voiceOverlayManager != null) voiceOverlayManager.Initialize(Root);

            if(DiscordManager.Instance != null) {
                DiscordManager.Instance.SetStatus("In Main Menu", "Browsing");
            }
        }

        protected override void OnDisable() {
            SetOptionsOpenState(false);
            base.OnDisable();
        }

        protected override void OnInitialize() {
            FindPanels();
            InitializeNavigator();
            WireSubManagerCallbacks();
        }

        protected override Dictionary<string, System.Type> GetRequiredElements() {
            return new Dictionary<string, System.Type> {
                { "main-menu-panel", typeof(VisualElement) }
            };
        }

        private void WireSubManagerCallbacks() {
            if(uiManager != null) WireUIManagerEvents();
            if(sessionManager != null) WireSessionManagerEvents();
        }

        #endregion

        #region Initialization

        private void FindPanels() {
            MainMenuPanel = QRequired<VisualElement>("main-menu-panel");
            _gamemodePanel = QOptional<VisualElement>("gamemode-panel");
            _lobbyPanel = QOptional<VisualElement>("lobby-panel");
            _loadoutPanel = QOptional<VisualElement>("loadout-panel");
            _optionsPanel = QOptional<VisualElement>("options-panel");
            _creditsPanel = QOptional<VisualElement>("credits-panel");

            _panels = new List<VisualElement> {
                MainMenuPanel,
                _gamemodePanel,
                uiManager.PlayGamemodePanel,
                _lobbyPanel,
                _loadoutPanel,
                _optionsPanel,
                _creditsPanel
            };
        }

        private void InitializeNavigator() {
            // Loadout panel keeps its snappy behavior (no cross-fade) to preserve UX.
            _navigator = new UINavigator(
                this,
                _panels,
                PanelFadeDuration,
                panel => panel == _loadoutPanel
            );
        }

        private void InitializeSubManagers() {
            if(voiceOverlayManager == null) voiceOverlayManager = GetComponentInChildren<VoiceOverlayManager>();
            
            if(uiManager != null) {
                if(uiManager.uiDocument == null) uiManager.uiDocument = uiDocument;
            }
            if(sessionManager != null) {
                if(sessionManager.uiDocument == null) sessionManager.uiDocument = uiDocument;
            }
            if(gamemodeManager && gamemodeManager.uiDocument == null) {
                gamemodeManager.uiDocument = uiDocument;
            }
        }

        public void ShowLoadoutPanel() {
            var loadoutManager = FindFirstObjectByType<LoadoutManager>();
            if(loadoutManager != null) loadoutManager.ShowLoadout();
            ShowPanel(_loadoutPanel);
        }

        public void ShowProfileView(ulong steamId, string playerName, bool isEditable) {
            var loadoutManager = FindFirstObjectByType<LoadoutManager>();
            if(loadoutManager != null) {
                loadoutManager.ShowProfileView(steamId, playerName, isEditable);
            }
            ShowPanel(_loadoutPanel);
        }

        private void WireUIManagerEvents() {
            if(uiManager == null) return;

            uiManager.OnPlayMatchmakingClicked = () => {
                _isPrivateMatchIntent = false;
                ShowPanel(uiManager.PlayGamemodePanel);
            };
            uiManager.OnPlayPrivateClicked = () => {
                _isPrivateMatchIntent = true;
                ShowPanel(uiManager.PlayGamemodePanel);
            };
            uiManager.OnGamemodeSelected = mode => {
                // IMPORTANT:
                // We often auto-host a "party lobby" in the background for invites/party UX.
                // That lobby uses "Private" as its Steam lobby mode, but it should NOT force the
                // Private Match flow. Only the explicit button intent should decide the path here.
                if(_isPrivateMatchIntent) {
                    sessionManager.HandlePrivateMatchSelection(mode).Forget();
                } else {
                    MainMenuSessionManager.HandleGamemodeSelected(mode);
                    // Matchmaking intent: start search
                    sessionManager.HandleFindGameClicked(mode).Forget();

                    // Hide gamemode panel
                    if (uiManager.PlayGamemodePanel.resolvedStyle.display == DisplayStyle.Flex) {
                        ShowPanel(MainMenuPanel);
                    }
                }
            };
            uiManager.OnGamemodeDropdownClicked = () => sessionManager.ToggleGamemodeDropdown();
            uiManager.OnCancelMatchmakingClicked = () => sessionManager.HandleCancelMatchmakingClicked();
            uiManager.OnLoadoutClicked = () => {
                var loadoutManager = FindFirstObjectByType<LoadoutManager>();
                if(loadoutManager != null) loadoutManager.ShowLoadout();
                ShowPanel(_loadoutPanel);
            };
            uiManager.OnOptionsClicked = () => {
                if(optionsMenuManager != null) {
                    optionsMenuManager.OnBackFromOptionsCallback = ReturnToMainMenuFromOptions;
                    optionsMenuManager.LoadSettings();
                    optionsMenuManager.OnOptionsPanelShown();
                }
                ShowPanel(_optionsPanel);
            };
            uiManager.OnCreditsClicked = () => ShowPanel(_creditsPanel);
            uiManager.OnQuitConfirmed = OnQuitConfirmed;
            uiManager.OnQuitCancelled = OnQuitCancelled;
            uiManager.OnLobbyLeaveConfirmed = () => {
                if(SessionManager.Instance != null) {
                    SessionManager.Instance.LeaveToMainMenuAsync().Forget(); // Removed skipFade param if not supported? Or keep if overload exists. Assuming default.
                }
                ShowPanel(MainMenuPanel);
            };
            uiManager.OnLobbyLeaveCancelled = () => { };
            uiManager.OnShowPanel = ShowPanel;
        }

        private void WireSessionManagerEvents() {
            if(sessionManager == null) return;

            sessionManager.OnHostClicked = () => { sessionManager.HandleHostClicked().Forget(); };
            sessionManager.OnJoinClicked = code => { _ = sessionManager.HandleFindGameClicked(); }; // Mapped to Find Game
            sessionManager.OnStartGameClicked = () => {
                if(gamemodeManager != null) gamemodeManager.CloseDropdown();
            };
            sessionManager.OnBackFromLobbyClicked = () => {
                // Check if we should show modal
                // Logic: If Host or Has Members
                var shouldShowModal = IsInActiveLobby();

                if(shouldShowModal && uiManager != null) {
                    uiManager.ShowLobbyLeaveConfirmation();
                } else {
                    UISoundService.PlayButtonClick(isBack: true);
                    SessionManager.Instance.LeaveToMainMenuAsync().Forget();
                    ShowPanel(MainMenuPanel);
                }
            };
            sessionManager.OnHostStatusChanged = (isHost, wasHost) => {
                if(gamemodeManager != null) gamemodeManager.SetHostStatus(isHost, wasHost);
            };
            sessionManager.ShouldShowLobbyLeaveModal = IsInActiveLobby;
        }
        
        private static bool IsInActiveLobby() {
            var sessionManagerInstance = SessionManager.Instance;
            return sessionManagerInstance != null && sessionManagerInstance.CurrentLobby.HasValue;
        }

        #endregion

        #region Navigation

        public void ShowPanel(VisualElement panel) {
            if(panel == null) return;

            if(_navigator == null) {
                if(_navigatorMissingLogged) return;
                Debug.LogError(
                    "[MainMenuManager] UI navigator is not initialized; cannot show panel. " +
                    "Check OnInitialize/FindPanels/InitializeNavigator execution order.",
                    this);
                _navigatorMissingLogged = true;
                return;
            }

            _navigatorMissingLogged = false;
            _navigator.Show(panel);
            SetOptionsOpenState(panel == _optionsPanel);
            UpdateDiscordStatusForPanel(panel);
            
            // Refresh challenges when main menu panel is shown
            if(panel == MainMenuPanel && uiManager != null) {
                uiManager.RefreshMainMenuChallenges();
            }
        }

        private void SetOptionsOpenState(bool isOptionsOpen) {
            if(Root == null) return;

            if(isOptionsOpen) {
                Root.AddToClassList("options-open");
            } else {
                Root.RemoveFromClassList("options-open");
            }

            if(menuBlurController != null) {
                menuBlurController.SetBlurActive(isOptionsOpen);
            }
        }

        private void InitializeMenuBlurController() {
            if(optionsBlurVolume == null) {
                return;
            }

            if(menuBlurController == null) {
                menuBlurController = GetComponent<MenuBlurVolumeController>();
            }

            if(menuBlurController == null) {
                menuBlurController = gameObject.AddComponent<MenuBlurVolumeController>();
            }

            menuBlurController.SetBlurVolume(optionsBlurVolume);
        }

        private void UpdateDiscordStatusForPanel(VisualElement panel) {
            if(DiscordManager.Instance == null) return;

            if(panel == MainMenuPanel) DiscordManager.Instance.SetStatus("In Main Menu", "Browsing");
            else if(panel == _lobbyPanel) DiscordManager.Instance.SetStatus("In Lobby", "Waiting for Match");
            else if(panel == _gamemodePanel) DiscordManager.Instance.SetStatus("In Main Menu", "Selecting Gamemode");
            else if(panel == _loadoutPanel) DiscordManager.Instance.SetStatus("In Main Menu", "Editing Loadout");
        }


        public void ShowCharacterCustomization() {
            if(characterCustomizationManager != null) {
                characterCustomizationManager.OnButtonClickedCallback = OnButtonClicked;
                characterCustomizationManager.MouseEnterCallback = MouseEnter;
                characterCustomizationManager.OnBackFromCustomizationCallback = () => ShowPanel(_loadoutPanel);
            }
            ShowPanel(_loadoutPanel);
            if(characterCustomizationManager != null) characterCustomizationManager.ShowCustomization();
        }


        #endregion

        #region Settings

        private void SetupOptionsMenuManager() {
            if(optionsMenuManager == null) return;

            optionsMenuManager.OnButtonClickedCallback = OnButtonClicked;
            optionsMenuManager.MouseEnterCallback = _ => UISoundService.PlayButtonHover();
            optionsMenuManager.OnBackFromOptionsCallback = ReturnToMainMenuFromOptions;
            optionsMenuManager.Initialize();

            if(characterCustomizationManager == null) return;
            {
                characterCustomizationManager.OnButtonClickedCallback = OnButtonClicked;
                characterCustomizationManager.MouseEnterCallback = _ => UISoundService.PlayButtonHover();
                characterCustomizationManager.OnBackFromCustomizationCallback = () => ShowPanel(_loadoutPanel);
            }
        }

        private void LoadSettings() {
            if(optionsMenuManager != null) optionsMenuManager.LoadSettings();
        }

        private void ReturnToMainMenuFromOptions() {
            ShowPanel(MainMenuPanel);
        }

        #endregion

        #region UI Utilities

        public static void OnButtonClicked(bool isBack = false) {
            UISoundService.PlayButtonClick(isBack);
        }

        public static void MouseEnter(MouseEnterEvent evt) {
            UISoundService.PlayButtonHover();
        }

        #endregion

        #region Quit Confirmation

        private static void OnQuitConfirmed() {
            Debug.Log("Quitting game...");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        private static void OnQuitCancelled() {
            OnButtonClicked();
        }

        #endregion
    }
}
