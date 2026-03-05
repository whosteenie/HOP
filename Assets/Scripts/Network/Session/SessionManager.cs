using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Match;
using Game.Settings;
using Game.Social;
using Network.Diagnostics;
using Network.Events;
using Network.Singletons;
using Network.Steam;
using Network.UGS;
using Steamworks;
using Unity.Netcode;
using Unity.Services.Authentication;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityUtils;
using Lobby = Steamworks.Data.Lobby;

namespace Network.Session {
    /// <summary>
    /// Session manager for UGS lobby/matchmaker/relay flows.
    /// Steam is used as a social layer (party metadata, invites, rich presence).
    /// Orchestrates NetworkManager lifecycle for host/client transitions.
    /// </summary>
    public sealed partial class SessionManager : Singleton<SessionManager> {
        private enum SessionPhase {
            Menu,
            Searching,
            CreatingLobby,
            JoiningLobby,
            LobbyReady,
            StartingHost,
            StartingClient,
            SynchronizingLoad,
            LoadingScene,
            InGame,
            Error
        }

        // ===== State =====
        public Lobby? CurrentLobby { get; private set; }
        private SessionPhase _phase;
        private float _phaseStartTime;

        private SessionPhase Phase {
            get => _phase;
            set {
                if(_phase == value) return;
                var previous = _phase;
                _phase = value;
                _phaseStartTime = Time.time;
                if(Debug.isDebugBuild) Debug.Log($"[SessionManager] Phase -> {value}");
                FlowLog.Emit(FlowEventIds.SyncStateTransition,
                    ("from", previous),
                    ("to", value));
            }
        }

        private bool IsInGameplay { get; set; }
        public string SelectedGameMode { get; private set; } = "Deathmatch";

        public string FlowSessionId {
            get {
                if(_ugsMatchLobby != null && string.IsNullOrEmpty(_ugsMatchLobby.Id) == false) return _ugsMatchLobby.Id;
                if(_ugsPartyLobby != null && string.IsNullOrEmpty(_ugsPartyLobby.Id) == false) return _ugsPartyLobby.Id;
                if(CurrentLobby.HasValue && CurrentLobby.Value.Id != 0) return CurrentLobby.Value.Id.ToString();
                return string.IsNullOrEmpty(CurrentPartyId) == false ? CurrentPartyId : "";
            }
        }

        public string CurrentPartyId { get; private set; }
        private bool IsPartyLeader { get; set; }

        public int CurrentPartySize {
            get {
                if(_ugsPartyLobby is { Players: { Count: > 0 } }) {
                    return _ugsPartyLobby.Players.Count;
                }

                if(!CurrentLobby.HasValue) return 1;
                var memberCount = CurrentLobby.Value.MemberCount;
                return memberCount > 0 ? memberCount : 1;
            }
        }

        public bool HasRealPartyMembers => CurrentPartySize > 1;
        public bool HasPartyLobby => _ugsPartyLobby != null;

        public bool IsLocalPartyLeaderResolved {
            get {
                // Solo users are always considered leaders of their own backend party context.
                if(HasRealPartyMembers == false) return true;

                if(_ugsPartyLobby != null) {
                    var localUgsId = AuthenticationService.Instance.PlayerId;
                    if(string.IsNullOrEmpty(localUgsId) == false) {
                        return _ugsPartyLobby.HostId == localUgsId;
                    }
                }

                if(!CurrentLobby.HasValue || !SteamClient.IsValid) return IsPartyLeader;
                var localSteamId = SteamClient.SteamId;
                if(localSteamId != 0) {
                    return CurrentLobby.Value.Owner.Id == localSteamId;
                }

                return IsPartyLeader;
            }
        }

        public bool IsPartyMemberResolved => HasRealPartyMembers && !IsLocalPartyLeaderResolved;

        // ===== UGS Lobby keys (separate namespace from Steam lobby data) =====
        private const string UgsPartyIdKey = "partyId";
        private const string UgsMatchTypeKey = "matchType"; // "Public" | "Private"
        private const string UgsTargetModeKey = "targetMode";
        private const string UgsRelayJoinCodeKey = "relayJoinCode";
        private const string UgsMatchIdKey = "matchId";
        private const string UgsFollowMatchLobbyIdKey = "followMatchLobbyId";
        private const string UgsLobbyStateKey = "lobbyState";
        private const string UgsExpectedPlayersKey = "expectedPlayers";
        private const string UgsMemberReadyKey = "readyToLoad";

        // ===== UGS Lobby state =====
        private Unity.Services.Lobbies.Models.Lobby _ugsPartyLobby;
        private Unity.Services.Lobbies.Models.Lobby _ugsMatchLobby;

        private CustomNetworkManager _customNetworkManager;
        private NetworkManager _networkManager;
        private CancellationTokenSource _sessionLifetimeCts;
        private bool _isLeaving;
        private bool _isShuttingDown;
        private int _leaveSequenceId;
        private int _activeSessionOperations;
        private int _expectedGamePlayerCount = 1;
        private bool _unexpectedDisconnectInFlight;
        // Track if we expect a disconnect (e.g. intentionally leaving)

        /// <summary>True when we intentionally left (LeaveLobby etc.); used to skip disconnect capture in OnNetworkDespawn.</summary>
        public bool IsExpectedDisconnect { get; private set; }

        public bool IsSearching {
            get {
                return Phase switch {
                    SessionPhase.Searching or SessionPhase.CreatingLobby or SessionPhase.JoiningLobby
                        or SessionPhase.StartingClient or SessionPhase.SynchronizingLoad
                        or SessionPhase.LoadingScene => true,
                    _ => false
                };
            }
        }

        public bool ShowMatchmakingStatus {
            get {
                switch(Phase) {
                    case SessionPhase.Menu:
                    case SessionPhase.InGame:
                    case SessionPhase.Error:
                    case SessionPhase.SynchronizingLoad:
                    case SessionPhase.LoadingScene:
                    case SessionPhase.LobbyReady:
                    case SessionPhase.StartingHost:
                        return false;
                    case SessionPhase.Searching:
                    case SessionPhase.CreatingLobby:
                    case SessionPhase.JoiningLobby:
                    case SessionPhase.StartingClient:
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                return IsSearching;
            }
        }

        public bool IsSessionBusy {
            get {
                var nm = _networkManager != null ? _networkManager : NetworkManager.Singleton;
                var shuttingDown = nm != null && nm.ShutdownInProgress;
                return _isLeaving || _activeSessionOperations > 0 || shuttingDown;
            }
        }

        public int ExpectedGamePlayerCount => Mathf.Max(1, _expectedGamePlayerCount);
        private CancellationToken SessionLifetimeToken =>
            _sessionLifetimeCts != null ? _sessionLifetimeCts.Token : CancellationToken.None;

        #region Unity Lifecycle

        protected override void Awake() {
            if(HasInstance && Instance != this) {
                Destroy(gameObject);
                return;
            }

            DontDestroyOnLoad(gameObject);

            if(DisconnectTransitionController.Instance == null) {
                gameObject.AddComponent<DisconnectTransitionController>();
            }

            _networkManager = NetworkManager.Singleton;
            if(_networkManager != null) {
                _customNetworkManager = _networkManager.GetComponent<CustomNetworkManager>();
            }
            _sessionLifetimeCts = new CancellationTokenSource();

            SelectedMapSceneName = MatchMapService.DefaultGameplaySceneName;
            SelectedMapId = MatchMapService.DefaultMapId;
        }

        private void OnEnable() {
            RegisterNetworkCallbacks();
            SceneManager.sceneLoaded += OnSceneLoaded;

            // Steam Callbacks
            SteamMatchmaking.OnLobbyMemberJoined += OnLobbyMemberJoined;
            SteamMatchmaking.OnLobbyMemberLeave += OnLobbyMemberLeave;
            SteamMatchmaking.OnLobbyDataChanged += OnLobbyDataChanged;
            SteamMatchmaking.OnLobbyMemberDataChanged += OnLobbyMemberDataChanged;
            SteamFriends.OnGameLobbyJoinRequested += OnGameLobbyJoinRequested;
            SteamFriends.OnGameRichPresenceJoinRequested += OnGameRichPresenceJoinRequested;

            EventBus.Unsubscribe<GameSettingsChangedEvent>(OnGameSettingsChanged);
            EventBus.Subscribe<GameSettingsChangedEvent>(OnGameSettingsChanged);
        }

        private void OnDisable() {
            UnregisterNetworkCallbacks();
            SceneManager.sceneLoaded -= OnSceneLoaded;

            SteamMatchmaking.OnLobbyMemberJoined -= OnLobbyMemberJoined;
            SteamMatchmaking.OnLobbyMemberLeave -= OnLobbyMemberLeave;
            SteamMatchmaking.OnLobbyDataChanged -= OnLobbyDataChanged;
            SteamMatchmaking.OnLobbyMemberDataChanged -= OnLobbyMemberDataChanged;
            SteamFriends.OnGameLobbyJoinRequested -= OnGameLobbyJoinRequested;
            SteamFriends.OnGameRichPresenceJoinRequested -= OnGameRichPresenceJoinRequested;

            EventBus.Unsubscribe<GameSettingsChangedEvent>(OnGameSettingsChanged);
        }

        private void OnLocalSettingsChanged() {
            // Streamer mode toggle can change the display name we want other players to see.
            UpdateLocalDisplayNameInLobby();
        }

        private void OnGameSettingsChanged(GameSettingsChangedEvent _) {
            OnLocalSettingsChanged();
        }

        private void UpdateLocalDisplayNameInLobby() {
            if(!CurrentLobby.HasValue) return;
            if(!SteamClient.IsValid || !SteamClient.IsLoggedOn) return;

            try {
                var displayName = StreamerMode.GetLocalDisplayName();
                if(string.IsNullOrEmpty(displayName)) return;
                CurrentLobby.Value.SetMemberData(DisplayNameKey, displayName);
                var hide = StreamerMode.Enabled;
                CurrentLobby.Value.SetMemberData(AvatarHiddenKey, hide ? "1" : "0");

                var data = GameSettings.Data;
                var baseColor = data.player.customization.baseColor;
                var iconId = PlayerIconPicker.PickIconIdFromBaseColor(baseColor, hide);
                CurrentLobby.Value.SetMemberData(PlayerIconKey, iconId);
            } catch(Exception ex) {
                // If Steam is transitioning offline, this can fail transiently.
                if(Debug.isDebugBuild) {
                    Debug.LogWarning($"[SessionManager] Failed to update local lobby display metadata: {ex.Message}");
                }
            }
        }

        private void Start() {
            if(SteamManager.Instance == null) {
                Debug.LogError("[SessionManager] SteamManager not found!");
            }

            // Bootstrap UGS identity early so Lobby/Matchmaker/Vivox can rely on it later.
            LaunchSessionTask(UgsAuthService.InitializeAndSignInAsync(),
                "BootstrapUGSAuth");
        }

        private void OnDestroy() {
            CancelSessionLifetimeTasks();
            if(_isShuttingDown) return;
            LeaveLobby();
        }

        private void OnApplicationQuit() {
            _isShuttingDown = true;
            CancelSessionLifetimeTasks();
        }

        #endregion

        #region Public API - Matchmaking

        /// <summary>
        /// Starts a local "private match" without Steam (offline).
        /// This is a host-only loopback session (LAN/multiplayer offline is handled later).
        /// </summary>
        public async UniTask StartOfflinePrivateMatchAsync(string mode) {
            if(!TryBeginSessionOperation("StartOfflinePrivateMatchAsync")) return;
            try {
                if(string.IsNullOrEmpty(mode)) return;

                // Leave any Steam lobby context (safe even if Steam is offline).
                LeaveLobby();
                await TryLeaveVoiceChannelAsync();

                // Shut down NGO if it was previously listening.
                await CleanupNetworkAsync();

                ApplyRuntimeMode(mode, "OfflinePrivateMatch");
                SetExpectedGamePlayerCount(1, "OfflinePrivateMatch");

                SetFrontStatus(SessionPhase.StartingHost, "Starting offline match...");

                // Fade out (keeps UX consistent with Steam private match flow).
                await FadeOutWithFallbackAsync(300);

                if(TryGetUnityTransport("StartOfflinePrivateMatchAsync", out var networkManager, out var utp) ==
                   false) {
                    SetFrontStatus(SessionPhase.Error, "Offline networking not configured.");
                    return;
                }

                // Ensure we use UTP loopback.
                utp.SetConnectionData("127.0.0.1", 7777);
                networkManager.NetworkConfig.NetworkTransport = utp;

                // Start host and load the game scene via NGO scene management.
                ApplyLocalConnectionPayload(true);
                if(!networkManager.StartHost()) {
                    Debug.LogError("[SessionManager] Failed to start offline host after cleanup.");
                    SetFrontStatus(SessionPhase.Error, "Failed to start offline host.");
                    if(SceneTransitionManager.Instance != null) {
                        await SceneTransitionManager.Instance.FadeInAsync();
                    }

                    return;
                }

                if(!TryLoadGameplaySceneAsHost("StartOfflinePrivateMatchAsync/LoadScene")) {
                    SetFrontStatus(SessionPhase.Error, "Failed to load offline match scene.");
                    if(SceneTransitionManager.Instance != null) {
                        await SceneTransitionManager.Instance.FadeInAsync();
                    }
                }
            } finally {
                EndSessionOperation();
            }
        }

        private async UniTask<bool> CreateSteamSocialLobbyAsync(int maxMembers) {
            try {
                var result = await SteamMatchmaking.CreateLobbyAsync(maxMembers);
                if(!result.HasValue) {
                    SetFrontStatus(SessionPhase.Error, "Failed to create party lobby.");
                    return false;
                }

                var lobby = result.Value;
                lobby.SetPrivate();
                lobby.SetJoinable(true);
                lobby.SetData(TargetModeKey, SelectedGameMode);

                if(string.IsNullOrEmpty(CurrentPartyId)) {
                    CurrentPartyId = Guid.NewGuid().ToString();
                }

                lobby.SetData(PartyIdKey, CurrentPartyId);
                CurrentLobby = lobby;
                IsPartyLeader = true;
                lobby.SetMemberData(PartyIdKey, CurrentPartyId);
                UpdateLocalDisplayNameInLobby();

                // Solo social lobbies do not need voice yet; join when the party has at least 2 members.
                if(lobby.MemberCount > 1) {
                    TryJoinVoiceForSteamSocialLobby(lobby.Id, "CreateSteamSocialLobbyAsync");
                }

                FlowLog.Emit(FlowEventIds.PartyLifecycle,
                    ("action", "CreateSteamSocialLobby"),
                    ("partyId", CurrentPartyId),
                    ("steamLobbyId", lobby.Id),
                    ("mode", SelectedGameMode));

                UpdateSteamRichPresence();
                SetFrontStatus(SessionPhase.LobbyReady, "Lobby Ready. Invite Friends!");
                NotifyPartyStateChanged();
                return true;
            } catch(Exception ex) {
                Debug.LogError($"[SessionManager] Failed to create Steam social lobby: {ex.Message}");
                SetFrontStatus(SessionPhase.Error, "Failed to create party lobby.");
                return false;
            }
        }

        /// <summary>
        /// Sets the selected gamemode and updates lobby data if hosting.
        /// </summary>
        /// <param name="mode">The gamemode ID.</param>
        public void SetGameMode(string mode) {
            ApplyRuntimeMode(mode, "MenuSelection", refreshUi: false);
            if(CurrentLobby.HasValue && CurrentLobby.Value.Owner.Id == SteamClient.SteamId) {
                CurrentLobby.Value.SetData(TargetModeKey, mode);
            }

            EventBus.Publish(new FrontStatusChangedEvent(null));
        }


        /// <summary>
        /// Leaves the current Steam social lobby context.
        /// </summary>
        public void LeaveLobby() {
            FlowLog.Emit(FlowEventIds.PartyLifecycle,
                ("action", "LeaveLobby"),
                ("partyId", CurrentPartyId),
                ("steamLobbyId", CurrentLobby.HasValue ? CurrentLobby.Value.Id.ToString() : "none"));

            IsExpectedDisconnect = true;
            if(CurrentLobby.HasValue) {
                CurrentLobby.Value.Leave();
                CurrentLobby = null;
            }

            Phase = SessionPhase.Menu;
            IsPartyLeader = false;
            SetExpectedGamePlayerCount(1, "LeaveLobby");
        }

        /// <summary>
        /// Transitions the local player back to the main menu and clears active match state.
        /// </summary>
        /// <param name="skipFadeOut">When true, caller already faded to black (e.g. unexpected disconnect). Skips initial fade-out.</param>
        public async UniTask LeaveToMainMenuAsync(bool skipFadeOut = false) {
            if(_isLeaving) {
                Debug.LogWarning("[SessionManager] LeaveToMainMenuAsync ignored: leave already in progress.");
                return;
            }

            _leaveSequenceId++;
            var leaveId = _leaveSequenceId;
            _isLeaving = true;

            try {
                FlowLog.Emit(FlowEventIds.SessionExit,
                    ("leaveId", leaveId),
                    ("reason", "LeaveToMainMenu"),
                    ("step", "EXIT_BEGIN"),
                    ("phase", Phase),
                    ("gameplay", IsInGameplay),
                    ("scene", SceneManager.GetActiveScene().name));

                // Cancel any active matchmaking first
                await ClearMatchmakingStateAsync();
                FlowLog.Emit(FlowEventIds.SessionExit, ("leaveId", leaveId), ("step", "EXIT_MATCHMAKING_CLEARED"));

                if(Game.Audio2.AudioService.Instance != null) {
                    Game.Audio2.AudioService.Instance.StopAll();
                }

                var currentScene = SceneManager.GetActiveScene().name;
                var shouldFade = currentScene != "MainMenu";
                FlowLog.Emit(FlowEventIds.SessionExit,
                    ("leaveId", leaveId),
                    ("step", "EXIT_SCENE_SNAPSHOT"),
                    ("currentScene", currentScene),
                    ("shouldFade", shouldFade));

                if(skipFadeOut && DisconnectTransitionController.Instance != null) {
                    DisconnectTransitionController.Instance.CleanupDuplicate();
                }

                if(shouldFade && !skipFadeOut) {
                    FlowLog.Emit(FlowEventIds.SessionExit, ("leaveId", leaveId), ("step", "EXIT_FADE_OUT_BEGIN"));
                    await FadeOutWithFallbackAsync();
                    FlowLog.Emit(FlowEventIds.SessionExit, ("leaveId", leaveId), ("step", "EXIT_FADE_OUT_DONE"));
                }

                FlowLog.Emit(FlowEventIds.SessionExit, ("leaveId", leaveId), ("step", "EXIT_VOICE_LEAVE_BEGIN"));
                await TryLeaveVoiceChannelAsync();
                FlowLog.Emit(FlowEventIds.SessionExit, ("leaveId", leaveId), ("step", "EXIT_VOICE_LEAVE_DONE"));

                FlowLog.Emit(FlowEventIds.SessionExit,
                    ("leaveId", leaveId),
                    ("step", "EXIT_PARTY_FOLLOW_RESET_BEGIN"));
                await ResetPartyFollowStateIfHostAsync();
                FlowLog.Emit(FlowEventIds.SessionExit,
                    ("leaveId", leaveId),
                    ("step", "EXIT_PARTY_FOLLOW_RESET_DONE"));

                LeaveLobby();
                await ClearMatchStateAsync();
                if(SteamManager.Instance != null) {
                    SteamManager.Instance.ClearAvatarCache();
                }
                FlowLog.Emit(FlowEventIds.SessionExit, ("leaveId", leaveId), ("step", "EXIT_MATCH_STATE_CLEARED"));

                await CleanupNetworkAsync();
                FlowLog.Emit(FlowEventIds.SessionExit, ("leaveId", leaveId), ("step", "EXIT_NETWORK_CLEANUP_DONE"));

                FlowLog.Emit(FlowEventIds.SessionExit, ("leaveId", leaveId), ("step", "EXIT_SCENE_LOAD_BEGIN"));
                await EnsureMainMenuLoadedAndReadyAsync(currentScene);
                FlowLog.Emit(FlowEventIds.SessionExit, ("leaveId", leaveId), ("step", "EXIT_SCENE_LOAD_DONE"));

                if(shouldFade) {
                    FlowLog.Emit(FlowEventIds.SessionExit, ("leaveId", leaveId), ("step", "EXIT_FADE_IN_BEGIN"));
                    await FadeInWithFallbackAsync();
                    FlowLog.Emit(FlowEventIds.SessionExit, ("leaveId", leaveId), ("step", "EXIT_FADE_IN_DONE"));
                }

                FlowLog.Emit(FlowEventIds.SessionExit, ("leaveId", leaveId), ("step", "EXIT_FINALIZED"));
            } finally {
                _isLeaving = false;
                FlowLog.Emit(FlowEventIds.SessionExit, ("leaveId", leaveId), ("step", "EXIT_LEAVE_FLAG_CLEARED"));
            }
        }

        private async UniTask EnsureMainMenuLoadedAndReadyAsync(string currentScene) {
            if(currentScene == "MainMenu") return;

            SceneManager.LoadScene("MainMenu");

            var sceneLoaded = await WaitForActiveSceneAsync("MainMenu", 15f, SessionLifetimeToken);
            if(!sceneLoaded) {
                Debug.LogWarning("[SessionManager] Timed out waiting for MainMenu scene activation during leave flow.");
            }

            var menuReady = await WaitForMainMenuReadyAsync(15f, SessionLifetimeToken);
            if(!menuReady) {
                Debug.LogWarning(
                    "[SessionManager] Timed out waiting for MainMenuManager initialization during leave flow.");
            }
        }

        public static bool IsGameplaySceneName(string sceneName) {
            return MatchMapService.IsGameplayScene(sceneName);
        }

        #endregion

        #region Internal / Networking

        private static void FadeOutImmediateWithFallback() {
            if(SceneTransitionManager.Instance != null) {
                SceneTransitionManager.Instance.FadeOutImmediate();
            }
        }

        private static async UniTask FadeOutWithFallbackAsync(int fallbackDelayMs = 500) {
            if(SceneTransitionManager.Instance != null) {
                await SceneTransitionManager.Instance.FadeOutAsync();
                return;
            }

            await UniTask.Delay(fallbackDelayMs);
        }

        private static async UniTask FadeInWithFallbackAsync(int fallbackDelayMs = 500) {
            if(SceneTransitionManager.Instance != null) {
                await SceneTransitionManager.Instance.FadeInAsync();
                return;
            }

            await UniTask.Delay(fallbackDelayMs);
        }

        private static bool ShouldEmitThrottledLog(ref float nextLogTime, float intervalSeconds) {
            var now = Time.unscaledTime;
            if(now < nextLogTime) {
                return false;
            }

            nextLogTime = now + intervalSeconds;
            return true;
        }

        private static async UniTask EnsureSignedInAsync() {
            await UgsAuthService.InitializeAndSignInAsync();
        }

        #endregion
    }
}
