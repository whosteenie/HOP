using System;
using System.Collections;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Network;
using Network.Events;
using Network.Steam;
using Steamworks;
using Steamworks.Data;
using UnityEngine;
using UnityEngine.UIElements;
using Game.Match;
using Network.Services;

namespace Game.Menu {
    /// <summary>
    /// Manages gamemode selection for Steam Lobbies.
    /// Syncs "GameMode" key in Lobby Data.
    /// </summary>
    public class MainMenuGamemodeManager : Game.UI.UIElementBase {
        private static MainMenuGamemodeManager Instance { get; set; }
        private VisualElement _gamemodeDropdownContainer;
        private Label _gamemodeDisplayLabel;
        private VisualElement _gamemodeArrow;
        private VisualElement _gamemodeDropdownMenu;
        private bool _isGamemodeDropdownOpen;

        private string _selectedGameMode;
        private bool _isHost;

        // Events
        public Action<string> OnGameModeSelected;

        protected override void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            base.Awake();
        }

        protected override void OnInitialize() {
            FindUIElements();
            SetupGamemodeDropdown();
        }

        protected override Dictionary<string, System.Type> GetRequiredElements() {
            return new Dictionary<string, System.Type>();
        }

        protected override void OnDestroy() {
            if(Instance == this) Instance = null;
            base.OnDestroy();
        }

        protected override void OnEnable() {
            base.OnEnable();
            SteamMatchmaking.OnLobbyDataChanged += OnLobbyDataChanged;
            RegisterCleanup(() => SteamMatchmaking.OnLobbyDataChanged -= OnLobbyDataChanged);
            // Initial Check
            UpdateGamemodeFromSession();
        }

        protected override void OnDisable() {
            SteamMatchmaking.OnLobbyDataChanged -= OnLobbyDataChanged;
            base.OnDisable();
        }

        private void OnLobbyDataChanged(Lobby lobby) {
             UpdateGamemodeFromSession();
        }

        private void FindUIElements() {
            _gamemodeDropdownContainer = QOptional<VisualElement>("gamemode-dropdown-container");
            _gamemodeDisplayLabel = QOptional<Label>("gamemode-display-label");
            _gamemodeArrow = QOptional<VisualElement>("gamemode-arrow");
            _gamemodeDropdownMenu = QOptional<VisualElement>("gamemode-dropdown-menu");
        }

        private void SetupGamemodeDropdown() {
            SetupOption("gamemode-option-deathmatch", "Deathmatch");
            SetupOption("gamemode-option-team-deathmatch", "Team Deathmatch");
            SetupOption("gamemode-option-tag", "Gun Tag");
            SetupOption("gamemode-option-hopball", "Hopball");
            SetupOption("gamemode-option-koth", "KOTH");
            SetupOption("gamemode-option-private-match", "Private Match");
        }
        
        private void SetupOption(string uiName, string modeName) {
            var btn = QOptional<Button>(uiName);
            if (btn != null) {
                System.Action clickHandler = () => {
                    if (!_isHost) return;
                    UISoundService.PlayButtonClick();
                    HandleGameModeSelected(modeName);
                    OnGameModeSelected?.Invoke(modeName);
                };
                btn.clicked += clickHandler;
                RegisterCleanup(() => btn.clicked -= clickHandler);
            }
        }

        public void SetHostStatus(bool isHost, bool wasHost) {
            _isHost = isHost;

            switch(isHost) {
                case true when !wasHost: {
                    SubscribeToGamemodeEvents();
                    StartCoroutine(ShowArrowWithAnimation());
                
                    if(string.IsNullOrEmpty(_selectedGameMode) || _selectedGameMode == "Lobby") {
                        _selectedGameMode = "Deathmatch";
                    }
                    
                    // Sync initial
                    SyncGamemodeToSession(_selectedGameMode);
                    break;
                }
                case false when wasHost: {
                    UnsubscribeFromGamemodeEvents();
                    if(_gamemodeArrow != null) _gamemodeArrow.AddToClassList("hidden");
                    if(_gamemodeDropdownMenu != null) _gamemodeDropdownMenu.AddToClassList("hidden");
                    _isGamemodeDropdownOpen = false;
                    break;
                }
            }

            if(!isHost) UpdateGamemodeFromSession();
            
            if(_gamemodeDisplayLabel != null) _gamemodeDisplayLabel.SetEnabled(_isHost);
        }

        private void SubscribeToGamemodeEvents() {
            if(_gamemodeDisplayLabel == null) return;
            EventCallback<ClickEvent> handler = OnGamemodeLabelClicked;
            _gamemodeDisplayLabel.RegisterCallback(handler);
            RegisterCleanup(() => _gamemodeDisplayLabel.UnregisterCallback(handler));
        }

        private void UnsubscribeFromGamemodeEvents() {
            if(_gamemodeDisplayLabel == null) return;
            // Cleanup is handled by RegisterCleanup
        }

        private void OnGamemodeLabelClicked(ClickEvent evt) {
            if(!_isHost) return;
            UISoundService.PlayButtonClick();
            ToggleGamemodeDropdown();
        }

        private void ToggleGamemodeDropdown() {
            if(!_isHost) return;
            _isGamemodeDropdownOpen = !_isGamemodeDropdownOpen;

            if(_isGamemodeDropdownOpen) {
                if(_gamemodeDropdownMenu != null) _gamemodeDropdownMenu.RemoveFromClassList("hidden");
                if(_gamemodeArrow != null) {
                    _gamemodeArrow.RemoveFromClassList("hidden");
                    _gamemodeArrow.AddToClassList("arrow-down");
                }
                if(_gamemodeDropdownMenu != null) _gamemodeDropdownMenu.BringToFront();
            } else {
                if(_gamemodeDropdownMenu != null) _gamemodeDropdownMenu.AddToClassList("hidden");
                if(_gamemodeArrow != null) _gamemodeArrow.RemoveFromClassList("arrow-down");
            }
        }

        public void HandleGameModeSelected(string modeName) {
            _selectedGameMode = modeName;

            if(MatchSettingsManager.Instance != null) {
                MatchSettingsManager.Instance.selectedGameModeId = modeName;
            }

            UpdateGamemodeDisplay();
            ToggleGamemodeDropdown();
            SyncGamemodeToSession(modeName);
        }

        private void SyncGamemodeToSession(string gamemode) {
            if(!_isHost) return;
            if (SessionManager.Instance != null && SessionManager.Instance.CurrentLobby.HasValue) {
                SessionManager.Instance.CurrentLobby.Value.SetData("GameMode", gamemode);
            }
        }

        private void UpdateGamemodeDisplay() {
            if(_gamemodeDisplayLabel == null) return;
            _gamemodeDisplayLabel.text = _selectedGameMode ?? "Lobby";
        }

        public void UpdateGamemodeFromSession() {
            if(_isHost) return;
            
            if (SessionManager.Instance != null && SessionManager.Instance.CurrentLobby.HasValue) {
                string mode = SessionManager.Instance.CurrentLobby.Value.GetData("GameMode");
                if (!string.IsNullOrEmpty(mode) && mode != _selectedGameMode) {
                    _selectedGameMode = mode;
                    UpdateGamemodeDisplay();
                    if(MatchSettingsManager.Instance != null) {
                         MatchSettingsManager.Instance.selectedGameModeId = mode;
                    }
                }
            } else {
                 if (_selectedGameMode != "Lobby") {
                     _selectedGameMode = "Lobby";
                     UpdateGamemodeDisplay();
                 }
            }
        }
        
        public void ResetGamemodeUI() {
             _isHost = false;
             SetHostStatus(false, true);
             _selectedGameMode = "Lobby";
             UpdateGamemodeDisplay();
        }

        public void SetDefaultGamemode(string gamemode) {
            _selectedGameMode = gamemode;
            UpdateGamemodeDisplay();
        }

        public void CloseDropdown() {
             if (_isGamemodeDropdownOpen) ToggleGamemodeDropdown();
        }

        public string SelectedGameMode => _selectedGameMode;

        private IEnumerator ShowArrowWithAnimation() {
            if(_gamemodeArrow == null) yield break;
            _gamemodeArrow.RemoveFromClassList("hidden");
            yield return null;
            _gamemodeArrow.RemoveFromClassList("arrow-down");
            _gamemodeArrow.AddToClassList("arrow-slide-in");
        }
    }
}
