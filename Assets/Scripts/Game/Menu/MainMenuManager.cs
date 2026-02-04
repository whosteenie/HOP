using System.Collections;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Discord;
using Network;
using Network.Services;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor;
using Steamworks;

namespace Game.Menu {
    /// <summary>
    /// Main coordinator for the main menu system.
    /// Delegates UI, session, and gamemode management to specialized sub-managers.
    /// Steamworks Integrated.
    /// </summary>
    public class MainMenuManager : MonoBehaviour {
        #region Serialized Fields

        public UIDocument uiDocument;

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

        [Header("References")]
        [SerializeField] private Camera mainCamera;

        #endregion

        #region UI Elements - Panels

        private VisualElement _root;
        public VisualElement MainMenuPanel { get; private set; }
        private VisualElement _gamemodePanel;
        private VisualElement _lobbyPanel;
        private VisualElement _loadoutPanel;
        private VisualElement _optionsPanel;
        private VisualElement _creditsPanel;
        private List<VisualElement> _panels;
        private VisualElement _currentPanel;
        private Coroutine _panelFadeCoroutine;
        private const float PanelFadeDuration = 0.08f;
        private bool _isPrivateMatchIntent;

        #endregion

        #region Unity Lifecycle

        private void Awake() {
            if(uiDocument == null) {
                Debug.LogError("[MainMenuManager] UIDocument is not assigned!");
                return;
            }

            _root = uiDocument.rootVisualElement;
            FindPanels();
            WireSubManagerCallbacks();
        }

        private void Start() {
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;

            InitializeSubManagers();
            SetupOptionsMenuManager();
            LoadSettings();

            if(uiManager != null) uiManager.Initialize();
            if(sessionManager != null) sessionManager.Initialize();
            CheckFirstTimeSetup();

            if(DiscordManager.Instance != null) {
                DiscordManager.Instance.SetStatus("In Main Menu", "Browsing");
            }
        }

        private void WireSubManagerCallbacks() {
            if(uiManager != null) WireUIManagerEvents();
            if(sessionManager != null) WireSessionManagerEvents();
            if(gamemodeManager != null) WireGamemodeManagerEvents();
        }

        #endregion

        #region Initialization

        private void FindPanels() {
            MainMenuPanel = _root.Q<VisualElement>("main-menu-panel");
            _gamemodePanel = _root.Q<VisualElement>("gamemode-panel");
            _lobbyPanel = _root.Q<VisualElement>("lobby-panel");
            _loadoutPanel = _root.Q<VisualElement>("loadout-panel");
            _optionsPanel = _root.Q<VisualElement>("options-panel");
            _creditsPanel = _root.Q<VisualElement>("credits-panel");

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

        private void InitializeSubManagers() {
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
            uiManager.OnGamemodeSelected = (mode) => {
                if (_isPrivateMatchIntent) {
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
            uiManager.OnFirstTimeContinue = OnFirstTimeSetupContinue;
            uiManager.OnShowPanel = ShowPanel;
        }

        private void WireSessionManagerEvents() {
            if(sessionManager == null) return;

            sessionManager.OnHostClicked = () => { sessionManager.HandleHostClicked().Forget(); };
            sessionManager.OnJoinClicked = code => { _ = sessionManager.HandleFindGameClicked(); }; // Mapped to Find Game
            sessionManager.OnStartGameClicked = () => {
                gamemodeManager?.CloseDropdown();
            };
            sessionManager.OnBackFromLobbyClicked = () => {
                // Check if we should show modal
                // Logic: If Host or Has Members
                bool shouldShowModal = IsInActiveLobby();

                if(shouldShowModal && uiManager != null) {
                    uiManager.ShowLobbyLeaveConfirmation();
                } else {
                    UISoundService.PlayButtonClick(isBack: true);
                    SessionManager.Instance?.LeaveToMainMenuAsync().Forget();
                    ShowPanel(MainMenuPanel);
                }
            };
            sessionManager.OnHostStatusChanged = (isHost, wasHost) => {
                gamemodeManager?.SetHostStatus(isHost, wasHost);
            };
            sessionManager.ShouldShowLobbyLeaveModal = IsInActiveLobby;
        }
        
        private bool IsInActiveLobby() {
            var sessionManagerInstance = SessionManager.Instance;
            if (sessionManagerInstance == null || !sessionManagerInstance.CurrentLobby.HasValue) return false;
            
            // If we are owner, definitely warn.
            if (sessionManagerInstance.CurrentLobby.Value.Owner.Id == SteamClient.SteamId) return true;
            
            // If connected to multiplayer, warn.
            return true; 
        }

        private void WireGamemodeManagerEvents() {
             if (gamemodeManager != null) {
                 gamemodeManager.OnGameModeSelected = modeName => gamemodeManager.HandleGameModeSelected(modeName);
             }
        }

        #endregion

        #region First Time Setup

        private void CheckFirstTimeSetup() {
            uiManager?.CheckFirstTimeSetup();
        }

        private void OnFirstTimeSetupContinue() {
            if(uiManager == null) return;

            var playerName = uiManager.GetFirstTimeNameInput();
            if(string.IsNullOrWhiteSpace(playerName)) playerName = "Player";

            PlayerPrefs.SetString("PlayerName", playerName);
            PlayerPrefs.Save();

            uiManager.HideFirstTimeSetup();
            LoadSettings();
        }

        #endregion

        #region Navigation

        public void ShowPanel(VisualElement panel) {
            if(panel == null) return;

            if(_currentPanel == null) {
                foreach(var p in _panels) {
                    if(p != null && p != panel) HidePanelImmediate(p);
                }
                ShowPanelImmediate(panel);
                UpdateDiscordStatusForPanel(panel);
                _currentPanel = panel;
                return;
            }

            if(panel == _currentPanel) return;
            if(_panelFadeCoroutine != null) {
                StopCoroutine(_panelFadeCoroutine);
                _panelFadeCoroutine = null;
            }

            var needFadeOut = _currentPanel != _loadoutPanel;
            var needFadeIn = panel != _loadoutPanel;
            var requiresFade = needFadeOut || needFadeIn;

            if(!requiresFade) {
                HidePanelImmediate(_currentPanel);
                ShowPanelImmediate(panel);
                UpdateDiscordStatusForPanel(panel);
                _currentPanel = panel;
                return;
            }

            _panelFadeCoroutine = StartCoroutine(FadeBetweenPanels(_currentPanel, panel));
            UpdateDiscordStatusForPanel(panel);
        }

        private void UpdateDiscordStatusForPanel(VisualElement panel) {
            if(DiscordManager.Instance == null) return;

            if(panel == MainMenuPanel) DiscordManager.Instance.SetStatus("In Main Menu", "Browsing");
            else if(panel == _lobbyPanel) DiscordManager.Instance.SetStatus("In Lobby", "Waiting for Match");
            else if(panel == _gamemodePanel) DiscordManager.Instance.SetStatus("In Main Menu", "Selecting Gamemode");
            else if(panel == _loadoutPanel) DiscordManager.Instance.SetStatus("In Main Menu", "Editing Loadout");
        }

        private void HidePanelImmediate(VisualElement panel) {
            if(panel == null) return;
            panel.AddToClassList("hidden");
            panel.style.display = StyleKeyword.Null;
            panel.style.opacity = new StyleFloat(1f);
        }

        private void ShowPanelImmediate(VisualElement panel) {
            if(panel == null) return;
            panel.RemoveFromClassList("hidden");
            panel.style.display = DisplayStyle.Flex;
            panel.style.opacity = new StyleFloat(1f);
            panel.BringToFront();
        }

        private IEnumerator FadeBetweenPanels(VisualElement oldPanel, VisualElement newPanel) {
            foreach(var p in _panels) {
                if(p == null || p == oldPanel || p == newPanel) continue;
                HidePanelImmediate(p);
            }

            var fadeOutPanel = oldPanel == _loadoutPanel ? null : oldPanel;
            var fadeInPanel = newPanel == _loadoutPanel ? null : newPanel;

            if(fadeInPanel != null) {
                fadeInPanel.RemoveFromClassList("hidden");
                fadeInPanel.style.display = DisplayStyle.Flex;
                fadeInPanel.style.opacity = new StyleFloat(0f);
                fadeInPanel.BringToFront();
            } else if(newPanel != null) {
                ShowPanelImmediate(newPanel);
            }

            var elapsed = 0f;
            while(elapsed < PanelFadeDuration) {
                elapsed += Time.deltaTime;
                var t = Mathf.Clamp01(elapsed / PanelFadeDuration);
                if(fadeOutPanel != null) fadeOutPanel.style.opacity = new StyleFloat(1f - t);
                if(fadeInPanel != null) fadeInPanel.style.opacity = new StyleFloat(t);
                yield return null;
            }

            if(fadeOutPanel != null) HidePanelImmediate(fadeOutPanel);

            if(fadeInPanel != null) {
                fadeInPanel.style.opacity = new StyleFloat(1f);
                fadeInPanel.RemoveFromClassList("hidden");
                fadeInPanel.style.display = DisplayStyle.Flex;
                fadeInPanel.BringToFront();
            }

            _currentPanel = newPanel;
            _panelFadeCoroutine = null;
        }

        public void ShowCharacterCustomization() {
            if(characterCustomizationManager != null) {
                characterCustomizationManager.OnButtonClickedCallback = OnButtonClicked;
                characterCustomizationManager.MouseEnterCallback = MouseEnter;
                characterCustomizationManager.OnBackFromCustomizationCallback = () => ShowPanel(_loadoutPanel);
            }
            ShowPanel(_loadoutPanel);
            characterCustomizationManager?.ShowCustomization();
        }


        #endregion

        #region Settings

        private void SetupOptionsMenuManager() {
            if(optionsMenuManager == null) return;

            optionsMenuManager.OnButtonClickedCallback = OnButtonClicked;
            optionsMenuManager.MouseEnterCallback = _ => UISoundService.PlayButtonHover();
            optionsMenuManager.OnBackFromOptionsCallback = () => ShowPanel(MainMenuPanel);
            optionsMenuManager.Initialize();

            if(characterCustomizationManager != null) {
                characterCustomizationManager.OnButtonClickedCallback = OnButtonClicked;
                characterCustomizationManager.MouseEnterCallback = _ => UISoundService.PlayButtonHover();
                characterCustomizationManager.OnBackFromCustomizationCallback = () => ShowPanel(_loadoutPanel);
            }
        }

        private void LoadSettings() {
            optionsMenuManager?.LoadSettings();
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