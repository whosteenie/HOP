using System;
using System.Collections;
using Cysharp.Threading.Tasks;
using Network;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.UIElements;

using Game.Match;
using Network.Services;

namespace Game.Menu {
    /// <summary>
    /// Manages gamemode selection, dropdown UI, and session property syncing for the main menu.
    /// Handles all gamemode-related functionality.
    /// </summary>
    public class MainMenuGamemodeManager : MonoBehaviour {
        [Header("References")]
        public UIDocument uiDocument;

        private VisualElement _root;
        private VisualElement _gamemodeDropdownContainer;
        private Label _gamemodeDisplayLabel;
        private VisualElement _gamemodeArrow;
        private VisualElement _gamemodeDropdownMenu;
        private bool _isGamemodeDropdownOpen;

        private string _selectedGameMode;
        private bool _isHost;

        // Events
        public Action<string> OnGameModeSelected;
        public Action<bool> OnHostStatusChanged; // bool is new host status

        private void Awake() {
            if(uiDocument == null) {
                Debug.LogError("[MainMenuGamemodeManager] UIDocument is not assigned!");
                return;
            }

            _root = uiDocument.rootVisualElement;
            FindUIElements();
            SetupGamemodeDropdown();
        }

        private void OnEnable() {
            // Hook into session property changes
            HookSessionPropertyChanges();
        }

        private void OnDisable() {
            UnhookSessionPropertyChanges();
        }

        private void HookSessionPropertyChanges() {
            var sessionManager = SessionManager.Instance;
            var session = sessionManager != null ? sessionManager.ActiveSession : null;
            if(session != null) {
                session.SessionPropertiesChanged += OnSessionPropertiesChanged;
            }
        }

        private void UnhookSessionPropertyChanges() {
            var sessionManager = SessionManager.Instance;
            var session = sessionManager != null ? sessionManager.ActiveSession : null;
            if(session != null) {
                session.SessionPropertiesChanged -= OnSessionPropertiesChanged;
            }
        }

        private void OnSessionPropertiesChanged() {
            UpdateGamemodeFromSession();
        }

        private void FindUIElements() {
            _gamemodeDropdownContainer = _root.Q<VisualElement>("gamemode-dropdown-container");
            _gamemodeDisplayLabel = _root.Q<Label>("gamemode-display-label");
            _gamemodeArrow = _root.Q<VisualElement>("gamemode-arrow");
            _gamemodeDropdownMenu = _root.Q<VisualElement>("gamemode-dropdown-menu");
        }

        private void SetupGamemodeDropdown() {
            var deathmatchOption = _root.Q<Button>("gamemode-option-deathmatch");
            var teamDeathmatchOption = _root.Q<Button>("gamemode-option-team-deathmatch");
            var tagOption = _root.Q<Button>("gamemode-option-tag");
            var hopballOption = _root.Q<Button>("gamemode-option-hopball");
            var privateMatchOption = _root.Q<Button>("gamemode-option-private-match");

            if(deathmatchOption != null) {
                deathmatchOption.clicked += () => {
                    if(!_isHost) return;
                    UISoundService.PlayButtonClick();
                    OnGameModeSelected?.Invoke("Deathmatch");
                };
                deathmatchOption.RegisterCallback<MouseEnterEvent>(_ => {
                    if(_isHost) {
                        UISoundService.PlayButtonHover();
                    }
                });
            }

            if(teamDeathmatchOption != null) {
                teamDeathmatchOption.clicked += () => {
                    if(!_isHost) return;
                    UISoundService.PlayButtonClick();
                    OnGameModeSelected?.Invoke("Team Deathmatch");
                };
                teamDeathmatchOption.RegisterCallback<MouseEnterEvent>(_ => {
                    if(_isHost) {
                        UISoundService.PlayButtonHover();
                    }
                });
            }

            if(tagOption != null) {
                tagOption.clicked += () => {
                    if(!_isHost) return;
                    UISoundService.PlayButtonClick();
                    OnGameModeSelected?.Invoke("Gun Tag");
                };
                tagOption.RegisterCallback<MouseEnterEvent>(_ => {
                    if(_isHost) {
                        UISoundService.PlayButtonHover();
                    }
                });
            }

            if(hopballOption != null) {
                hopballOption.clicked += () => {
                    if(!_isHost) return;
                    UISoundService.PlayButtonClick();
                    OnGameModeSelected?.Invoke("Hopball");
                };
                hopballOption.RegisterCallback<MouseEnterEvent>(_ => {
                    if(_isHost) {
                        UISoundService.PlayButtonHover();
                    }
                });
            }

            if(privateMatchOption != null) {
                privateMatchOption.clicked += () => {
                    if(!_isHost) return;
                    UISoundService.PlayButtonClick();
                    OnGameModeSelected?.Invoke("Private Match");
                };
                privateMatchOption.RegisterCallback<MouseEnterEvent>(_ => {
                    if(_isHost) {
                        UISoundService.PlayButtonHover();
                    }
                });
            }
        }

        public void SetHostStatus(bool isHost, bool wasHost) {
            _isHost = isHost;

            switch(isHost) {
                case true when !wasHost: {
                    // Just became host - subscribe to events and show arrow
                    SubscribeToGamemodeEvents();
                    StartCoroutine(ShowArrowWithAnimation());
                
                    // Set default gamemode to "Deathmatch" when becoming host (if not already set to a valid gamemode)
                    if(string.IsNullOrEmpty(_selectedGameMode) || _selectedGameMode == "Lobby") {
                        _selectedGameMode = "Deathmatch";
                    }
                
                    // Update MatchSettings
                    if(MatchSettingsManager.Instance != null) {
                        var settings = MatchSettingsManager.Instance;
                        settings.selectedGameModeId = _selectedGameMode;
                        settings.matchDurationSeconds = GetMatchDurationForMode(_selectedGameMode);
                    }
                
                    // Update display immediately to show the selected gamemode
                    if(_gamemodeDisplayLabel != null) {
                        _gamemodeDisplayLabel.text = _selectedGameMode;
                    }
                
                    // Sync initial gamemode to session
                    SyncGamemodeToSessionAsync(_selectedGameMode).Forget();
                    break;
                }
                case false when wasHost: {
                    // No longer host - unsubscribe and hide arrow
                    UnsubscribeFromGamemodeEvents();
                    if(_gamemodeArrow != null) {
                        _gamemodeArrow.AddToClassList("hidden");
                    }
                    if(_gamemodeDropdownMenu != null) {
                        _gamemodeDropdownMenu.AddToClassList("hidden");
                    }
                    _isGamemodeDropdownOpen = false;
                    if(_gamemodeDisplayLabel != null) {
                        _gamemodeDisplayLabel.RemoveFromClassList("gamemode-hover");
                        _gamemodeDisplayLabel.RemoveFromClassList("gamemode-clicked");
                    }

                    break;
                }
            }

            // Update display
            if(!isHost) {
                UpdateGamemodeFromSession();
            }

            // Enable/disable dropdown interaction
            if(_gamemodeDisplayLabel != null) {
                _gamemodeDisplayLabel.SetEnabled(_isHost);
            }

            // Hide dropdown if not host
            if(!_isHost && _gamemodeDropdownMenu != null) {
                _gamemodeDropdownMenu.AddToClassList("hidden");
            }

            if(_isHost || _gamemodeArrow == null) return;
            _gamemodeArrow.RemoveFromClassList("arrow-down");
            _gamemodeArrow.RemoveFromClassList("arrow-slide-in");
        }

        private void SubscribeToGamemodeEvents() {
            if(_gamemodeDisplayLabel == null) return;
            _gamemodeDisplayLabel.RegisterCallback<ClickEvent>(OnGamemodeLabelClicked);
            _gamemodeDisplayLabel.RegisterCallback<MouseEnterEvent>(OnGamemodeMouseEnter);
            _gamemodeDisplayLabel.RegisterCallback<MouseLeaveEvent>(OnGamemodeMouseLeave);
        }

        private void UnsubscribeFromGamemodeEvents() {
            if(_gamemodeDisplayLabel == null) return;
            _gamemodeDisplayLabel.UnregisterCallback<ClickEvent>(OnGamemodeLabelClicked);
            _gamemodeDisplayLabel.UnregisterCallback<MouseEnterEvent>(OnGamemodeMouseEnter);
            _gamemodeDisplayLabel.UnregisterCallback<MouseLeaveEvent>(OnGamemodeMouseLeave);
        }

        private void OnGamemodeMouseEnter(MouseEnterEvent evt) {
            if(!_isHost) return;
            if(_gamemodeDisplayLabel != null) {
                _gamemodeDisplayLabel.AddToClassList("gamemode-hover");
            }
            UISoundService.PlayButtonHover();
        }

        private void OnGamemodeMouseLeave(MouseLeaveEvent evt) {
            if(_isHost && _gamemodeDisplayLabel != null) {
                _gamemodeDisplayLabel.RemoveFromClassList("gamemode-hover");
            }
        }

        private void OnGamemodeLabelClicked(ClickEvent evt) {
            if(!_isHost) return;

            UISoundService.PlayButtonClick();

            // Add click feedback
            if(_gamemodeDisplayLabel != null) {
                _gamemodeDisplayLabel.AddToClassList("gamemode-clicked");
            }

            StartCoroutine(RemoveClickFeedback());
            ToggleGamemodeDropdown();
        }

        private IEnumerator RemoveClickFeedback() {
            yield return new WaitForSeconds(0.15f);
            if(_gamemodeDisplayLabel != null) {
                _gamemodeDisplayLabel.RemoveFromClassList("gamemode-clicked");
            }
        }

        private void ToggleGamemodeDropdown() {
            if(!_isHost) return;

            _isGamemodeDropdownOpen = !_isGamemodeDropdownOpen;

            if(_isGamemodeDropdownOpen) {
                if(_gamemodeDropdownMenu != null) {
                    _gamemodeDropdownMenu.RemoveFromClassList("hidden");
                }
                if(_gamemodeArrow != null) {
                    _gamemodeArrow.RemoveFromClassList("hidden");
                    _gamemodeArrow.AddToClassList("arrow-down");
                }

                // Position dropdown below container
                if(_gamemodeDropdownMenu != null && _gamemodeDropdownContainer != null) {
                    var containerWorldPos = _gamemodeDropdownContainer.worldBound.position;
                    var containerLocalPos = _gamemodeDropdownContainer.parent.WorldToLocal(containerWorldPos);
                    _gamemodeDropdownMenu.style.left = containerLocalPos.x;
                    _gamemodeDropdownMenu.style.top =
                        containerLocalPos.y + _gamemodeDropdownContainer.resolvedStyle.height + 8f;
                }

                if(_gamemodeDropdownMenu != null) {
                    _gamemodeDropdownMenu.BringToFront();
                }
            } else {
                if(_gamemodeDropdownMenu != null) {
                    _gamemodeDropdownMenu.AddToClassList("hidden");
                }
                if(_gamemodeArrow != null) {
                    _gamemodeArrow.RemoveFromClassList("arrow-down");
                }
            }
        }

        public void HandleGameModeSelected(string modeName) {
            // If clicking the same gamemode, just close the dropdown
            if(_selectedGameMode == modeName && _isGamemodeDropdownOpen) {
                ToggleGamemodeDropdown();
                return;
            }

            _selectedGameMode = modeName;

            if(MatchSettingsManager.Instance != null) {
                var settings = MatchSettingsManager.Instance;
                settings.selectedGameModeId = modeName;
                settings.matchDurationSeconds = GetMatchDurationForMode(modeName);
            }

            UpdateGamemodeDisplay();
            ToggleGamemodeDropdown();

            // Sync gamemode to session properties in background
            SyncGamemodeToSessionAsync(modeName).Forget();
        }

        private static int GetMatchDurationForMode(string modeName) {
            return modeName switch {
                "Deathmatch" => 600, // 10 min
                "Team Deathmatch" => 900, // 15 min
                "Gun Tag" => 300, // 5 min
                "Hopball" => 1200, // 20 min
                "Private Match" => 30,
                _ => MatchSettingsManager.Instance != null
                    ? MatchSettingsManager.Instance.defaultMatchDurationSeconds
                    : 600
            };
        }

        private async UniTask SyncGamemodeToSessionAsync(string gamemode) {
            var sessionManager = SessionManager.Instance;
            var session = sessionManager != null ? sessionManager.ActiveSession : null;
            if(session == null || !_isHost) return;

            try {
                var host = session.AsHost();
                if(host != null) {
                    host.SetProperty("gamemode", new SessionProperty(gamemode, VisibilityPropertyOptions.Member));
                    await host.SavePropertiesAsync();
                }
            } catch(Exception e) {
                Debug.LogWarning($"[MainMenuGamemodeManager] Failed to sync gamemode to session: {e.Message}");
            }
        }

        private void UpdateGamemodeDisplay() {
            if(_gamemodeDisplayLabel == null) return;
            if(_isHost) {
                _gamemodeDisplayLabel.text = _selectedGameMode != null ? _selectedGameMode : "Deathmatch";
            } else {
                _gamemodeDisplayLabel.text = "Lobby";
            }
        }

        private void UpdateGamemodeFromSession() {
            var sessionManager = SessionManager.Instance;
            var session = sessionManager != null ? sessionManager.ActiveSession : null;
            if(session == null || _isHost) return;

            // Try to get gamemode from session properties
            if(session.Properties.TryGetValue("gamemode", out var prop) && !string.IsNullOrEmpty(prop.Value)) {
                _selectedGameMode = prop.Value;
                if(_gamemodeDisplayLabel != null) {
                    _gamemodeDisplayLabel.text = _selectedGameMode;
                }

                if(MatchSettingsManager.Instance != null) {
                    MatchSettingsManager.Instance.selectedGameModeId = _selectedGameMode;
                }
            } else {
                // No gamemode set yet, show "Lobby"
                if(_gamemodeDisplayLabel != null) {
                    _gamemodeDisplayLabel.text = "Lobby";
                }
            }
        }

        public void ResetGamemodeUI() {
            _isGamemodeDropdownOpen = false;

            if(_gamemodeDropdownMenu != null) {
                _gamemodeDropdownMenu.AddToClassList("hidden");
            }
            if(_gamemodeArrow != null) {
                _gamemodeArrow.AddToClassList("hidden");
                _gamemodeArrow.RemoveFromClassList("arrow-down");
            }

            if(_gamemodeDisplayLabel != null) {
                _gamemodeDisplayLabel.text = "Lobby";
                _gamemodeDisplayLabel.RemoveFromClassList("gamemode-hover");
                _gamemodeDisplayLabel.RemoveFromClassList("gamemode-clicked");
            }

            UnsubscribeFromGamemodeEvents();
        }

        public void SetDefaultGamemode(string gamemode) {
            _selectedGameMode = gamemode;
            if(_gamemodeDisplayLabel != null) {
                _gamemodeDisplayLabel.text = gamemode;
            }
        }

        public void CloseDropdown() {
            if(_isGamemodeDropdownOpen) {
                ToggleGamemodeDropdown();
            }
        }

        public string SelectedGameMode => _selectedGameMode;

        private IEnumerator ShowArrowWithAnimation() {
            if(_gamemodeArrow == null) yield break;

            // Remove hidden class first
            _gamemodeArrow.RemoveFromClassList("hidden");

            // Wait one frame to ensure base styles are applied
            yield return null;

            // Set initial arrow direction
            if(_gamemodeArrow != null) {
                _gamemodeArrow.RemoveFromClassList("arrow-down");
            }

            // Add the slide-in class to trigger animation
            if(_gamemodeArrow != null) {
                _gamemodeArrow.AddToClassList("arrow-slide-in");
            }
        }
    }
}
