using System.Collections;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Game.Match;
using Network;
using Network.Services;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor;

namespace Game.Menu {
    /// <summary>
    /// Main coordinator for the main menu system.
    /// Delegates UI, session, and gamemode management to specialized sub-managers.
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

        #endregion

        #region Unity Lifecycle

        private void Awake() {
            if(uiDocument == null) {
                Debug.LogError("[MainMenuManager] UIDocument is not assigned!");
                return;
            }

            _root = uiDocument.rootVisualElement;

            // Find panel references (needed for ShowPanel coordination)
            FindPanels();

            // CRITICAL: Wire up callbacks in Awake to ensure they're set before sub-managers' Start() runs
            WireSubManagerCallbacks();
        }

        private void Start() {
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;

            // Initialize sub-managers (they will register events in their Start)
            // Callbacks are already wired in Awake(), so events will be registered with callbacks set
            InitializeSubManagers();

            // Setup options menu manager
            SetupOptionsMenuManager();
            LoadSettings();

            // Initialize UI manager (hides game menu, etc.)
            if(uiManager != null) {
                uiManager.Initialize();
            }

            // Check first time setup
            CheckFirstTimeSetup();
        }

        private void WireSubManagerCallbacks() {
            // Wire UI Manager callbacks first
            if(uiManager != null) {
                WireUIManagerEvents();
            }

            // Wire Session Manager callbacks
            if(sessionManager != null) {
                WireSessionManagerEvents();
            }

            // Wire Gamemode Manager callbacks
            if(gamemodeManager != null) {
                WireGamemodeManagerEvents();
            }
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
                _lobbyPanel,
                _loadoutPanel,
                _optionsPanel,
                _creditsPanel
            };
        }

        private void InitializeSubManagers() {
            // Initialize UI Manager
            if(uiManager != null) {
                if(uiManager.uiDocument == null) {
                    uiManager.uiDocument = uiDocument;
                }
                // Callbacks already wired in WireSubManagerCallbacks()
            }

            // Initialize Session Manager
            if(sessionManager != null) {
                if(sessionManager.uiDocument == null) {
                    sessionManager.uiDocument = uiDocument;
                }
                // Callbacks already wired in WireSubManagerCallbacks()
            }

            // Initialize Gamemode Manager
            if(gamemodeManager != null) {
                if(gamemodeManager.uiDocument == null) {
                    gamemodeManager.uiDocument = uiDocument;
                }
                // Callbacks already wired in WireSubManagerCallbacks()
            }
        }

        private void WireUIManagerEvents() {
            if(uiManager == null) return;

            uiManager.OnPlayClicked = OnPlayClicked;
            uiManager.OnLoadoutClicked = () => {
                var loadoutManager = FindFirstObjectByType<LoadoutManager>();
                if(loadoutManager != null) {
                    loadoutManager.ShowLoadout();
                }

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
                    SessionManager.Instance.LeaveToMainMenuAsync(skipFade: true).Forget();
                }

                ShowPanel(MainMenuPanel);
            };
            uiManager.OnLobbyLeaveCancelled = () => { };
            uiManager.OnFirstTimeContinue = OnFirstTimeSetupContinue;
            uiManager.OnShowPanel = ShowPanel;
        }

        private void WireSessionManagerEvents() {
            if(sessionManager == null) return;

            sessionManager.OnHostClicked = () => {
                if(sessionManager != null) {
                    sessionManager.HandleHostClicked();
                }
            };
            sessionManager.OnJoinClicked = code => {
                if(sessionManager != null) {
                    sessionManager.HandleJoinClicked(code);
                }
            };
            sessionManager.OnStartGameClicked = () => {
                if(gamemodeManager != null) {
                    gamemodeManager.CloseDropdown();
                }

                if(sessionManager != null) {
                    sessionManager.HandleStartGameClicked();
                }
            };
            sessionManager.OnBackFromLobbyClicked = () => {
                // Check if we should show modal
                var sessionManagerInstance = SessionManager.Instance;
                var session = sessionManagerInstance != null ? sessionManagerInstance.ActiveSession : null;

                var isHost = sessionManager != null && sessionManager.IsHost;
                var hasJoinCode = session != null && !string.IsNullOrEmpty(session.Code);
                var shouldShowModal = (session != null) && (isHost || hasJoinCode);

                if(shouldShowModal && uiManager != null) {
                    uiManager.ShowLobbyLeaveConfirmation();
                } else {
                    // Leave directly and show main menu panel
                    UISoundService.PlayButtonClick(isBack: true);
                    if(SessionManager.Instance != null) {
                        SessionManager.Instance.LeaveToMainMenuAsync(skipFade: true).Forget();
                    }

                    ShowPanel(MainMenuPanel);
                }
            };
            sessionManager.OnHostStatusChanged = (isHost, wasHost) => {
                // Notify gamemode manager when host status changes
                if(gamemodeManager != null) {
                    gamemodeManager.SetHostStatus(isHost, wasHost);
                }
            };
            sessionManager.ShouldShowLobbyLeaveModal = () => {
                var sessionManagerInstance = SessionManager.Instance;
                var session = sessionManagerInstance != null ? sessionManagerInstance.ActiveSession : null;
                if(session == null) return false;
                var isHost = sessionManager != null && sessionManager.IsHost;
                var hasJoinCode = !string.IsNullOrEmpty(session.Code);
                return isHost || hasJoinCode;
            };
        }

        private void WireGamemodeManagerEvents() {
            if(gamemodeManager == null) return;

            gamemodeManager.OnGameModeSelected = modeName => {
                if(gamemodeManager != null) {
                    gamemodeManager.HandleGameModeSelected(modeName);
                }
            };
        }

        #endregion

        #region First Time Setup

        private void CheckFirstTimeSetup() {
            if(uiManager != null) {
                uiManager.CheckFirstTimeSetup();
            }
        }

        private void OnFirstTimeSetupContinue() {
            if(uiManager == null) return;

            var playerName = uiManager.GetFirstTimeNameInput();
            if(string.IsNullOrWhiteSpace(playerName)) {
                playerName = "Player";
            }

            PlayerPrefs.SetString("PlayerName", playerName);
            PlayerPrefs.Save();

            uiManager.HideFirstTimeSetup();
            LoadSettings();
        }

        #endregion

        #region Navigation

        public void ShowPanel(VisualElement panel) {
            if(panel == null) {
                Debug.LogError("[MainMenuManager] ShowPanel called with null panel!");
                return;
            }

            if(_currentPanel == null) {
                // First time: show immediately
                foreach(var p in _panels) {
                    if(p != null && p != panel) {
                        HidePanelImmediate(p);
                    }
                }

                ShowPanelImmediate(panel);
                _currentPanel = panel;
                return;
            }

            if(panel == _currentPanel) return;

            if(_panelFadeCoroutine != null) {
                StopCoroutine(_panelFadeCoroutine);
                _panelFadeCoroutine = null;
            }

            var needFadeOut = _currentPanel != null && _currentPanel != _loadoutPanel;
            var needFadeIn = panel != _loadoutPanel;
            var requiresFade = needFadeOut || needFadeIn;

            if(!requiresFade) {
                HidePanelImmediate(_currentPanel);
                ShowPanelImmediate(panel);
                _currentPanel = panel;
                return;
            }

            _panelFadeCoroutine = StartCoroutine(FadeBetweenPanels(_currentPanel, panel));
        }

        private static void HidePanelImmediate(VisualElement panel) {
            if(panel == null) return;
            panel.AddToClassList("hidden");
            panel.style.display = StyleKeyword.Null;
            panel.style.opacity = new StyleFloat(1f);
        }

        private static void ShowPanelImmediate(VisualElement panel) {
            if(panel == null) return;
            panel.RemoveFromClassList("hidden");
            panel.style.display = DisplayStyle.Flex;
            panel.style.opacity = new StyleFloat(1f);
            panel.BringToFront();
        }

        private IEnumerator FadeBetweenPanels(VisualElement oldPanel, VisualElement newPanel) {
            // Hide all other panels immediately
            foreach(var p in _panels) {
                if(p == null) continue;
                if(p == oldPanel || p == newPanel) continue;
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
                if(fadeOutPanel != null) {
                    fadeOutPanel.style.opacity = new StyleFloat(1f - t);
                }

                if(fadeInPanel != null) {
                    fadeInPanel.style.opacity = new StyleFloat(t);
                }

                yield return null;
            }

            if(fadeOutPanel != null) {
                HidePanelImmediate(fadeOutPanel);
            }

            if(fadeInPanel != null) {
                fadeInPanel.style.opacity = new StyleFloat(1f);
                fadeInPanel.RemoveFromClassList("hidden");
                fadeInPanel.style.display = DisplayStyle.Flex;
                fadeInPanel.BringToFront();
            }

            _currentPanel = newPanel;
            _panelFadeCoroutine = null;
        }

        /// <summary>
        /// Shows the character customization panel (called from LoadoutManager).
        /// </summary>
        public void ShowCharacterCustomization() {
            if(characterCustomizationManager != null) {
                characterCustomizationManager.OnButtonClickedCallback = OnButtonClicked;
                characterCustomizationManager.MouseEnterCallback = MouseEnter;
                characterCustomizationManager.OnBackFromCustomizationCallback = () => { ShowPanel(_loadoutPanel); };
            }

            ShowPanel(_loadoutPanel);
            if(characterCustomizationManager != null) {
                characterCustomizationManager.ShowCustomization();
            }
        }

        private void OnPlayClicked() {
            // Initialize gamemode from MatchSettings or default to Deathmatch
            if(MatchSettingsManager.Instance != null) {
                var selectedGameMode = MatchSettingsManager.Instance.selectedGameModeId;
                if(string.IsNullOrEmpty(selectedGameMode)) {
                    selectedGameMode = "Deathmatch";
                    MatchSettingsManager.Instance.selectedGameModeId = selectedGameMode;
                }
            }

            // Update gamemode manager
            if(gamemodeManager != null) {
                gamemodeManager.SetDefaultGamemode("Lobby");
                gamemodeManager.ResetGamemodeUI();
            }

            // Reset lobby UI
            if(sessionManager != null) {
                sessionManager.ResetLobbyUI();
            }

            ShowPanel(_lobbyPanel);
        }

        #endregion

        #region Settings

        private void SetupOptionsMenuManager() {
            if(optionsMenuManager == null) {
                Debug.LogError("[MainMenuManager] OptionsMenuManager not assigned!");
                return;
            }

            optionsMenuManager.OnButtonClickedCallback = OnButtonClicked;
            optionsMenuManager.MouseEnterCallback = _ => UISoundService.PlayButtonHover();
            optionsMenuManager.OnBackFromOptionsCallback = () => ShowPanel(MainMenuPanel);

            optionsMenuManager.Initialize();

            if(characterCustomizationManager != null) {
                characterCustomizationManager.OnButtonClickedCallback = OnButtonClicked;
                characterCustomizationManager.MouseEnterCallback = _ => UISoundService.PlayButtonHover();
                characterCustomizationManager.OnBackFromCustomizationCallback = () => { ShowPanel(_loadoutPanel); };
            }
        }

        private void LoadSettings() {
            if(optionsMenuManager != null) {
                optionsMenuManager.LoadSettings();
            }
        }

        #endregion

        #region UI Utilities

        public void OnButtonClicked(bool isBack = false) {
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

        private void OnQuitCancelled() {
            OnButtonClicked();
            // HideQuitConfirmation is handled internally by UIManager
        }

        #endregion
    }
}