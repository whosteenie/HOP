using System;
using System.Collections;
using System.Collections.Generic;
using Diagnostics;
using Steamworks;
using Steamworks.Data;
using UnityEngine.UIElements;
using Game.Match;
using Game.UI.Core;
using SessionManager = Network.Session.SessionManager;

namespace Game.Menu.Main {
    /// <summary>
    /// Manages gamemode selection for Steam Lobbies.
    /// Syncs "GameMode" key in Lobby Data.
    /// </summary>
    public class MainMenuGamemodeManager : UIElementBase {
        private Label _gamemodeDisplayLabel;
        private VisualElement _gamemodeArrow;
        private VisualElement _gamemodeDropdownMenu;
        private bool _isGamemodeDropdownOpen;

        private bool _isHost;

        protected override void OnInitialize() {
            FindUIElements();
            SetupGamemodeDropdown();
        }

        protected override Dictionary<string, Type> GetRequiredElements() {
            return new Dictionary<string, Type>();
        }

        protected override void OnEnable() {
            base.OnEnable();
            SteamMatchmaking.OnLobbyDataChanged -= OnLobbyDataChanged;
            SteamMatchmaking.OnLobbyDataChanged += OnLobbyDataChanged;
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
            QOptional<VisualElement>("gamemode-dropdown-container");
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
        }
        
        private void SetupOption(string uiName, string modeName) {
            var btn = QOptional<Button>(uiName);
            if(btn == null) return;
            Action clickHandler = () => {
                if (!_isHost) return;
                UISound.PlayButtonClick();
                HandleGamemodeSelected(modeName);
            };
            btn.clicked += clickHandler;
            RegisterCleanup(() => btn.clicked -= clickHandler);
        }

        public void SetHostStatus(bool isHost, bool wasHost) {
            _isHost = isHost;

            switch(isHost) {
                case true when !wasHost: {
                    SubscribeToGamemodeEvents();
                    StartCoroutine(ShowArrowWithAnimation());
                
                    if(string.IsNullOrEmpty(SelectedGameMode) || SelectedGameMode == "Lobby") {
                        SelectedGameMode = "Deathmatch";
                    }
                    
                    // Sync initial
                    SyncGamemodeToSession(SelectedGameMode);
                    break;
                }
                case false when wasHost: {
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

        private void OnGamemodeLabelClicked(ClickEvent evt) {
            if(!_isHost) return;
            UISound.PlayButtonClick();
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

        private void HandleGamemodeSelected(string modeName) {
            SelectedGameMode = modeName;
            FlowLog.Emit(FlowEventIds.ModeSelect,
                ("selectedMode", modeName),
                ("isHost", _isHost));

            if(MatchSettingsManager.Instance != null) {
                MatchSettingsManager.Instance.selectedGameModeId = modeName;
            }

            UpdateGamemodeDisplay();
            ToggleGamemodeDropdown();
            SyncGamemodeToSession(modeName);
        }

        private void SyncGamemodeToSession(string gamemode) {
            if(!_isHost) return;
            if(SessionManager.Instance != null) {
                SessionManager.Instance.SetGameMode(gamemode);
            }
        }

        private void UpdateGamemodeDisplay() {
            if(_gamemodeDisplayLabel == null) return;
            _gamemodeDisplayLabel.text = SelectedGameMode ?? "Lobby";
        }

        private void UpdateGamemodeFromSession() {
            if(_isHost) return;
            
            if(SessionManager.Instance != null && SessionManager.Instance.CurrentLobby.HasValue) {
                var mode = SessionManager.Instance.CurrentLobby.Value.GetData("TargetMode");
                if(string.IsNullOrEmpty(mode) || mode == SelectedGameMode) return;
                SelectedGameMode = mode;
                UpdateGamemodeDisplay();
                if(MatchSettingsManager.Instance != null) {
                    MatchSettingsManager.Instance.selectedGameModeId = mode;
                }
            } else {
                if(SelectedGameMode == "Lobby") return;
                SelectedGameMode = "Lobby";
                UpdateGamemodeDisplay();
            }
        }
        
        private string SelectedGameMode { get; set; }

        private IEnumerator ShowArrowWithAnimation() {
            if(_gamemodeArrow == null) yield break;
            _gamemodeArrow.RemoveFromClassList("hidden");
            yield return null;
            _gamemodeArrow.RemoveFromClassList("arrow-down");
            _gamemodeArrow.AddToClassList("arrow-slide-in");
        }
    }
}

