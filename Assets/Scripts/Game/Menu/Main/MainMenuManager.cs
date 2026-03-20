using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Diagnostics;
using Events;
using Game.Menu.Loadout;
using Game.Social;
using Game.Match;
using Game.Menu.PrivateMatch;
using Game.Menu.Shared;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.UIElements;
using Game.Menu.Options;
using Game.Settings;
using Game.UI.Core;
using Game.UI.Misc;
using Game.UI.Screens;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using Cursor = UnityEngine.Cursor;
using SessionManager = Network.Session.SessionManager;

namespace Game.Menu.Main {
    /// <summary>
    /// Main coordinator for the main menu system.
    /// Delegates UI, session, and gamemode management to specialized sub-managers.
    /// Steamworks Integrated.
    /// </summary>
    public class MainMenuManager : UIElementBase {
        private enum MainMenuPanelState {
            MainMenu,
            GamemodeSelect,
            PrivateMatchSetup,
            Lobby,
            Loadout,
            Options,
            Credits
        }

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
        [SerializeField] private MainMenuPrivateMatchSetupManager privateMatchSetupManager;
        [SerializeField] private VoiceOverlayManager voiceOverlayManager;

        [Header("References")]
        [SerializeField] private Camera mainCamera;
        [SerializeField] private MenuBlurVolumeController menuBlurController;
        [SerializeField] private Volume optionsBlurVolume;
        [SerializeField] private MainMenuBackgroundRandomizer backgroundRandomizer;

        #endregion

        #region UI Elements - Panels

        private VisualElement MainMenuPanel { get; set; }
        private VisualElement _gamemodePanel;
        private VisualElement _lobbyPanel;
        private VisualElement _loadoutPanel;
        private VisualElement _optionsPanel;
        private VisualElement _creditsPanel;
        private VisualElement _privateMatchSetupPanel;
        private List<VisualElement> _panels;
        private const float PanelFadeDuration = 0.08f;
        private MainMenuPanelState _currentPanelState = MainMenuPanelState.MainMenu;
        private MainMenuPanelState _stateBeforeOptions = MainMenuPanelState.MainMenu;

        // Shared UI navigation helper for main menu panels
        private UINavigator _navigator;
        private bool _navigatorMissingLogged;

        // Direct back-button wiring so private match Back always returns to Gamemode Select
        private Button _privateMatchBackButton;
        private Action _privateMatchBackClickHandler;
        private bool _backInputEventsBound;
        private int _lastBackHandledFrame = -1;

        #endregion

        #region Unity Lifecycle

        protected override void Start() {
            base.Start();
            InitializeMenuBlurController();
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;

            SetupOptionsMenuManager();
            LoadSettings();

            if(backgroundRandomizer == null) {
                backgroundRandomizer = GetComponent<MainMenuBackgroundRandomizer>();
            }
            if(backgroundRandomizer != null) {
                var backgroundSelection = GameSettings.Data.video?.mainMenuBackgroundSelection;
                backgroundRandomizer.ApplySelection(backgroundSelection);
            }

            if(uiManager != null) MainMenuUIManager.InitializeGameMenuVisibility();
            if(sessionManager != null) sessionManager.Initialize();
            if(voiceOverlayManager != null) voiceOverlayManager.Initialize(Root);

            if(DiscordManager.Instance != null) {
                DiscordManager.Instance.SetStatus("In Main Menu", "Browsing");
            }
        }

        protected void OnDisable() {
            UnbindBackInputEvents();
            SetOptionsOpenState(false, false);
        }

        private void OnEnable() {
            BindBackInputEvents();
        }

        protected override void OnDestroy() {
            UnbindBackInputEvents();
            if(_privateMatchBackButton != null && _privateMatchBackClickHandler != null) {
                _privateMatchBackButton.clicked -= _privateMatchBackClickHandler;
            }
            base.OnDestroy();
        }

        private void Update() {
            var keyboard = Keyboard.current;
            if(keyboard == null || !keyboard.escapeKey.wasPressedThisFrame) return;
            TryHandleBackInputThisFrame();
        }

        protected override void OnInitialize() {
            InitializeSubManagers();
            FindPanels();
            InitializeNavigator();
            RegisterBackKeyHandler();
            WireSubManagerCallbacks();
        }

        protected override Dictionary<string, Type> GetRequiredElements() {
            return new Dictionary<string, Type> {
                { "main-menu-panel", typeof(VisualElement) }
            };
        }

        private void RegisterBackKeyHandler() {
            if(Root == null) return;
            Root.RegisterCallback<KeyDownEvent>(OnRootKeyDown, TrickleDown.TrickleDown);
            RegisterCleanup(() => {
                if(Root != null) {
                    Root.UnregisterCallback<KeyDownEvent>(OnRootKeyDown, TrickleDown.TrickleDown);
                }
            });
        }

        private void OnRootKeyDown(KeyDownEvent evt) {
            if(evt == null || evt.keyCode != KeyCode.Escape) return;
            TryHandleBackInputThisFrame();
            evt.StopPropagation();
        }

        private void TryHandleBackInputThisFrame() {
            var currentFrame = Time.frameCount;
            if(_lastBackHandledFrame == currentFrame) return;
            _lastBackHandledFrame = currentFrame;
            HandleEscapeBackRequest();
        }

        private void BindBackInputEvents() {
            if(_backInputEventsBound) return;
            EventBus.Subscribe<MainMenuBackRequestedEvent>(OnMainMenuBackRequested);
            _backInputEventsBound = true;
        }

        private void UnbindBackInputEvents() {
            if(!_backInputEventsBound) return;
            EventBus.Unsubscribe<MainMenuBackRequestedEvent>(OnMainMenuBackRequested);
            _backInputEventsBound = false;
        }

        private void OnMainMenuBackRequested(MainMenuBackRequestedEvent _) {
            TryHandleBackInputThisFrame();
        }

        private void WireSubManagerCallbacks() {
            if(uiManager != null) WireUIManagerEvents();
            if(sessionManager != null) WireSessionManagerEvents();
            if(privateMatchSetupManager != null) WirePrivateSetupManagerEvents();
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
            _privateMatchSetupPanel = QOptional<VisualElement>("private-match-setup-panel");

            // Wire private match Back directly so it always returns to Gamemode Select (WS-A back behavior)
            _privateMatchBackButton = _privateMatchSetupPanel?.Q<Button>("private-match-back-button");
            if(_privateMatchBackButton != null) {
                _privateMatchBackClickHandler = () => {
                    UISound.PlayButtonClick(isBack: true);
                    TransitionToState(MainMenuPanelState.GamemodeSelect);
                    if(privateMatchSetupManager != null) privateMatchSetupManager.OnBackRequested?.Invoke();
                };
                _privateMatchBackButton.clicked += _privateMatchBackClickHandler;
            }

            _panels = new List<VisualElement> {
                MainMenuPanel,
                _gamemodePanel,
                uiManager.PlayGamemodePanel,
                _lobbyPanel,
                _loadoutPanel,
                _optionsPanel,
                _creditsPanel,
                _privateMatchSetupPanel
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
            var loadoutManager = uiManager != null ? uiManager.loadoutManager : null;

            if(uiManager != null) {
                if(uiManager.uiDocument == null) uiManager.uiDocument = uiDocument;
            }
            if(sessionManager != null) {
                if(sessionManager.uiDocument == null) sessionManager.uiDocument = uiDocument;
            }
            if(gamemodeManager && gamemodeManager.uiDocument == null) {
                gamemodeManager.uiDocument = uiDocument;
            }
            if(privateMatchSetupManager == null) return;
            if(privateMatchSetupManager.uiDocument == null)
                privateMatchSetupManager.uiDocument = uiDocument;
            // Force init so dropdowns are bound regardless of script execution order (setup panel may be hidden at Start).
            if(Root != null)
                privateMatchSetupManager.Initialize(Root);
            if(sessionManager != null) {
                privateMatchSetupManager.SetSessionHooks(
                    () => sessionManager.IsHost,
                    sessionManager.ShowContextMenuForPartyMember);
            }

            if(loadoutManager != null) {
                loadoutManager.ConfigureMainMenuHooks(
                    uiDocument,
                    () => OnButtonClicked(),
                    () => OnButtonClicked(true),
                    MouseEnter,
                    () => ShowPanel(MainMenuPanel));
            }
        }

        private void ShowLoadoutPanel() {
            if(uiManager != null && uiManager.loadoutManager != null)
                uiManager.loadoutManager.ShowLoadout();

            TransitionToState(MainMenuPanelState.Loadout);
        }

        private void ShowProfileView(ulong steamId, string playerName, bool isEditable) {
            if(uiManager != null && uiManager.loadoutManager != null) 
                uiManager.loadoutManager.ShowProfileView(steamId, playerName, isEditable);

            TransitionToState(MainMenuPanelState.Loadout);
        }

        private void WireUIManagerEvents() {
            if(uiManager == null) return;

            uiManager.OnPlayMatchmakingClicked = () => TransitionToState(MainMenuPanelState.GamemodeSelect);
            uiManager.OnGamemodePrivateMatchClicked = () => {
                if(_privateMatchSetupPanel == null) {
                    return;
                }

                TransitionToState(MainMenuPanelState.PrivateMatchSetup);
            };
            uiManager.OnGamemodeWipClicked = () => {
                if(uiManager != null) {
                    uiManager.ShowToast("WIP");
                }
            };
            // Gamemode cards always mean public queue. Only the "Private Match" side action opens the private match panel.
            uiManager.OnGamemodeSelected = mode => {
                MainMenuSessionManager.HandleGamemodeSelected(mode);
                sessionManager.HandleFindGameClicked(mode).Forget();
                TransitionToState(MainMenuPanelState.MainMenu);
            };
            uiManager.OnGamemodeDropdownClicked = () => sessionManager.ToggleGamemodeDropdown();
            uiManager.OnCancelMatchmakingClicked = () => sessionManager.HandleCancelMatchmakingClicked();
            uiManager.OnLoadoutClicked = ShowLoadoutPanel;
            uiManager.OnOptionsClicked = () => {
                if(optionsMenuManager != null) {
                    optionsMenuManager.OnBackFromOptionsCallback = ReturnToMainMenuFromOptions;
                    optionsMenuManager.LoadSettings();
                    optionsMenuManager.OnOptionsPanelShown();
                }
                _stateBeforeOptions = _currentPanelState;
                TransitionToState(MainMenuPanelState.Options);
            };
            uiManager.OnCreditsClicked = () => TransitionToState(MainMenuPanelState.Credits);
            uiManager.OnQuitConfirmed = OnQuitConfirmed;
            uiManager.OnQuitCancelled = OnQuitCancelled;
            uiManager.OnLobbyLeaveConfirmed = () => {
                if(SessionManager.Instance != null) {
                    SessionManager.Instance.LeaveToMainMenuAsync().Forget(); // Removed skipFade param if not supported? Or keep if overload exists. Assuming default.
                }
                TransitionToState(MainMenuPanelState.MainMenu);
            };
            uiManager.OnLobbyLeaveCancelled = () => { };
            uiManager.OnShowPanel = ShowPanel;
        }

        private void WirePrivateSetupManagerEvents() {
            privateMatchSetupManager.OnBackRequested = () => TransitionToState(MainMenuPanelState.GamemodeSelect);
            privateMatchSetupManager.OnStartRequested = draft => {
                if(string.IsNullOrWhiteSpace(draft.GamemodeId)) {
                    if(uiManager != null) {
                        uiManager.ShowToast("Select a gamemode first.");
                    }
                    return;
                }
                if(sessionManager == null) {
                    DevLog.LogError("[MainMenuManager] MainMenuSessionManager is missing; cannot start private match.");
                    return;
                }

                sessionManager.HandlePrivateMatchSelection(
                    draft.GamemodeId,
                    draft.MapId,
                    draft.MatchTimerSeconds,
                    draft.UsePreMatchCountdown,
                    draft.SwapWeaponsOnDeath,
                    draft.ScoreToWin,
                    draft.KothHillSpeed,
                    draft.TaggedPlayers,
                    privateMatchSetupManager.GetDraftTeamAssignments()).Forget();
            };
        }

        private void WireSessionManagerEvents() {
            if(sessionManager == null) return;

            sessionManager.OnHostStatusChanged = (isHost, wasHost) => {
                if(gamemodeManager != null) gamemodeManager.SetHostStatus(isHost, wasHost);
            };
            sessionManager.OnLocalProfileRequested = ShowLoadoutPanel;
            sessionManager.OnProfileRequested = ShowProfileView;
            sessionManager.ShouldShowSwitchTeamInContextMenu = () =>
                _currentPanelState == MainMenuPanelState.PrivateMatchSetup &&
                privateMatchSetupManager != null &&
                MatchSettingsManager.IsTeamBasedMode(privateMatchSetupManager.GetDraftSettings().GamemodeId);
            sessionManager.OnSwitchTeamRequested = steamId => {
                if(privateMatchSetupManager != null) privateMatchSetupManager.SwitchPlayerTeam(steamId);
            };
        }

        #endregion

        #region Navigation

        private void HandleEscapeBackRequest() {
            switch(_currentPanelState) {
                case MainMenuPanelState.Options:
                    if(optionsMenuManager != null) {
                        optionsMenuManager.HandleBackRequest();
                    }
                    return;
                case MainMenuPanelState.Loadout:
                    if(uiManager != null && uiManager.loadoutManager != null) {
                        uiManager.loadoutManager.HandleBackRequest();
                    }
                    return;
                case MainMenuPanelState.PrivateMatchSetup:
                    if(privateMatchSetupManager == null) return;
                    UISound.PlayButtonClick(isBack: true);
                    if(privateMatchSetupManager.OnBackRequested != null) {
                        privateMatchSetupManager.OnBackRequested.Invoke();
                    }
                    return;
                case MainMenuPanelState.GamemodeSelect:
                case MainMenuPanelState.Credits:
                    UISound.PlayButtonClick(isBack: true);
                    TransitionToState(MainMenuPanelState.MainMenu);
                    return;
                case MainMenuPanelState.Lobby:
                    if(uiManager != null && uiManager.HandleLobbyBackRequest()) {
                        return;
                    }
                    UISound.PlayButtonClick(isBack: true);
                    TransitionToState(MainMenuPanelState.MainMenu);
                    return;
                case MainMenuPanelState.MainMenu:
                    if(uiManager != null) {
                        uiManager.HandleMainMenuBackRequest();
                    }
                    return;
                default:
                    return;
            }
        }

        private void TransitionToState(MainMenuPanelState state) {
            while(true) {
                var panel = GetPanelForState(state);
                if(panel == null) {
                    if(state == MainMenuPanelState.MainMenu) return;
                    DevLog.LogWarning($"[MainMenuManager] Panel for state '{state}' is missing. Falling back to MainMenu.");
                    state = MainMenuPanelState.MainMenu;
                    continue;
                }

                _currentPanelState = state;
                ShowPanelInternal(panel);

                if(state != MainMenuPanelState.PrivateMatchSetup) return;
                if(privateMatchSetupManager != null) privateMatchSetupManager.RefreshDropdowns();

                if(privateMatchSetupManager != null) privateMatchSetupManager.RefreshTeamPreview();
                break;
            }
        }

        private VisualElement GetPanelForState(MainMenuPanelState state) {
            if(state != MainMenuPanelState.GamemodeSelect)
                return state switch {
                    MainMenuPanelState.MainMenu => MainMenuPanel,
                    MainMenuPanelState.PrivateMatchSetup => _privateMatchSetupPanel,
                    MainMenuPanelState.Lobby => _lobbyPanel,
                    MainMenuPanelState.Loadout => _loadoutPanel,
                    MainMenuPanelState.Options => _optionsPanel,
                    MainMenuPanelState.Credits => _creditsPanel,
                    _ => MainMenuPanel
                };
            var panel = uiManager != null ? uiManager.PlayGamemodePanel : null;
            if(panel == null && Root != null)
                panel = Root.Q<VisualElement>("play-gamemode-panel");
            return panel;
        }

        private MainMenuPanelState GetStateForPanel(VisualElement panel) {
            if(panel == null) return _currentPanelState;
            if(panel == MainMenuPanel) return MainMenuPanelState.MainMenu;
            if(uiManager != null && panel == uiManager.PlayGamemodePanel) return MainMenuPanelState.GamemodeSelect;
            if(panel == _privateMatchSetupPanel) return MainMenuPanelState.PrivateMatchSetup;
            if(panel == _lobbyPanel) return MainMenuPanelState.Lobby;
            if(panel == _loadoutPanel) return MainMenuPanelState.Loadout;
            if(panel == _optionsPanel) return MainMenuPanelState.Options;
            return panel == _creditsPanel ? MainMenuPanelState.Credits : _currentPanelState;
        }

        private void ShowPanel(VisualElement panel) {
            if(panel == null) return;
            _currentPanelState = GetStateForPanel(panel);
            ShowPanelInternal(panel);
        }

        private void ShowPanelInternal(VisualElement panel) {
            if(_navigator == null) {
                if(_navigatorMissingLogged) return;
                DevLog.LogError(
                    "[MainMenuManager] UI navigator is not initialized; cannot show panel. " +
                    "Check OnInitialize/FindPanels/InitializeNavigator execution order.",
                    this);
                _navigatorMissingLogged = true;
                return;
            }

            _navigatorMissingLogged = false;
            _navigator.Show(panel);
            if(backgroundRandomizer != null) {
                var suppressBackgroundDepthOfField = panel == _loadoutPanel;
                backgroundRandomizer.SetDepthOfFieldSuppressed(suppressBackgroundDepthOfField);
            }

            var useMenuOverlay = panel == _optionsPanel || panel == _privateMatchSetupPanel;
            var isPrivateSetupOpen = panel == _privateMatchSetupPanel;
            SetOptionsOpenState(useMenuOverlay, isPrivateSetupOpen);
            UpdateDiscordStatusForPanel(panel);
            
            // Refresh challenges when main menu panel is shown
            if(panel == MainMenuPanel && uiManager != null) {
                uiManager.RefreshMainMenuChallenges();
            }
        }

        private void SetOptionsOpenState(bool isOptionsOpen, bool isPrivateSetupOpen) {
            if(Root == null) return;

            var panelRoot = Root.panel?.visualTree;

            // Apply to both document root and panel root.
            // Dropdown popup menus are attached under panel root, not under this UIDocument root.
            SetClassState(Root, "options-open", isOptionsOpen);
            SetClassState(panelRoot, "options-open", isOptionsOpen);
            SetClassState(Root, "private-match-open", isPrivateSetupOpen);
            SetClassState(panelRoot, "private-match-open", isPrivateSetupOpen);

            if(menuBlurController != null) {
                menuBlurController.SetBlurActive(isOptionsOpen);
            }

            return;

            static void SetClassState(VisualElement element, string className, bool enabled) {
                if(element == null) return;
                if(enabled) {
                    element.AddToClassList(className);
                } else {
                    element.RemoveFromClassList(className);
                }
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
            else if(panel == _gamemodePanel || (uiManager != null && panel == uiManager.PlayGamemodePanel))
                DiscordManager.Instance.SetStatus("In Main Menu", "Selecting Gamemode");
            else if(panel == _privateMatchSetupPanel) DiscordManager.Instance.SetStatus("In Main Menu", "Configuring Private Match");
            else if(panel == _loadoutPanel) DiscordManager.Instance.SetStatus("In Main Menu", "Editing Loadout");
        }
        #endregion

        #region Settings

        private void SetupOptionsMenuManager() {
            if(optionsMenuManager == null) return;

            optionsMenuManager.OnButtonClickedCallback = OnButtonClicked;
            optionsMenuManager.MouseEnterCallback = _ => UISound.PlayButtonHover();
            optionsMenuManager.OnBackFromOptionsCallback = ReturnToMainMenuFromOptions;
            optionsMenuManager.Initialize();

            if(characterCustomizationManager == null) return;
            {
                characterCustomizationManager.OnButtonClickedCallback = OnButtonClicked;
                characterCustomizationManager.MouseEnterCallback = _ => UISound.PlayButtonHover();
                characterCustomizationManager.OnBackFromCustomizationCallback =
                    () => TransitionToState(MainMenuPanelState.Loadout);
            }
        }

        private void LoadSettings() {
            if(optionsMenuManager != null) optionsMenuManager.LoadSettings();
        }

        private void ReturnToMainMenuFromOptions() {
            if(_stateBeforeOptions == MainMenuPanelState.Options) {
                _stateBeforeOptions = MainMenuPanelState.MainMenu;
            }
            TransitionToState(_stateBeforeOptions);
        }

        #endregion

        #region UI Utilities

        private static void OnButtonClicked(bool isBack = false) {
            UISound.PlayButtonClick(isBack);
        }

        private static void MouseEnter(MouseEnterEvent evt) {
            UISound.PlayButtonHover();
        }

        #endregion

        #region Quit Confirmation

        private static void OnQuitConfirmed() {
            DevLog.Log("Quitting game...");
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
