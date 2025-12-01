using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Network;
using Network.Services;
using Unity.Services.Authentication;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Menu {
    /// <summary>
    /// Manages session creation, joining, player list, and relay code for the main menu.
    /// Handles all multiplayer session-related functionality.
    /// </summary>
    public class MainMenuSessionManager : MonoBehaviour {
        [Header("References")]
        public UIDocument uiDocument;

        private VisualElement _root;
        private Label _joinCodeLabel;
        private Label _waitingLabel;
        private TextField _joinCodeInput;
        private VisualElement _playerList;
        private Button _hostButton;
        private Button _joinButton;
        private Button _copyButton;
        private Button _startButton;
        private Button _backLobbyButton;

        private bool _justCreatedSessionAsHost;
        private bool _isStartingGame;
        private string _cachedPlayerName;

        // Events
        public Action OnHostClicked;
        public Action<string> OnJoinClicked;
        public Action OnStartGameClicked;
        public Action OnBackFromLobbyClicked;
        public Action<bool, bool> OnHostStatusChanged; // isHost, wasHost
        private readonly Action _onRelayCodeAvailable;
        public Func<bool> ShouldShowLobbyLeaveModal;

        public MainMenuSessionManager(Action onRelayCodeAvailable) {
            _onRelayCodeAvailable = onRelayCodeAvailable;
        }

        private void Awake() {
            if(uiDocument == null) {
                Debug.LogError("[MainMenuSessionManager] UIDocument is not assigned!");
                return;
            }

            _root = uiDocument.rootVisualElement;
            FindUIElements();
            RegisterUIEvents();
        }

        private void OnEnable() {
            if(SessionManager.Instance != null) {
                SessionManager.Instance.PlayersChanged += OnPlayersChanged;
                SessionManager.Instance.RelayCodeAvailable += OnRelayCodeAvailableInternal;
                SessionManager.Instance.FrontStatusChanged += UpdateStatusText;
                SessionManager.Instance.SessionJoined += OnSessionJoined;
                SessionManager.Instance.HostDisconnected += OnHostDisconnected;
                SessionManager.Instance.LobbyReset += ResetLobbyUI;
            }
        }

        private void OnDisable() {
            if(SessionManager.Instance != null) {
                SessionManager.Instance.PlayersChanged -= OnPlayersChanged;
                SessionManager.Instance.RelayCodeAvailable -= OnRelayCodeAvailableInternal;
                SessionManager.Instance.FrontStatusChanged -= UpdateStatusText;
                SessionManager.Instance.SessionJoined -= OnSessionJoined;
                SessionManager.Instance.HostDisconnected -= OnHostDisconnected;
                SessionManager.Instance.LobbyReset -= ResetLobbyUI;
            }
        }

        private void FindUIElements() {
            _joinCodeLabel = _root.Q<Label>("host-label");
            _waitingLabel = _root.Q<Label>("waiting-label");
            _joinCodeInput = _root.Q<TextField>("join-input");
            _playerList = _root.Q<VisualElement>("player-list");
            _hostButton = _root.Q<Button>("host-button");
            _joinButton = _root.Q<Button>("join-button");
            _copyButton = _root.Q<Button>("copy-code-button");
            _startButton = _root.Q<Button>("start-button");
            _backLobbyButton = _root.Q<Button>("back-to-gamemode");

            if(_joinCodeInput != null) {
                _joinCodeInput.maxLength = 6;
                _joinCodeInput.isDelayed = false;
            }
        }

        private void RegisterUIEvents() {
            _hostButton.clicked += () => {
                OnHostClicked?.Invoke();
            };

            _joinCodeInput.RegisterValueChangedCallback(OnJoinCodeInputValueChanged);
            _joinCodeInput.RegisterCallback<FocusInEvent>(OnJoinCodeInputFocusIn);
            _joinCodeInput.RegisterCallback<FocusOutEvent>(OnJoinCodeInputFocusOut);
            UpdateJoinCodePlaceholderVisibility();

            _joinButton.clicked += () => {
                if(_joinCodeInput != null) {
                    OnJoinClicked?.Invoke(_joinCodeInput.value.ToUpper());
                }
            };

            _copyButton.clicked += CopyJoinCodeToClipboard;
            _startButton.clicked += () => {
                OnStartGameClicked?.Invoke();
            };

            _backLobbyButton.clicked += () => {
                if(ShouldShowLobbyLeaveModal != null && ShouldShowLobbyLeaveModal()) {
                    // Modal will be shown by MainMenuManager
                    OnBackFromLobbyClicked?.Invoke();
                } else {
                    // Leave directly
                    UISoundService.PlayButtonClick(isBack: true);
                    if(SessionManager.Instance != null) {
                        SessionManager.Instance.LeaveToMainMenuAsync(skipFade: true).Forget();
                    }
                    OnBackFromLobbyClicked?.Invoke();
                }
            };
        }

        private void OnJoinCodeInputValueChanged(ChangeEvent<string> evt) {
            // Force uppercase
            if(evt.newValue != evt.newValue.ToUpper()) {
                _joinCodeInput.value = evt.newValue.ToUpper();
            }
            UpdateJoinCodePlaceholderVisibility();
        }

        private void OnJoinCodeInputFocusIn(FocusInEvent evt) {
            UpdateJoinCodePlaceholderVisibility();
        }

        private void OnJoinCodeInputFocusOut(FocusOutEvent evt) {
            UpdateJoinCodePlaceholderVisibility();
        }

        private void UpdateJoinCodePlaceholderVisibility() {
            if(_joinCodeInput == null) return;
            var hasValue = !string.IsNullOrEmpty(_joinCodeInput.value);
            if(hasValue) {
                _joinCodeInput.AddToClassList("has-value");
            } else {
                _joinCodeInput.RemoveFromClassList("has-value");
            }
        }

        private void OnRelayCodeAvailableInternal(string code) {
            _onRelayCodeAvailable?.Invoke();
        }

        private void UpdateStatusText(string msg) {
            if(_waitingLabel != null) {
                _waitingLabel.text = msg;
            }
        }

        private void OnSessionJoined(string sessionCode) {
            JoinCodeService.UpdateJoinCodeDisplay(_joinCodeLabel, _copyButton, sessionCode);
            CalculateAndUpdateHostStatus();
        }

        private void OnHostDisconnected() {
            if(_waitingLabel != null) {
                _waitingLabel.text = "Host disconnected. Create or join a new game.";
            }
            MainMenuUIManager.EnableButton(_hostButton);
            MainMenuUIManager.EnableButton(_joinButton);
            MainMenuUIManager.DisableButton(_startButton);
            MainMenuUIManager.DisableButton(_copyButton);
        }

        public void ResetLobbyUI() {
            JoinCodeService.UpdateJoinCodeDisplay(_joinCodeLabel, _copyButton, null);
            if(_playerList != null) {
                _playerList.Clear();
            }
            if(_joinCodeInput != null) {
                _joinCodeInput.value = "";
            }
            IsHost = false;
            _justCreatedSessionAsHost = false;
            _isStartingGame = false;
            _cachedPlayerName = null;

            MainMenuUIManager.EnableButton(_hostButton);
            MainMenuUIManager.EnableButton(_joinButton);
            MainMenuUIManager.DisableButton(_copyButton);
            MainMenuUIManager.DisableButton(_startButton);
        }

        public async void HandleHostClicked() {
            try {
                _isStartingGame = false;
                MainMenuUIManager.DisableButton(_hostButton);
                MainMenuUIManager.DisableButton(_joinButton);

                // Clear player list immediately
                if(_playerList != null) {
                    _playerList.Clear();
                }

                // Cache our own player name for immediate display
                _cachedPlayerName = PlayerPrefs.GetString("PlayerName", "Player");
                if(string.IsNullOrWhiteSpace(_cachedPlayerName)) {
                    _cachedPlayerName = "Player";
                }

                var joinCode = await SessionManager.Instance.StartSessionAsHost();

                if(string.IsNullOrEmpty(joinCode)) {
                    MainMenuUIManager.EnableButton(_hostButton);
                    MainMenuUIManager.EnableButton(_joinButton);
                    _cachedPlayerName = null;
                    return;
                }

                _justCreatedSessionAsHost = true;
                JoinCodeService.UpdateJoinCodeDisplay(_joinCodeLabel, _copyButton, joinCode);
                MainMenuUIManager.EnableButton(_copyButton);

                // Immediately refresh player list
                RefreshPlayerList();
                CalculateAndUpdateHostStatus();
            } catch(Exception e) {
                Debug.LogException(e);
                MainMenuUIManager.EnableButton(_hostButton);
                MainMenuUIManager.EnableButton(_joinButton);
            }
        }

        public async void HandleJoinClicked(string code) {
            try {
                var regexAlphaNum = new System.Text.RegularExpressions.Regex("^[a-zA-Z0-9]*$");
                if(string.IsNullOrWhiteSpace(code) || code.Length != 6 || !regexAlphaNum.IsMatch(code)) {
                    if(_waitingLabel != null) {
                        _waitingLabel.text = "Invalid join code";
                    }
                    return;
                }

                // Clear player list immediately
                if(_playerList != null) {
                    _playerList.Clear();
                }

                // Cache our own player name for immediate display
                _cachedPlayerName = PlayerPrefs.GetString("PlayerName", "Player");
                if(string.IsNullOrWhiteSpace(_cachedPlayerName)) {
                    _cachedPlayerName = "Player";
                }

                MainMenuUIManager.DisableButton(_hostButton);
                MainMenuUIManager.DisableButton(_joinButton);

                var result = await SessionManager.Instance.JoinSessionByCodeAsync(code);
                if(_waitingLabel != null) {
                    _waitingLabel.text = result;
                }

                if(result.Contains("Lobby joined")) {
                    JoinCodeService.UpdateJoinCodeDisplay(_joinCodeLabel, _copyButton, code);
                    MainMenuUIManager.EnableButton(_copyButton);
                    RefreshPlayerList();
                } else {
                    MainMenuUIManager.EnableButton(_joinButton);
                    MainMenuUIManager.EnableButton(_hostButton);
                    _cachedPlayerName = null;
                }
            } catch(Exception e) {
                Debug.LogException(e);
                if(_waitingLabel != null) {
                    _waitingLabel.text = "Error joining session: " + e.Message;
                }
                MainMenuUIManager.EnableButton(_hostButton);
                MainMenuUIManager.EnableButton(_joinButton);
            }
        }

        public async void HandleStartGameClicked() {
            try {
                _isStartingGame = true;
                MainMenuUIManager.DisableButton(_startButton);

                await SessionManager.Instance.BeginGameplayAsHostAsync();
            } catch(Exception e) {
                Debug.LogException(e);
                if(_waitingLabel != null) {
                    _waitingLabel.text = "Failed to start game: " + e.Message;
                }
                _isStartingGame = false;
                MainMenuUIManager.EnableButton(_startButton);
            }
        }

        private void CopyJoinCodeToClipboard() {
            UISoundService.PlayButtonClick();
            JoinCodeService.CopyFromLabel(_joinCodeLabel);
        }

        private void OnPlayersChanged(IReadOnlyList<IReadOnlyPlayer> players) {
            RefreshPlayerList(players);
        }

        private void RefreshPlayerList(IReadOnlyList<IReadOnlyPlayer> players = null) {
            if(_playerList == null) return;

            _playerList.Clear();

            if(players == null) {
                var sessionManagerInstance = SessionManager.Instance;
                var session = sessionManagerInstance != null ? sessionManagerInstance.ActiveSession : null;
                if(session == null) return;
                players = session.Players;
            }

            if(players == null || players.Count == 0) {
                return;
            }

            // Identify host
            var sessionManagerInstanceForHost = SessionManager.Instance;
            var activeSessionForHost = sessionManagerInstanceForHost != null ? sessionManagerInstanceForHost.ActiveSession : null;
            var hostId = activeSessionForHost != null ? activeSessionForHost.Host : null;

            foreach(var p in players) {
                string display;

                // Try to get player name from properties
                if(p.Properties != null &&
                   p.Properties.TryGetValue("playerName", out var prop) &&
                   !string.IsNullOrEmpty(prop.Value)) {
                    display = prop.Value;
                }
                // Fallback to cached name if available
                else if(!string.IsNullOrEmpty(_cachedPlayerName) && p.Id == AuthenticationService.Instance.PlayerId) {
                    display = _cachedPlayerName;
                }
                // Final fallback to player ID
                else {
                    display = p.Id;
                }

                var isHost = !string.IsNullOrEmpty(hostId) && p.Id == hostId;
                AddPlayerEntry(display, isHost);
            }

            CalculateAndUpdateHostStatus();
        }

        private void AddPlayerEntry(string playerName, bool isHost) {
            foreach(var child in _playerList.Children()) {
                child.style.borderBottomWidth = 1f;
            }

            var entry = new VisualElement();
            entry.AddToClassList("player-entry");
            if(isHost) entry.AddToClassList("host");

            var label = new Label(playerName);
            entry.Add(label);
            _playerList.Add(entry);

            entry.style.borderBottomWidth = 0f;
        }

        private void CalculateAndUpdateHostStatus() {
            var sessionManagerInstance = SessionManager.Instance;
            var session = sessionManagerInstance != null ? sessionManagerInstance.ActiveSession : null;
            var detectedAsHost = false;

            if(session != null) {
                var hostId = session.Host;
                if(!string.IsNullOrEmpty(hostId)) {
                    if(session.IsHost) {
                        detectedAsHost = true;
                    } else if(_justCreatedSessionAsHost) {
                        detectedAsHost = true;
                    } else if(session.Players.Count == 1) {
                        detectedAsHost = true;
                    }
                } else {
                    if(_justCreatedSessionAsHost) {
                        detectedAsHost = true;
                    }
                }
            }

            UpdateHostStatus(detectedAsHost);
        }

        private void UpdateHostStatus(bool isHost) {
            var wasHost = IsHost;
            IsHost = isHost;

            var sessionManagerInstance = SessionManager.Instance;
            var isInGameplay = sessionManagerInstance != null && sessionManagerInstance.IsInGameplay;

            // Start button: enabled for hosts (only if not in gameplay and not already starting)
            if(IsHost && !isInGameplay && !_isStartingGame) {
                MainMenuUIManager.EnableButton(_startButton);
            } else {
                MainMenuUIManager.DisableButton(_startButton);
            }

            // Notify listeners of host status change
            if(wasHost != isHost) {
                OnHostStatusChanged?.Invoke(isHost, wasHost);
            }
        }

        public void SetJustCreatedSessionAsHost(bool value) {
            _justCreatedSessionAsHost = value;
        }

        public bool IsHost { get; private set; }

        public bool JustCreatedSessionAsHost => _justCreatedSessionAsHost;
        public bool IsStartingGame => _isStartingGame;
    }
}
