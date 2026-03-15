using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Match;
using Network.Core;
using Network.Diagnostics;
using Network.Events;
using Network.Singletons;
using Network.Steam;
using Network.UGS;
using Steamworks;
using Unity.Netcode;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityUtils;
using Lobby = Steamworks.Data.Lobby;
using System.Collections.Generic;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Lobbies.Models;
using Unity.Services.Matchmaker.Models;

namespace Network.Session {
    /// <summary>
    /// Session manager for UGS lobby/matchmaker/relay flows.
    /// Steam is used as a social layer (party metadata, invites, rich presence).
    /// Orchestrates NetworkManager lifecycle for host/client transitions.
    /// </summary>
    public sealed class SessionManager : Singleton<SessionManager>, ISessionContext, ISteamSessionActions,
        IMatchmakerSessionActions, IPartySessionActions, ILobbyEventActions,
        IMatchSnapshotActions, IDistributedAuthorityActions, INetworkLifecycleActions, ISceneFlowActions,
        ILeaveToMenuActions, IHostMapSceneActions, IOnGameSceneLoadedActions, IPrivateMatchHostActions {
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

        public int CurrentPartySize => SessionParty.GetCurrentPartySize(this);

        public bool HasRealPartyMembers => SessionParty.HasRealPartyMembers(this);
        public bool HasPartyLobby => _ugsPartyLobby != null;

        public bool IsLocalPartyLeaderResolved => SessionParty.IsLocalPartyLeaderResolved(this);

        public bool IsPartyMemberResolved => SessionParty.IsPartyMemberResolved(this);

        // ===== UGS Lobby keys (separate namespace from Steam lobby data) =====
        private const string UgsMatchTypeKey = "matchType"; // "Public" | "Private"
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


        #region Core Session Operations

        private bool TryGetNetworkManager(string operationName, out NetworkManager networkManager) {
            if(_networkManager == null) {
                _networkManager = NetworkManager.Singleton;
            }

            networkManager = _networkManager;
            if(networkManager != null) {
                return true;
            }

            Debug.LogError($"[SessionManager] NetworkManager.Singleton is null during {operationName}.");
            return false;
        }

        private bool TryGetUnityTransport(string operationName, out NetworkManager networkManager,
            out UnityTransport transport) {
            transport = null;
            if(TryGetNetworkManager(operationName, out networkManager) == false) {
                return false;
            }

            transport = networkManager.NetworkConfig.NetworkTransport as UnityTransport;
            if(transport != null) {
                return true;
            }

            transport = networkManager.GetComponent<UnityTransport>();
            if(transport != null) {
                return true;
            }

            Debug.LogError(
                $"[SessionManager] No UnityTransport-compatible transport is configured on NetworkManager during {operationName}.");
            return false;
        }

        private async UniTask<bool> TrySetMatchLobbyStateAsync(string lobbyState,
            DataObject.VisibilityOptions visibility, string context) =>
            await SessionMatchLobby.TrySetMatchLobbyStateAsync(this, lobbyState, visibility, context);

        private async UniTask<string> CreateDaSessionAsync(int maxPlayers, bool isPrivateMatch,
            string contextLabel) =>
            await SessionNetworkLifecycle.CreateDaSessionAsync(
                this, this, this, maxPlayers, isPrivateMatch, contextLabel);

        private async UniTask<SessionNetworkLifecycle.DaSessionJoinResult>
            JoinDaSessionAsync(
                string sessionCode, bool isPrivateMatch,
                string contextLabel) =>
            await SessionNetworkLifecycle.JoinDaSessionAsync(
                sessionCode, isPrivateMatch, this, this, this, contextLabel);

        private static async UniTask LeaveActiveSessionAsync(string contextLabel) {
            var activeSession = SessionNetworkLifecycle.GetActiveSession();
            if(activeSession == null) return;
            SessionNetworkLifecycle.UnbindActiveSession();
            await SessionNetworkLifecycle.LeaveSessionAsync(activeSession, contextLabel);
        }

        internal bool TryResolveDaPlayerMetadata(string ugsPlayerId, out string partyId,
            out ulong steamId) =>
            SessionNetworkLifecycle.TryResolveDaPlayerMetadata(
                _ugsMatchLobby?.Players, _ugsPartyLobby?.Players, ugsPlayerId, out partyId, out steamId);

        private bool TryLoadGameplaySceneAsHost(string contextLabel) =>
            SessionSceneFlow.TryLoadGameplaySceneAsHost(this, this, contextLabel);

        public string SelectedMapId { get; private set; }
        private string SelectedMapSceneName { get; set; }

        /// <summary>When true, host map selection uses existing SelectedMapId/SelectedMapSceneName from private match draft.</summary>
        private bool _privateMatchMapPreset;

        private void SetPrivateMatchMapPreset(bool value) => _privateMatchMapPreset = value;

        /// <summary>Sets the map from a private match draft (map id). Skips random selection when loading the gameplay scene.</summary>
        private void SetSelectedMapFromId(string mapId) =>
            SessionSceneFlow.RunSetSelectedMapFromId(this, this, mapId);

        private void SetSelectedMap(string mapId, string sceneName) {
            SelectedMapId = mapId;
            SelectedMapSceneName = sceneName;
        }

        private bool ConsumePrivateMatchMapPreset() {
            var was = _privateMatchMapPreset;
            _privateMatchMapPreset = false;
            return was;
        }

        private void LoadSceneAsHost(string sceneName) {
            if(TryGetNetworkManager("LoadSceneAsHost", out var networkManager))
                networkManager.SceneManager.LoadScene(sceneName, LoadSceneMode.Single);
        }

        private void SetSteamLobbyMapIfOwner(string mapId, string sceneName) {
            if(!CurrentLobby.HasValue || CurrentLobby.Value.Owner.Id != SteamClient.SteamId) return;
            CurrentLobby.Value.SetData("TargetMapId", mapId ?? string.Empty);
            CurrentLobby.Value.SetData("TargetMapScene", sceneName ?? string.Empty);
        }

        /// <summary>
        /// Applies all private match draft settings before starting the match (gamemode, map, timer, score, tagged, team assignments).
        /// Call from the menu flow before StartPrivateMatchAsync / StartOfflinePrivateMatchAsync.
        /// </summary>
        public void ApplyPrivateMatchSettings(
            string mode,
            string mapId,
            int matchTimerSeconds,
            bool usePreMatchCountdown,
            bool swapWeaponsOnDeath,
            int scoreToWin,
            int kothHillSpeed,
            int taggedPlayers,
            IReadOnlyDictionary<ulong, int> teamAssignments) =>
            SessionParty.RunApplyPrivateMatchSettings(this, this, mode, mapId, matchTimerSeconds,
                usePreMatchCountdown, swapWeaponsOnDeath, scoreToWin, kothHillSpeed, taggedPlayers, teamAssignments);

        /// <summary>
        /// Shuts down the Netcode network manager and leaves the active UGS session.
        /// </summary>
        private async UniTask CleanupNetworkAsync() {
            await SessionNetworkLifecycle.CleanupNetworkAsync(this, this);
        }

        private void CancelSessionLifetimeTasks() {
            if(_sessionLifetimeCts == null) return;
            if(_sessionLifetimeCts.IsCancellationRequested == false) {
                _sessionLifetimeCts.Cancel();
            }

            _sessionLifetimeCts.Dispose();
            _sessionLifetimeCts = null;
        }

        private static void LaunchSessionTask(UniTask task, string context, bool logCancellation = false) {
            LaunchSessionTaskInternal(task, context, logCancellation).Forget();
        }

        private static async UniTaskVoid LaunchSessionTaskInternal(UniTask task, string context, bool logCancellation) {
            try {
                await task;
            } catch(OperationCanceledException) {
                if(logCancellation && Debug.isDebugBuild) {
                    Debug.Log($"[SessionManager] Task canceled: {context}");
                }
            } catch(Exception ex) {
                Debug.LogError($"[SessionManager] Task failed ({context}): {ex}");
            }
        }

        private bool TryBeginSessionOperation(string operationName) {
            if(IsSessionBusy) {
                Debug.LogWarning($"[SessionManager] Ignoring '{operationName}' while session is busy.");
                return false;
            }

            _activeSessionOperations++;
            return true;
        }

        private void EndSessionOperation() {
            if(_activeSessionOperations > 0) {
                _activeSessionOperations--;
            }
        }

        private static UniTask TryLeaveVoiceChannelAsync() =>
            SessionVoice.TryLeaveVoiceChannelAsync();

        private void TryJoinVoiceForSteamSocialLobby(ulong lobbyId, string context) =>
            SessionVoice.TryJoinVoiceForSteamSocialLobby(lobbyId, context,
                () => _isLeaving || _isShuttingDown, (t, l) => LaunchSessionTask(t, l));

        private void TryJoinVoiceForActiveMatch(string context) =>
            SessionVoice.TryJoinVoiceForActiveMatch(this, () => _isLeaving || _isShuttingDown, (t, l) => LaunchSessionTask(t, l), context);

        public bool TryGetActiveVoiceChannelName(out string channelName) {
            channelName = SessionVoice.GetMatchVoiceChannelName(this);
            return !string.IsNullOrEmpty(channelName);
        }

        private void SetExpectedGamePlayerCount(int count, string source) {
            _expectedGamePlayerCount = Mathf.Max(1, count);
            if(Debug.isDebugBuild) {
                Debug.Log($"[SessionManager] Expected gameplay players set to {_expectedGamePlayerCount} ({source}).");
            }
        }

        /// <summary>
        /// Clears matchmaker state, cancelling any active ticket.
        /// </summary>
        private async UniTask ClearMatchmakingStateAsync() {
            if(Debug.isDebugBuild) {
                Debug.Log("[SessionManager] ClearMatchmakingState called");
            }

            _matchmaker.CancelMatchmaking();
            await UniTask.Yield();
        }

        /// <summary>
        /// Clears UGS match lobby state to avoid stale data affecting future matches.
        /// </summary>
        private async UniTask ClearMatchStateAsync() {
            await _matchLobby.ClearMatchStateAsync(this, this, this);
            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings != null) {
                matchSettings.preMatchCountdownEnabled = true;
                matchSettings.swapWeaponsOnDeath = true;
            }

            UpdateSteamRichPresence();
        }

        private UniTask ResetPartyFollowStateIfHostAsync() =>
            SessionParty.ResetPartyFollowStateIfHostAsync(this, this, _matchLobby);

        /// <summary>
        /// Updates the session phase and triggers status change events.
        /// </summary>
        /// <param name="phase">The new session phase.</param>
        /// <param name="message">The status message to display.</param>
        private void SetFrontStatus(SessionPhase phase, string message) {
            Phase = phase;
            EventBus.Publish(new FrontStatusChangedEvent(message));
        }

        private void RegisterNetworkCallbacks() {
            if(TryGetNetworkManager("RegisterNetworkCallbacks", out var networkManager) == false) return;
            SessionNetworkLifecycle.RegisterNetworkCallbacks(
                networkManager,
                this,
                this,
                () => SessionNetworkLifecycle.HasActiveSession,
                source => _sceneFlow.TriggerUnexpectedDisconnectFlow(this, this, source));
        }

        private void UnregisterNetworkCallbacks() {
            SessionNetworkLifecycle.UnregisterNetworkCallbacks(_networkManager);
        }

        private void ApplyRuntimeMode(string mode, string source, bool refreshUi = true) {
            if(string.IsNullOrWhiteSpace(mode)) return;

            var changed = SelectedGameMode != mode;
            SelectedGameMode = mode;

            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings != null && matchSettings.selectedGameModeId != mode) {
                matchSettings.selectedGameModeId = mode;
                changed = true;
            }

            if(Debug.isDebugBuild) {
                Debug.Log($"[SessionManager] Applied mode '{mode}' from {source}.");
            }

            FlowLog.Emit(FlowEventIds.ModeApply,
                ("source", source),
                ("mode", mode),
                ("changed", changed));

            if(changed && refreshUi) {
                EventBus.Publish(new FrontStatusChangedEvent(null));
            }
        }

        private bool TryGetRuntimeMode(out string mode, out string source) =>
            SessionMatchLobby.TryGetRuntimeMode(this, out mode, out source);

        #endregion

        #region Steam join/follow and presence (delegated to SteamSocialBridge)

        private async UniTask HandleSteamConnectStringAsync(string connect) =>
            await _steamSocialBridge.HandleSteamConnectStringAsync(connect);

        private async UniTask FollowSessionContextFromSteamLobbyAsync(Lobby lobby) =>
            await _steamSocialBridge.FollowSessionContextFromSteamLobbyAsync(lobby);

        private async UniTask<bool> JoinSteamSocialLobbyAsync(Lobby lobby) =>
            await _steamSocialBridge.JoinSteamSocialLobbyAsync(lobby);

        private void UpdateSteamRichPresence() => SteamSocialBridge.UpdateSteamRichPresence(this);

        /// <summary>Triggers session property refresh events to notify UI listeners.</summary>
        private static void NotifyPartyStateChanged() {
            EventBus.Publish(new SessionPropertiesRefreshedEvent());
        }

        private void UpdateSteamLobbyWithPartyDataIfOwner() =>
            SteamSocialBridge.UpdateSteamLobbyWithPartyDataIfOwner(this);

        #endregion

        #region Party + Match Flow

        /// <summary>
        /// Creates the UGS party lobby and optionally mirrors party context to a Steam social lobby.
        /// </summary>
        /// <param name="maxPlayers">Maximum members allowed in the party lobby.</param>
        /// <param name="isPrivate">Whether the UGS party lobby should be private.</param>
        public async UniTask CreatePartyLobbyAsync(int maxPlayers, bool isPrivate) =>
            await _party.CreatePartyLobbyAsync(this, this, maxPlayers, isPrivate);

        private async UniTask JoinPartyLobbyByCodeAsync(string code) =>
            await SessionParty.JoinPartyLobbyByCodeAsync(this, this, code);

        private UniTask PreFadePrivateHostAsync() =>
            SessionSceneFlow.RunPreFadePrivateHostAsync(this, v => _ugsHostPreFadedOut = v);

        private UniTask PreFadePublicHostAsync() =>
            SessionSceneFlow.RunPreFadePublicHostAsync(this, v => _ugsHostPreFadedOut = v);

        private UniTask CreatePublicMatchLobbyAsync(string mode, int maxPlayers, string matchId,
            string joinCode) =>
            _matchLobby.CreatePublicMatchLobbyAsync(this, this, this, mode, maxPlayers, matchId, joinCode);

        /// <summary>
        /// Starts a private match from the current UGS party lobby and drives sync-to-load for all members.
        /// </summary>
        /// <param name="mode">Game mode to apply to the created match lobby.</param>
        /// <param name="maxPlayers">Maximum players for relay allocation and match lobby creation.</param>
        public UniTask StartPrivateMatchAsync(string mode, int maxPlayers) =>
            SessionParty.RunStartPrivateMatchAsync(mode, maxPlayers, this, this, this, this, _matchLobby, this);

        private UniTask<bool> JoinMatchLobbyByIdAsync(string lobbyId) =>
            _matchLobby.JoinMatchLobbyByIdAsync(this, this, this, this, lobbyId);

        #endregion

        // ===== Matchmaker (delegated to SessionMatchmakerService; state for JoinPublicMatchByIdAsync/query) =====
        public float MatchmakingStartTime { get; private set; }

        private void SetMatchmakingStartTime(float value) => MatchmakingStartTime = value;

        #region UGS Matchmaker (Ticketing)

        /// <summary>
        /// Cancels active UGS matchmaking polling and clears local ticket state.
        /// </summary>
        public void CancelMatchmaking() => _matchmaker.CancelMatchmaking();

        /// <summary>
        /// Starts UGS quick-play matchmaking for the provided mode and drives host/client follow-up on assignment.
        /// </summary>
        public async UniTask StartMatchmakerQuickPlayAsync(string mode) =>
            await _matchmaker.StartMatchmakerQuickPlayAsync(mode);

        #endregion

        #region Public match host/client (used by SessionMatchmakerService via IMatchmakerSessionActions)

        private UniTask StartPublicMatchAsHostAsync(string mode, int maxPlayers, string matchId,
            StoredMatchmakingResults results) =>
            _matchmaker.RunStartPublicMatchAsHostAsync(mode, maxPlayers, matchId, results);

        private UniTask MarkHostReadyAsync() =>
            SessionMatchLobby.MarkHostReadyAsync(this, this);

        private UniTask JoinPublicMatchByIdAsync(string matchId) =>
            _matchmaker.JoinPublicMatchByIdAsync(matchId);

        #endregion

        private void SetNextUgsHeartbeatTime(float value) => _matchLobby.SetNextHeartbeatTime(value);

        private bool _ugsHostPreFadedOut;

        private void Update() {
            if(_isLeaving || _isShuttingDown) return;
            if(_ugsPartyLobby == null && _ugsMatchLobby == null) return;
            _matchLobby.Tick(this);
        }

        private async UniTask RefreshBackfillEligibilityAsync(bool force = false) {
            await _matchLobby.RefreshBackfillEligibilityAsync(this, force);
        }

        private void CompletePlayersReadyWaiter(bool result) =>
            _matchLobby.CompletePlayersReadyWaiter(result);

        private void SyncModeFromMatchLobby(Unity.Services.Lobbies.Models.Lobby lobby) =>
            SessionMatchLobby.SyncModeFromMatchLobby(this, lobby);

        private UniTask StartMatchSyncAsync(bool skipFadeOut = false) =>
            _matchLobby.StartMatchSyncAsync(this, this, this, skipFadeOut);

        private UniTask StartMatchClientAsync(bool useFadeOut = false, string expectedSessionCode = null,
            bool? expectedIsPrivateMatch = null) =>
            _matchLobby.StartMatchClientAsync(this, this, useFadeOut, expectedSessionCode,
                expectedIsPrivateMatch);

        private async UniTask EnsurePartyLobbySubscriptionAsync(string context) =>
            await _matchLobby.EnsurePartyLobbySubscriptionAsync(this, this, context);

        private async UniTask UnsubscribePartyLobbyAsync(string context) =>
            await _matchLobby.UnsubscribePartyLobbyAsync(context);

        private async UniTask UnsubscribeMatchLobbyAsync(string context) =>
            await _matchLobby.UnsubscribeMatchLobbyAsync(this, context);

        private int _gameScenePresentationSerial;

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
            if(IsGameplaySceneName(scene.name)) {
                LaunchSessionTask(SessionSceneFlow.RunOnGameSceneLoadedAsync(this, this, this),
                    "OnGameSceneLoadedAsync");
            }
        }

        private void EnableGameplaySpawningIfHost() {
            if(_customNetworkManager != null)
                _customNetworkManager.EnableGameplaySpawning();
        }

        private void CaptureDuplicateFpVisualsForDisconnect() =>
            SessionSceneFlow.CaptureDuplicateFpVisualsForDisconnect(this);

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

            _steamSocialBridge = new SteamSocialBridge();
            _matchmaker = new SessionMatchmaker();
            _matchmaker.SetContext(this, this);
            _party = new SessionParty();
            _matchLobby = new SessionMatchLobby();
            _matchmaker.SetMatchLobbyService(_matchLobby);
            _sceneFlow = new SessionSceneFlow();

            SelectedMapSceneName = MatchMapService.DefaultGameplaySceneName;
            SelectedMapId = MatchMapService.DefaultMapId;
        }

        private SteamSocialBridge _steamSocialBridge;
        private SessionMatchmaker _matchmaker;
        private SessionParty _party;
        private SessionMatchLobby _matchLobby;
        private SessionSceneFlow _sceneFlow;

        private void OnEnable() {
            RegisterNetworkCallbacks();
            SceneManager.sceneLoaded += OnSceneLoaded;

            _steamSocialBridge.Register(this, this);

            EventBus.Unsubscribe<GameSettingsChangedEvent>(OnGameSettingsChanged);
            EventBus.Subscribe<GameSettingsChangedEvent>(OnGameSettingsChanged);
        }

        private void OnDisable() {
            UnregisterNetworkCallbacks();
            SceneManager.sceneLoaded -= OnSceneLoaded;

            _steamSocialBridge?.Unregister();

            EventBus.Unsubscribe<GameSettingsChangedEvent>(OnGameSettingsChanged);
        }

        private void OnLocalSettingsChanged() {
            // Streamer mode toggle can change the display name we want other players to see.
            SteamSocialBridge.UpdateLocalDisplayNameInLobby(this);
        }

        private void OnGameSettingsChanged(GameSettingsChangedEvent _) {
            OnLocalSettingsChanged();
        }

        private void Start() {
            if(SteamManager.Instance == null) {
                Debug.LogError("[SessionManager] SteamManager not found!");
            }

            // Bootstrap UGS identity early so Lobby/Matchmaker/Vivox can rely on it later.
            LaunchSessionTask(UgsAuthService.InitAndSignInAsync(),
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
        public UniTask StartOfflinePrivateMatchAsync(string mode) =>
            SessionSceneFlow.RunStartOfflinePrivateMatchAsync(this, this, this, mode);

        private async UniTask<bool> CreateSteamSocialLobbyAsync(int maxMembers) =>
            await _steamSocialBridge.CreateSteamSocialLobbyAsync(maxMembers);

        /// <summary>
        /// Sets the selected gamemode and updates lobby data if hosting.
        /// </summary>
        /// <param name="mode">The gamemode ID.</param>
        public void SetGameMode(string mode) {
            ApplyRuntimeMode(mode, "MenuSelection", refreshUi: false);
            SteamSocialBridge.SetSteamLobbyGameMode(this, mode);
            EventBus.Publish(new FrontStatusChangedEvent(null));
        }

        /// <summary>
        /// Leaves the current Steam social lobby context.
        /// </summary>
        public void LeaveLobby() {
            _steamSocialBridge.LeaveSteamLobby();
            LaunchSessionTask(TryLeaveVoiceChannelAsync(), "LeaveLobby_VoiceLeave");
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
                await SessionSceneFlow.RunLeaveToMenuFlowAsync(this, this, leaveId, skipFadeOut);
            } finally {
                _isLeaving = false;
                FlowLog.Emit(FlowEventIds.SessionExit, ("leaveId", leaveId), ("step", "EXIT_LEAVE_FLAG_CLEARED"));
            }
        }

        private async UniTask EnsureMainMenuLoadedAndReadyAsync(string currentScene) =>
            await SessionSceneFlow.EnsureMainMenuLoadedAndReadyAsync(this, currentScene);

        public static bool IsGameplaySceneName(string sceneName) {
            return MatchMapService.IsGameplayScene(sceneName);
        }

        /// <summary>Removes a party member from the UGS party lobby.</summary>
        public void KickMember(SteamId targetId) => SessionPartyModeration.KickMember(this, targetId);

        /// <summary>Promotes a party member to be the new UGS party host.</summary>
        public void PromoteMember(SteamId targetId) => SessionPartyModeration.PromoteMember(this, targetId);

        #endregion

        #region ISessionContext

        SessionPhase ISessionContext.Phase => Phase;
        float ISessionContext.PhaseStartTime => _phaseStartTime;
        Lobby? ISessionContext.CurrentLobby => CurrentLobby;
        string ISessionContext.CurrentPartyId => CurrentPartyId;
        bool ISessionContext.IsPartyLeader => IsPartyLeader;
        string ISessionContext.SelectedGameMode => SelectedGameMode;
        string ISessionContext.SelectedMapId => SelectedMapId;
        string ISessionContext.SelectedMapSceneName => SelectedMapSceneName;
        Unity.Services.Lobbies.Models.Lobby ISessionContext.UgsPartyLobby => _ugsPartyLobby;
        Unity.Services.Lobbies.Models.Lobby ISessionContext.UgsMatchLobby => _ugsMatchLobby;
        bool ISessionContext.IsInGameplay => IsInGameplay;
        bool ISessionContext.IsLeaving => _isLeaving;
        bool ISessionContext.IsShuttingDown => _isShuttingDown;
        bool ISessionContext.IsExpectedDisconnect => IsExpectedDisconnect;
        bool ISessionContext.IsSearching => IsSearching;
        bool ISessionContext.IsSessionBusy => IsSessionBusy;
        int ISessionContext.ExpectedGamePlayerCount => ExpectedGamePlayerCount;
        CancellationToken ISessionContext.SessionLifetimeToken => SessionLifetimeToken;
        float ISessionContext.MatchmakingStartTime => MatchmakingStartTime;

        void ISessionContext.SetPhase(SessionPhase value) => Phase = value;
        void ISessionContext.SetCurrentLobby(Lobby? value) => CurrentLobby = value;
        void ISessionContext.SetCurrentPartyId(string value) => CurrentPartyId = value;
        void ISessionContext.SetIsPartyLeader(bool value) => IsPartyLeader = value;
        void ISessionContext.SetUgsPartyLobby(Unity.Services.Lobbies.Models.Lobby value) => _ugsPartyLobby = value;
        void ISessionContext.SetUgsMatchLobby(Unity.Services.Lobbies.Models.Lobby value) => _ugsMatchLobby = value;
        void ISessionContext.SetIsInGameplay(bool value) => IsInGameplay = value;
        void ISessionContext.SetIsExpectedDisconnect(bool value) => IsExpectedDisconnect = value;
        void ISessionContext.SetPrivateMatchMapPreset(bool value) => SetPrivateMatchMapPreset(value);
        void ISessionContext.SetMatchmakingStartTime(float value) => SetMatchmakingStartTime(value);
        void ISessionContext.SetNextUgsHeartbeatTime(float value) => SetNextUgsHeartbeatTime(value);

        void ISessionContext.LaunchSessionTask(UniTask task, string label) => LaunchSessionTask(task, label);

        bool ISessionContext.TryGetNetworkManager(string operationName, out NetworkManager networkManager) =>
            TryGetNetworkManager(operationName, out networkManager);

        bool ISessionContext.TryGetUnityTransport(string operationName, out NetworkManager networkManager,
            out UnityTransport transport) =>
            TryGetUnityTransport(operationName, out networkManager, out transport);

        bool ISessionContext.TryBeginSessionOperation(string operationName) => TryBeginSessionOperation(operationName);
        void ISessionContext.EndSessionOperation() => EndSessionOperation();

        void ISessionContext.SetFrontStatus(SessionPhase phase, string message) => SetFrontStatus(phase, message);

        void ISessionContext.SetExpectedGamePlayerCount(int count, string source) =>
            SetExpectedGamePlayerCount(count, source);

        void ISessionContext.ApplyRuntimeMode(string mode, string source, bool refreshUi) =>
            ApplyRuntimeMode(mode, source, refreshUi);

        void ISessionContext.LeaveLobby() => LeaveLobby();
        UniTask ISessionContext.LeaveToMainMenuAsync(bool skipFadeOut) => LeaveToMainMenuAsync(skipFadeOut);
        UniTask ISessionContext.EnsureSignedInAsync() => EnsureSignedInAsync();
        void ISessionContext.NotifyPartyStateChanged() => NotifyPartyStateChanged();
        void ISessionContext.UpdateSteamRichPresence() => UpdateSteamRichPresence();
        void ISessionContext.UpdateLocalDisplayNameInLobby() => SteamSocialBridge.UpdateLocalDisplayNameInLobby(this);

        #endregion

        #region ISteamSessionActions

        UniTask<bool> ISteamSessionActions.JoinSteamSocialLobbyAsync(Lobby lobby) => JoinSteamSocialLobbyAsync(lobby);

        UniTask ISteamSessionActions.FollowSessionContextFromSteamLobbyAsync(Lobby lobby) =>
            FollowSessionContextFromSteamLobbyAsync(lobby);

        UniTask ISteamSessionActions.HandleSteamConnectStringAsync(string connect) =>
            HandleSteamConnectStringAsync(connect);

        void ISteamSessionActions.TryJoinVoiceForSteamSocialLobby(ulong lobbyId, string context) =>
            TryJoinVoiceForSteamSocialLobby(lobbyId, context);

        UniTask ISteamSessionActions.JoinPartyLobbyByCodeAsync(string code) => JoinPartyLobbyByCodeAsync(code);
        UniTask<bool> ISteamSessionActions.JoinMatchLobbyByIdAsync(string lobbyId) => JoinMatchLobbyByIdAsync(lobbyId);

        #endregion

        #region IMatchmakerSessionActions

        UniTask<bool> IMatchmakerSessionActions.JoinMatchLobbyByIdAsync(string lobbyId) =>
            JoinMatchLobbyByIdAsync(lobbyId);

        UniTask IMatchmakerSessionActions.StartPublicMatchAsHostAsync(string mode, int maxPlayers, string matchId,
            StoredMatchmakingResults results) =>
            StartPublicMatchAsHostAsync(mode, maxPlayers, matchId, results);

        UniTask IMatchmakerSessionActions.JoinPublicMatchByIdAsync(string matchId) => JoinPublicMatchByIdAsync(matchId);

        UniTask<string> IMatchmakerSessionActions.CreateDaSessionAsync(int maxPlayers,
            bool isPrivateMatch, string contextLabel) =>
            CreateDaSessionAsync(maxPlayers, isPrivateMatch, contextLabel);

        UniTask IMatchmakerSessionActions.CreatePublicMatchLobbyAsync(string mode, int maxPlayers, string matchId,
            string joinCode) =>
            CreatePublicMatchLobbyAsync(mode, maxPlayers, matchId, joinCode);

        UniTask IMatchmakerSessionActions.PreFadePublicHostAsync() => PreFadePublicHostAsync();

        UniTask IMatchmakerSessionActions.MarkHostReadyAsync() => MarkHostReadyAsync();

        UniTask<bool> IMatchmakerSessionActions.TrySetMatchLobbyStateAsync(string lobbyState,
            DataObject.VisibilityOptions visibility, string context) =>
            TrySetMatchLobbyStateAsync(lobbyState, visibility, context);

        bool IMatchmakerSessionActions.TryLoadGameplaySceneAsHost(string contextLabel) =>
            TryLoadGameplaySceneAsHost(contextLabel);

        #endregion

        #region IPartySessionActions

        UniTask IPartySessionActions.UnsubscribeMatchLobbyAsync(string context) =>
            UnsubscribeMatchLobbyAsync(context);

        UniTask IPartySessionActions.UnsubscribePartyLobbyAsync(string context) =>
            UnsubscribePartyLobbyAsync(context);

        UniTask IPartySessionActions.EnsurePartyLobbySubscriptionAsync(string context) =>
            EnsurePartyLobbySubscriptionAsync(context);

        UniTask<bool> IPartySessionActions.CreateSteamSocialLobbyAsync(int maxPlayers) =>
            CreateSteamSocialLobbyAsync(maxPlayers);

        void IPartySessionActions.SetNextUgsHeartbeatTime(float value) => SetNextUgsHeartbeatTime(value);
        void IPartySessionActions.UpdateSteamLobbyWithPartyDataIfOwner() => UpdateSteamLobbyWithPartyDataIfOwner();
        void IPartySessionActions.TryJoinVoiceForActiveMatch(string context) => TryJoinVoiceForActiveMatch(context);

        #endregion

        #region ILobbyEventActions

        void ILobbyEventActions.CompletePlayersReadyWaiter(bool result) =>
            CompletePlayersReadyWaiter(result);

        #endregion

        #region IMatchSnapshotActions

        void IMatchSnapshotActions.SyncModeFromMatchLobby(Unity.Services.Lobbies.Models.Lobby lobby) =>
            SyncModeFromMatchLobby(lobby);

        UniTask IMatchSnapshotActions.StartMatchSyncAsync(bool skipFadeOut) =>
            StartMatchSyncAsync(skipFadeOut);

        UniTask IMatchSnapshotActions.StartMatchClientAsync(bool useFadeOut, string expectedSessionCode,
            bool? expectedIsPrivateMatch) =>
            StartMatchClientAsync(useFadeOut, expectedSessionCode, expectedIsPrivateMatch);

        UniTask IMatchSnapshotActions.FadeOutWithFallbackAsync(int fallbackDelayMs) =>
            FadeOutWithFallbackAsync(fallbackDelayMs);

        UniTask IMatchSnapshotActions.LeaveToMainMenuAsync(bool skipFadeOut) => LeaveToMainMenuAsync(skipFadeOut);

        UniTask<SessionNetworkLifecycle.DaSessionJoinResult>
            IMatchSnapshotActions.JoinDaSessionAsync(string sessionCode, bool isPrivateMatch,
                string contextLabel) =>
            JoinDaSessionAsync(sessionCode, isPrivateMatch, contextLabel);

        UniTask<bool> IMatchSnapshotActions.JoinMatchLobbyByIdAsync(string lobbyId) =>
            JoinMatchLobbyByIdAsync(lobbyId);

        bool IMatchSnapshotActions.UgsLocalReadySubmitted { get; set; }

        bool IMatchSnapshotActions.UgsSyncInProgress { get; set; }

        bool IMatchSnapshotActions.UgsClientStartedForMatch { get; set; }

        bool IMatchSnapshotActions.UgsHostPreFadedOut {
            get => _ugsHostPreFadedOut;
            set => _ugsHostPreFadedOut = value;
        }

        #endregion

        #region IDistributedAuthorityActions

        void IDistributedAuthorityActions.BindActiveSession(ISession session) =>
            SessionNetworkLifecycle.BindActiveSession(session, this, this,
                () => _networkManager != null && _networkManager.IsListening &&
                      IsGameplaySceneName(SceneManager.GetActiveScene().name));

        void IDistributedAuthorityActions.UnbindActiveSession() =>
            SessionNetworkLifecycle.UnbindActiveSession();

        bool IDistributedAuthorityActions.IsLocalPlayerMatchLobbyHost(Unity.Services.Lobbies.Models.Lobby lobby) =>
            SessionMatchLobby.IsLocalPlayerLobbyHost(lobby);

        void IDistributedAuthorityActions.OnPromotedToMatchHost() =>
            _matchLobby.ResetMatchHeartbeatForNewHost();

        #endregion

        #region INetworkLifecycleActions

        UniTask INetworkLifecycleActions.LeaveActiveSessionAsync(string contextLabel) =>
            LeaveActiveSessionAsync(contextLabel);

        UniTask INetworkLifecycleActions.CleanupNetworkAsync() => CleanupNetworkAsync();

        #endregion

        #region ISceneFlowActions

        string ISceneFlowActions.GetActiveSceneName() => SceneManager.GetActiveScene().name;
        bool ISceneFlowActions.IsGameplaySceneName(string sceneName) => IsGameplaySceneName(sceneName);
        void ISceneFlowActions.SetFrontStatus(SessionPhase phase, string message) => SetFrontStatus(phase, message);

        UniTask ISceneFlowActions.FadeOutWithFallbackAsync(int fallbackDelayMs) =>
            FadeOutWithFallbackAsync(fallbackDelayMs);

        UniTask ISceneFlowActions.LeaveToMainMenuAsync(bool skipFadeOut) => LeaveToMainMenuAsync(skipFadeOut);
        void ISceneFlowActions.CaptureDuplicateFpVisualsForDisconnect() => CaptureDuplicateFpVisualsForDisconnect();

        #endregion

        #region ILeaveToMenuActions

        UniTask ILeaveToMenuActions.ClearMatchmakingStateAsync() => ClearMatchmakingStateAsync();
        UniTask ILeaveToMenuActions.TryLeaveVoiceChannelAsync() => TryLeaveVoiceChannelAsync();
        UniTask ILeaveToMenuActions.ResetPartyFollowStateIfHostAsync() => ResetPartyFollowStateIfHostAsync();
        void ILeaveToMenuActions.LeaveLobby() => LeaveLobby();
        UniTask ILeaveToMenuActions.ClearMatchStateAsync() => ClearMatchStateAsync();
        UniTask ILeaveToMenuActions.CleanupNetworkAsync() => CleanupNetworkAsync();

        UniTask ILeaveToMenuActions.EnsureMainMenuLoadedAndReadyAsync(string currentScene) =>
            EnsureMainMenuLoadedAndReadyAsync(currentScene);

        string ILeaveToMenuActions.GetActiveSceneName() => SceneManager.GetActiveScene().name;

        #endregion

        #region IHostMapSceneActions

        bool IHostMapSceneActions.TryGetNetworkManager(string context, out NetworkManager networkManager) =>
            TryGetNetworkManager(context, out networkManager);

        void IHostMapSceneActions.SetSelectedMap(string mapId, string sceneName) => SetSelectedMap(mapId, sceneName);
        void IHostMapSceneActions.SetSelectedMapFromId(string mapId) => SetSelectedMapFromId(mapId);
        bool IHostMapSceneActions.ConsumePrivateMatchMapPreset() => ConsumePrivateMatchMapPreset();
        void IHostMapSceneActions.LoadScene(string sceneName) => LoadSceneAsHost(sceneName);

        void IHostMapSceneActions.SetSteamLobbyMapIfOwner(string mapId, string sceneName) =>
            SetSteamLobbyMapIfOwner(mapId, sceneName);

        #endregion

        #region IOnGameSceneLoadedActions

        bool IOnGameSceneLoadedActions.TryGetRuntimeMode(out string mode, out string source) =>
            TryGetRuntimeMode(out mode, out source);

        void IOnGameSceneLoadedActions.TryJoinVoiceForActiveMatch(string context) =>
            TryJoinVoiceForActiveMatch(context);

        UniTask IOnGameSceneLoadedActions.TrySetMatchLobbyStateAsync(string lobbyState,
            DataObject.VisibilityOptions visibility, string context) =>
            TrySetMatchLobbyStateAsync(lobbyState, visibility, context);

        UniTask IOnGameSceneLoadedActions.RefreshBackfillEligibilityAsync(bool force) =>
            RefreshBackfillEligibilityAsync(force);

        UniTask IOnGameSceneLoadedActions.UnsubscribeMatchLobbyAsync(string context) =>
            UnsubscribeMatchLobbyAsync(context);

        bool IOnGameSceneLoadedActions.TryGetNetworkManager(out NetworkManager networkManager) =>
            TryGetNetworkManager("OnGameSceneLoaded", out networkManager);

        void IOnGameSceneLoadedActions.EnableGameplaySpawningIfHost() =>
            EnableGameplaySpawningIfHost();

        int IOnGameSceneLoadedActions.StartGameScenePresentation() => ++_gameScenePresentationSerial;

        bool IOnGameSceneLoadedActions.IsCurrentGameScenePresentation(int serial) =>
            serial == _gameScenePresentationSerial;

        bool IOnGameSceneLoadedActions.IsMatchLobbyPublic() =>
            _ugsMatchLobby is { Data: not null } &&
            _ugsMatchLobby.Data.TryGetValue(UgsMatchTypeKey, out var matchTypeObj) &&
            matchTypeObj != null &&
            string.Equals(matchTypeObj.Value, "Public", StringComparison.OrdinalIgnoreCase);

        #endregion

        #region IPrivateMatchHostActions

        UniTask IPrivateMatchHostActions.PreFadePrivateHostAsync() => PreFadePrivateHostAsync();
        UniTask<string> IPrivateMatchHostActions.CreateDaSessionAsync(int maxPlayers, bool isPrivateMatch, string contextLabel) =>
            CreateDaSessionAsync(maxPlayers, isPrivateMatch, contextLabel);
        UniTask<bool> IPrivateMatchHostActions.TrySetMatchLobbyStateAsync(string lobbyState, DataObject.VisibilityOptions visibility, string context) =>
            TrySetMatchLobbyStateAsync(lobbyState, visibility, context);
        bool IPrivateMatchHostActions.TryLoadGameplaySceneAsHost(string contextLabel) => TryLoadGameplaySceneAsHost(contextLabel);
        UniTask IPrivateMatchHostActions.LeaveToMainMenuAsync(bool skipFadeOut) => LeaveToMainMenuAsync(skipFadeOut);

        #endregion

        #region Internal / Networking

        private static async UniTask FadeOutWithFallbackAsync(int fallbackDelayMs = 500) =>
            await SessionSceneFlow.FadeOutWithFallbackAsync(fallbackDelayMs);

        private static async UniTask EnsureSignedInAsync() {
            await UgsAuthService.InitAndSignInAsync();
        }

        #endregion
    }
}