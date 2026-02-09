using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Settings;
using Game.Social;
using Network.Core;
using Network.Diagnostics;
using Network.Singletons;
using Network.Steam;
using Steamworks;
using Steamworks.Data;
using Unity.Services.Authentication;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityUtils;
using Network.UGS;
using Unity.Services.Lobbies;
using Unity.Services.Relay;
using Unity.Services.Matchmaker;
using Unity.Services.Matchmaker.Models;

namespace Network {
    /// <summary>
    /// Steamworks-based Session Manager.
    /// Handles Lobby creation, joining, and matchmaking (Quick Play).
    /// Orchestrates NetworkManager start/stop using FacepunchTransport.
    /// </summary>
    public sealed class SessionManager : Singleton<SessionManager> {
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

        // ===== Events =====
        public event Action OnPartyStateChanged;

        // ===== State =====
        public Lobby? CurrentLobby { get; private set; }
        private SessionPhase _phase;
        private float _phaseStartTime;
        private SessionPhase Phase {
            get => _phase;
            set {
                if (_phase != value) {
                    var previous = _phase;
                    _phase = value;
                    _phaseStartTime = Time.time;
                    if (Debug.isDebugBuild) Debug.Log($"[SessionManager] Phase -> {value}");
                    FlowLog.Emit(FlowEventIds.SyncStateTransition,
                        ("from", previous),
                        ("to", value));
                }
            }
        }
        public bool IsInGameplay { get; private set; }
        public string SelectedGameMode { get; private set; } = "Deathmatch";
        public string FlowSessionId {
            get {
                if(_ugsMatchLobby != null && string.IsNullOrEmpty(_ugsMatchLobby.Id) == false) return _ugsMatchLobby.Id;
                if(CurrentLobby.HasValue && CurrentLobby.Value.Id != 0) return CurrentLobby.Value.Id.ToString();
                if(_ugsPartyLobby != null && string.IsNullOrEmpty(_ugsPartyLobby.Id) == false) return _ugsPartyLobby.Id;
                if(string.IsNullOrEmpty(CurrentPartyId) == false) return CurrentPartyId;
                return "";
            }
        }

        private const string GameSceneName = "Game";
        public string CurrentPartyId { get; private set; }
        public bool IsPartyLeader { get; private set; }

        private const string HostAddressKey = "HostAddress";
        private const string GameModeKey = "GameMode";
        private const string PartyIdKey = "PartyId";
        private const string DisplayNameKey = "DisplayName";
        private const string AvatarHiddenKey = "AvatarHidden";
        private const string PlayerIconKey = "PlayerIcon";
        private const string FollowLobbyIdKey = "FollowLobbyId";
        private const string LobbyStateKey = "LobbyState";
        private const string TargetModeKey = "TargetMode";
        private const string MemberReadyKey = "ReadyToLoad";

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
        private CancellationTokenSource _ugsLobbyCts;
        private float _nextUgsHeartbeatTime;
        private float _nextUgsPollTime;
        private const float UgsHeartbeatIntervalSeconds = 15f;
        private const float UgsPollIntervalSeconds = 2f;
        private bool _ugsSyncInProgress;
        private bool _ugsLocalReadySubmitted;
        private bool _ugsClientStartedForMatch;
        private bool _ugsHostPreFadedOut;

        // ===== Matchmaker state =====
        private string _matchmakerTicketId;
        private string _matchmakerQueueName;
        private CancellationTokenSource _matchmakerCts;
        private readonly List<ulong> _clientsFinishedLoading = new();
        private CustomNetworkManager _customNetworkManager;
        private NetworkManager _networkManager;
        private bool _isLeaving;
        private bool _hasCompletedInitialLoad;
        private bool _isShuttingDown;
        private CancellationTokenSource _matchmakingCts;
        public float MatchmakingStartTime { get; private set; }

        // Track if we expect a disconnect (e.g. intentionally leaving)
        private bool _expectedDisconnect;

        public bool IsSearching {
            get {
                return Phase switch {
                    SessionPhase.Searching or SessionPhase.CreatingLobby or SessionPhase.JoiningLobby
                        or SessionPhase.StartingClient or SessionPhase.SynchronizingLoad
                        or SessionPhase.LoadingScene => true,
                    SessionPhase.LobbyReady =>
                        // We are only "Locked/Searching" if the lobby is public (queueing)
                        CurrentLobby.HasValue && CurrentLobby.Value.GetData(GameModeKey) == "Public",
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
                        return false;
                    // Don't show status card for private lobbies when they are ready (except when syncing load)
                    case SessionPhase.LobbyReady:
                    case SessionPhase.StartingHost: {
                        if(CurrentLobby.HasValue && CurrentLobby.Value.GetData(GameModeKey) == "Private") return false;
                        break;
                    }
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

        public bool UseUgsBackend {
            get { return true; }
        }

        // Events
        public event Action<string> FrontStatusChanged;

        #region Unity Lifecycle

        protected override void Awake() {
            if(HasInstance && Instance != this) {
                Destroy(gameObject);
                return;
            }

            DontDestroyOnLoad(gameObject);

            _networkManager = NetworkManager.Singleton;
            if(_networkManager != null) {
                _customNetworkManager = _networkManager.GetComponent<CustomNetworkManager>();
            }
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

            GameSettings.OnSettingsChanged += OnLocalSettingsChanged;
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

            GameSettings.OnSettingsChanged -= OnLocalSettingsChanged;
        }

        private void OnLocalSettingsChanged() {
            // Streamer mode toggle can change the display name we want other players to see.
            UpdateLocalDisplayNameInLobby();
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
            } catch {
                // If Steam is transitioning offline, ignore.
            }
        }

        private void Start() {
            if(SteamManager.Instance == null) {
                Debug.LogError("[SessionManager] SteamManager not found!");
            }

            // Bootstrap UGS identity early so Lobby/Matchmaker/Vivox can rely on it later.
            UgsAuthService.InitializeAndSignInAsync().Forget();
        }

        private void OnDestroy() {
            if(_isShuttingDown) return;
            LeaveLobby();
        }

        private void OnApplicationQuit() {
            _isShuttingDown = true;
        }

        #endregion

        #region Steam Callbacks

        private void OnLobbyDataChanged(Lobby lobby) {
            if(lobby.Id != CurrentLobby?.Id) return;
            // Handle GameMode display
            var mode = lobby.GetData(TargetModeKey);
            if(!string.IsNullOrEmpty(mode) && mode != SelectedGameMode) {
                ApplyRuntimeMode(mode, "SteamLobbyDataChanged");
            }

            // Handle Party Persistence
            var partyId = lobby.GetData(PartyIdKey);
            if(!string.IsNullOrEmpty(partyId) && partyId != CurrentPartyId) {
                CurrentPartyId = partyId;
            }

            if(lobby.Owner.Id != 0) {
                var amIHost = lobby.Owner.Id == SteamClient.SteamId;

                if(IsPartyLeader != amIHost) {
                    Debug.Log($"[SessionManager] Host changed to {lobby.Owner.Name}. Migrating Netcode...");
                    IsPartyLeader = amIHost;
                    if(FrontStatusChanged != null) {
                        FrontStatusChanged.Invoke(null);
                    }

                    MigrateNetcodeToNewHost(lobby.Owner.Id).Forget();
                }
            }

            // Handle "Follow Leader" Migration
            var followIdStr = lobby.GetData(FollowLobbyIdKey);
            if(!string.IsNullOrEmpty(followIdStr) && Phase != SessionPhase.InGame) {
                var followId = ulong.Parse(followIdStr);
                if(followId != lobby.Id) {
                    Debug.Log($"[SessionManager] Leader moved to lobby {followId}. Following...");
                    JoinSessionByLobbyIdAsync(followId).Forget();
                }
            }

            // Handle Synchronization
            var state = lobby.GetData(LobbyStateKey);
            switch(state) {
                case "SynchronizingLoad": {
                    if(Phase != SessionPhase.SynchronizingLoad && Phase != SessionPhase.LoadingScene) {
                        HandleSynchronizationStart().Forget();
                    }

                    break;
                }
                case "LoadingScene": {
                    if(Phase != SessionPhase.LoadingScene) {
                        Phase = SessionPhase.LoadingScene;
                        BeginSceneLoad();
                    }

                    break;
                }
            }
        }

        /// <summary>
        /// Handles the transition into the synchronization phase before loading a scene.
        /// </summary>
        private async UniTask HandleSynchronizationStart() {
            SetFrontStatus(SessionPhase.SynchronizingLoad, "Waiting for party...");

            // Trigger Fade Out via SceneTransitionManager
            if(SceneTransitionManager.Instance != null) {
                await SceneTransitionManager.Instance.FadeOutAsync();
            } else {
                // Fallback if no transition manager
                await UniTask.Delay(500);
            }

            // Once fully black, report ready
            if(CurrentLobby != null) {
                CurrentLobby.Value.SetMemberData(MemberReadyKey, "true");
            }
        }

        /// <summary>
        /// Steam callback for when a member's data (like readiness) changes.
        /// </summary>
        private void OnLobbyMemberDataChanged(Lobby lobby, Friend friend) {
            if(lobby.Id != CurrentLobby?.Id) return;

            // Refresh any UI that depends on member data (e.g. streamer-mode display names).
            NotifyPartyStateChanged();

            // Host monitors member readiness
            if(IsPartyLeader && Phase == SessionPhase.SynchronizingLoad) {
                CheckAllMembersReady();
            }
        }

        /// <summary>
        /// Checks if all members are ready to load the scene.
        /// </summary>
        private void CheckAllMembersReady() {
            if(!CurrentLobby.HasValue) return;

            var members = CurrentLobby.Value.Members.ToList();
            var allReady = true;
            foreach(var member in members) {
                if(CurrentLobby.Value.GetMemberData(member, MemberReadyKey) == "true") continue;
                allReady = false;
                break;
            }

            if(!allReady) return;
            Debug.Log("[SessionManager] All members ready! Starting scene transition...");
            CurrentLobby.Value.SetData(LobbyStateKey, "LoadingScene");
        }

        /// <summary>
        /// initiates the Netcode scene load.
        /// </summary>
        private void BeginSceneLoad() {
            string mode = null;
            if(CurrentLobby != null) {
                mode = CurrentLobby.Value.GetData(TargetModeKey);
            }
            if(!string.IsNullOrEmpty(mode)) {
                ApplyRuntimeMode(mode, "SteamBeginSceneLoad", refreshUi: false);
            }

            if(IsPartyLeader) {
                _networkManager.SceneManager.LoadScene(GameSceneName, LoadSceneMode.Single);
            }
        }

        private static void OnLobbyMemberJoined(Lobby lobby, Friend friend) {
            Debug.Log($"[SessionManager] Member Joined: {friend.Name}");
            NotifyPartyStateChanged();
        }

        private static void OnLobbyMemberLeave(Lobby lobby, Friend friend) {
            Debug.Log($"[SessionManager] Member Left: {friend.Name}");
            NotifyPartyStateChanged();
        }

        private async void OnGameLobbyJoinRequested(Lobby lobby, SteamId id) {
            Debug.Log($"[SessionManager] Accepted Invite to Lobby {lobby.Id}");
            await JoinSessionByLobbyAsync(lobby);
        }

        private void OnGameRichPresenceJoinRequested(Friend friend, string connect) {
            if(string.IsNullOrEmpty(connect)) return;
            HandleSteamConnectStringAsync(connect).Forget();
        }

        private async UniTaskVoid HandleSteamConnectStringAsync(string connect) {
            // Expected formats:
            // - "UGS_PARTY_CODE:<lobbyCode>"
            // - "UGS_MATCH_ID:<lobbyId>"
            const string partyPrefix = "UGS_PARTY_CODE:";
            const string matchPrefix = "UGS_MATCH_ID:";

            if(connect.StartsWith(partyPrefix)) {
                var code = connect[partyPrefix.Length..];
                if(string.IsNullOrEmpty(code)) return;
                await JoinUgsPartyLobbyByCodeAsync(code);
                return;
            }

            if(connect.StartsWith(matchPrefix)) {
                var lobbyId = connect[matchPrefix.Length..];
                if(string.IsNullOrEmpty(lobbyId)) return;
                await JoinUgsMatchLobbyByIdAsync(lobbyId);
            }
        }

        /// <summary>
        /// Triggers the OnPartyStateChanged event to notify UI listeners.
        /// </summary>
        private static void NotifyPartyStateChanged() {
            if(!HasInstance) return;
            var instance = Instance;
            if(instance.OnPartyStateChanged != null) {
                instance.OnPartyStateChanged.Invoke();
            }
        }

        #endregion

        /// <summary>
        /// Synchronizes the start of a private match across all party members.
        /// </summary>
        /// <param name="mode">The gamemode ID to start.</param>
        public async UniTask StartPrivateMatchSync(string mode) {
            if(!IsPartyLeader || !CurrentLobby.HasValue) return;

            ApplyRuntimeMode(mode, "SteamPrivateMatchSyncHost");
            // Set targets for everyone
            CurrentLobby.Value.SetData(TargetModeKey, mode);

            // Keep the lobby's mode as Private. GameModeKey is used as the public/private discriminator
            // throughout the menu/session logic.
            CurrentLobby.Value.SetData(GameModeKey, "Private");

            // Start the synchronization phase
            Phase = SessionPhase.SynchronizingLoad;

            // Clear previous ready states
            foreach(var unused in CurrentLobby.Value.Members) {
                CurrentLobby.Value.SetMemberData(MemberReadyKey, "false");
            }

            // Broadcast state to trigger synchronization on all clients

            // Trigger UI update
            if(FrontStatusChanged != null) {
                FrontStatusChanged.Invoke("Synchronizing party...");
            }

            // Set data to trigger everyone else
            CurrentLobby.Value.SetData(LobbyStateKey, "SynchronizingLoad");

            // We need to also lock ourselves in.
            await HandleSynchronizationStart();
        }

        #region Public API - Matchmaking

        /// <summary>
        /// Creates a private Steam lobby and starts the Netcode host.
        /// </summary>
        /// <returns>True if successful.</returns>
        public async UniTask<bool> CreatePrivateLobbyAsync() {
            SetFrontStatus(SessionPhase.CreatingLobby, "Creating Private Lobby...");

            // 1. Leave current
            LeaveLobby();
            await CleanupNetworkAsync();

            try {
                // 2. Create Steam Lobby
                var result = await SteamMatchmaking.CreateLobbyAsync(16);
                if(!result.HasValue) {
                    SetFrontStatus(SessionPhase.Error, "Failed to create lobby.");
                    return false;
                }

                CurrentLobby = result.Value;
                CurrentLobby.Value.SetPrivate(); // Friends Only
                CurrentLobby.Value.SetData(HostAddressKey, SteamClient.SteamId.ToString());
                CurrentLobby.Value.SetData(GameModeKey, "Private");

                if(string.IsNullOrEmpty(CurrentPartyId)) {
                    CurrentPartyId = Guid.NewGuid().ToString();
                }

                IsPartyLeader = true; // We created this!
                CurrentLobby.Value.SetData(PartyIdKey, CurrentPartyId);
                CurrentLobby.Value.SetMemberData(PartyIdKey, CurrentPartyId);
                UpdateLocalDisplayNameInLobby();

                // Join Voice Channel (only if logged in, otherwise it will be joined after login)
                if (VoiceManager.Instance != null && VoiceManager.Instance.IsLoggedIn) {
                    VoiceManager.Instance.JoinChannelAsync("match_" + CurrentLobby.Value.Id).Forget();
                }

                SetFrontStatus(SessionPhase.LobbyReady, "Lobby Ready. Invite Friends!");
                FlowLog.Emit(FlowEventIds.PartyLifecycle,
                    ("action", "CreateSteamPrivate"),
                    ("partyId", CurrentPartyId),
                    ("steamLobbyId", CurrentLobby.Value.Id),
                    ("role", "Host"));

                // 3. Start Host (using FacepunchTransport)
                StartHost();
                return true;
            } catch(Exception ex) {
                Debug.LogError(ex);
                SetFrontStatus(SessionPhase.Error, "Error creating lobby.");
                return false;
            }
        }

        /// <summary>
        /// Searches for an open public lobby or creates one if none are found.
        /// </summary>
        /// <param name="mode">The gamemode ID to search for.</param>
        public async UniTask FindGameAsync(string mode = null) {
            // Cancel any previous search
            CancelMatchmaking();

            if(!string.IsNullOrEmpty(mode)) {
                ApplyRuntimeMode(mode, "SteamFindGame");
            }

            _matchmakingCts = new CancellationTokenSource();
            var token = _matchmakingCts.Token;

            try {
                // Cleanup before we start searching so Phase=Menu doesn't flicker
                // LeaveLobby(); // <-- REMOVED! Do NOT leave lobby if we are bringing a party!
                await CleanupNetworkAsync();

                SetFrontStatus(SessionPhase.Searching, $"Searching for {SelectedGameMode}...");
                MatchmakingStartTime = Time.time;

                if(token.IsCancellationRequested) return;

                // 1. Search
                var lobbies = await SteamMatchmaking.LobbyList
                    .WithKeyValue(GameModeKey, "Public")
                    .WithKeyValue("TargetMode", SelectedGameMode)
                    .RequestAsync();

                if(token.IsCancellationRequested) return;

                // Matchmaking Logic:
                // If we are in a party, we search for a lobby with enough available slots.
                var myPartySize = CurrentLobby.HasValue ? CurrentLobby.Value.MemberCount : 1;

                if(lobbies != null) {
                    foreach(var lobby in lobbies) {
                        if(token.IsCancellationRequested) return;

                        var availableSlots = lobby.MaxMembers - lobby.MemberCount;
                        if(availableSlots < myPartySize) continue;
                        Debug.Log(
                            $"[SessionManager] Found Lobby {lobby.Id} with {availableSlots} slots. Joining...");

                        if(CurrentLobby.HasValue) {
                            CurrentLobby.Value.SetData(FollowLobbyIdKey, lobby.Id.ToString());
                            await UniTask.Yield();
                        }

                        await JoinSessionByLobbyAsync(lobby);
                        return;
                    }
                }

                if(token.IsCancellationRequested) return;

                // No suitable lobby found -> Create public or convert existing
                if(CurrentLobby.HasValue && CurrentLobby.Value.GetData(GameModeKey) == "Private") {
                    Debug.Log($"[SessionManager] Reusing private lobby for public game.");
                    CurrentLobby.Value.SetPublic();
                    CurrentLobby.Value.SetJoinable(true);
                    CurrentLobby.Value.SetData(GameModeKey, "Public");
                    CurrentLobby.Value.SetData("TargetMode", SelectedGameMode);

                    StartHost();
                    SetFrontStatus(SessionPhase.LobbyReady, "Waiting for players...");
                } else {
                    Debug.Log($"[SessionManager] No {SelectedGameMode} lobbies found. Creating new public lobby.");
                    SetFrontStatus(SessionPhase.CreatingLobby, $"Creating {SelectedGameMode} Lobby...");

                    var maxPlayers = 10;
                    if(Game.Match.MatchSettingsManager.Instance != null) {
                        var def = Game.Match.MatchSettingsManager.Instance.GetGamemodeDef(SelectedGameMode);
                        if(def.maxPlayers > 0) maxPlayers = def.maxPlayers;
                    }

                    var result = await SteamMatchmaking.CreateLobbyAsync(maxPlayers);
                    if(token.IsCancellationRequested) {
                        if(result.HasValue) result.Value.Leave();
                        return;
                    }

                    if(result.HasValue) {
                        CurrentLobby = result.Value;
                        CurrentLobby.Value.SetPublic();
                        CurrentLobby.Value.SetJoinable(true);
                        // Also set MaxMembers based on Gamemode?
                        // Steam Lobby default is often 16 or user defined.
                        // We should set it to Gamemode.MaxPlayers (e.g. 10).
                        if(Game.Match.MatchSettingsManager.Instance != null) {
                            var def = Game.Match.MatchSettingsManager.Instance.GetGamemodeDef(SelectedGameMode);
                            if(def.maxPlayers > 0) {
                                // CurrentLobby.Value.SetMemberLimit(def.MaxPlayers); // Not exposed in Facepunch.Steamworks struct easily? 
                                // Actually it is: SetMemberLimit is not on the Struct, usually on the Lobby object.
                                // But Facepunch Lobby struct has limited setters. 
                                // We set it during CreateLobbyAsync(maxMembers).
                                // Since we created with 16 above... we might want to change it.
                                // Refactoring CreateLobbyAsync(16) -> CreateLobbyAsync(GamemodeMax).
                            }
                        }

                        CurrentLobby.Value.SetData(HostAddressKey, SteamClient.SteamId.ToString());
                        CurrentLobby.Value.SetData(GameModeKey, "Public");
                        CurrentLobby.Value.SetData("TargetMode", SelectedGameMode);
                        CurrentLobby.Value.SetData(PartyIdKey, CurrentPartyId);

                        StartHost();
                        SetFrontStatus(SessionPhase.LobbyReady, "Waiting for players...");
                    } else {
                        SetFrontStatus(SessionPhase.Error, "Failed to create match.");
                    }
                }
            } catch(OperationCanceledException) {
                Debug.Log("[SessionManager] Matchmaking Cancelled.");
            } finally {
                if(_matchmakingCts != null) {
                    _matchmakingCts.Dispose();
                    _matchmakingCts = null;
                }
            }
        }

        /// <summary>
        /// Cancels current matchmaking search and reverts the lobby state.
        /// </summary>
        public void CancelMatchmaking() {
            Debug.Log($"[SessionManager] CancelMatchmaking Called. Current Phase: {Phase}");
            if(_matchmakingCts != null) {
                _matchmakingCts.Cancel();
                _matchmakingCts.Dispose();
                _matchmakingCts = null;
            }

            // If we are hosting a public lobby (or were searching), revert to private lobby
            if(CurrentLobby.HasValue && IsPartyLeader) {
                // Keep the lobby, just make it private
                CurrentLobby.Value.SetPrivate();
                CurrentLobby.Value.SetData(GameModeKey, "Private");
                // Reset phase to LobbyReady (Private) which hides the status card
                SetFrontStatus(SessionPhase.LobbyReady, "");
            } else if(Phase != SessionPhase.InGame && Phase != SessionPhase.Menu) {
                if(!IsPartyLeader && CurrentLobby.HasValue) {
                    LeaveLobby();
                    CleanupNetworkAsync().Forget();
                    SetFrontStatus(SessionPhase.Menu, "");
                } else {
                    SetFrontStatus(SessionPhase.Menu, "");
                    if(!CurrentLobby.HasValue) {
                        CreatePrivateLobbyAsync().Forget();
                    }
                }
            }
        }

        /// <summary>
        /// Starts a local "private match" without Steam (offline).
        /// This is a host-only loopback session (LAN/multiplayer offline is handled later).
        /// </summary>
        public async UniTask StartOfflinePrivateMatchAsync(string mode) {
            if(string.IsNullOrEmpty(mode)) return;

            // Cancel any in-flight matchmaking without auto-hosting a Steam lobby.
            if(_matchmakingCts != null) {
                _matchmakingCts.Cancel();
                _matchmakingCts.Dispose();
                _matchmakingCts = null;
            }

            // Leave any Steam lobby context (safe even if Steam is offline).
            LeaveLobby();

            // Shut down NGO if it was previously listening.
            await CleanupNetworkAsync();

            ApplyRuntimeMode(mode, "OfflinePrivateMatch");

            SetFrontStatus(SessionPhase.StartingHost, "Starting offline match...");

            // Fade out (keeps UX consistent with Steam private match flow).
            if(SceneTransitionManager.Instance != null) {
                await SceneTransitionManager.Instance.FadeOutAsync();
            } else {
                await UniTask.Delay(300);
            }

            if(_networkManager == null) {
                _networkManager = NetworkManager.Singleton;
            }
            if(_networkManager == null) {
                Debug.LogError("[SessionManager] NetworkManager.Singleton is null. Cannot start offline match.");
                SetFrontStatus(SessionPhase.Error, "Offline networking not configured.");
                return;
            }

            var utp = _networkManager.GetComponent<UnityTransport>();
            if(utp == null) {
                Debug.LogError("[SessionManager] UnityTransport missing on NetworkManager. Cannot start offline match.");
                SetFrontStatus(SessionPhase.Error, "Offline networking not configured.");
                return;
            }

            // Ensure we use UTP loopback.
            utp.SetConnectionData("127.0.0.1", 7777);
            _networkManager.NetworkConfig.NetworkTransport = utp;

            // Start host and load the game scene via NGO scene management.
            ApplyLocalConnectionPayload(true);
            _networkManager.StartHost();
            Phase = SessionPhase.LoadingScene;
            _networkManager.SceneManager.LoadScene(GameSceneName, LoadSceneMode.Single);
        }

        /// <summary>
        /// Joins a specific Steam lobby and synchronizes the session.
        /// </summary>
        /// <param name="lobby">The lobby to join.</param>
        private async UniTask JoinSessionByLobbyAsync(Lobby lobby) {
            SetFrontStatus(SessionPhase.JoiningLobby, "Joining...");

            // Clean up old lobby properly (Migrate host if needed)
            if(CurrentLobby.HasValue && CurrentLobby.Value.Id != lobby.Id) {
                Debug.Log("[SessionManager] Switching lobbies. Leaving current...");
                LeaveLobby();
                // Note: LeaveLobby sets Phase to Menu, but we are about to Join.
                // Reset Phase to JoiningLobby just in case logic checks it.
                Phase = SessionPhase.JoiningLobby;
            }

            var result = await lobby.Join();
            if(result != RoomEnter.Success) {
                SetFrontStatus(SessionPhase.Error, $"Failed to join: {result}");
                return;
            }

            CurrentLobby = lobby;
            FlowLog.Emit(FlowEventIds.PartyLifecycle,
                ("action", "JoinSteamLobby"),
                ("steamLobbyId", lobby.Id),
                ("owner", lobby.Owner.Id),
                ("result", result.ToString()));

            // Join Voice Channel
            if (VoiceManager.Instance != null) {
                VoiceManager.Instance.JoinChannelAsync("match_" + lobby.Id).Forget();
            }

            // Sync Party ID from lobby
            var lobbyPartyId = lobby.GetData(PartyIdKey);
            if(!string.IsNullOrEmpty(lobbyPartyId)) {
                CurrentPartyId = lobbyPartyId;
                // If we joined a lobby with a party ID, we are NOT the global leader (unless it's ours)
                if(lobby.Owner.Id != SteamClient.SteamId) {
                    IsPartyLeader = false;
                }
            } else if(lobby.Owner.Id == SteamClient.SteamId) {
                // We are host of a lobby that has no party ID? (Shouldn't happen with our logic)
                IsPartyLeader = true;
            }

            // Tag ourselves as being in this party
            if(!string.IsNullOrEmpty(CurrentPartyId)) {
                lobby.SetMemberData(PartyIdKey, CurrentPartyId);
            }
            UpdateLocalDisplayNameInLobby();

            // Wait for Host Address to be set
            SetFrontStatus(SessionPhase.StartingClient, "Connecting to Host...");

            // Allow time for Netcode host to start if just promoted
            await UniTask.Delay(500);

            // 4. Get Host Data & Connect
            var hostAddress = lobby.GetData(HostAddressKey);

            // Retry logic if host hasn't set data yet
            var retries = 0;
            while(string.IsNullOrEmpty(hostAddress) && retries < 10) {
                await UniTask.Delay(500);
                hostAddress = lobby.GetData(HostAddressKey);
                retries++;
            }

            if(string.IsNullOrEmpty(hostAddress)) {
                SetFrontStatus(SessionPhase.Error, "Host address not found.");
                LeaveLobby();
                return;
            }

            if(!ulong.TryParse(hostAddress, out ulong steamId)) {
                SetFrontStatus(SessionPhase.Error, "Invalid Host ID.");
                LeaveLobby();
                return;
            }

            // Configure Transport
            var transport = _networkManager.GetComponent<FacepunchTransport>();
            if(transport == null) {
                Debug.LogError("FacepunchTransport missing on NetworkManager!");
                return;
            }

            transport.targetSteamId = steamId;
            _networkManager.NetworkConfig.NetworkTransport = transport;

            Debug.Log($"[SessionManager] Starting Client connecting to {steamId}");
            var isPrivateMatch = lobby.GetData(GameModeKey) == "Private";
            ApplyLocalConnectionPayload(isPrivateMatch);
            _networkManager.StartClient();
        }

        /// <summary>
        /// Attempts to join a session using its Steam Lobby ID.
        /// </summary>
        /// <param name="lobbyId">The ulong ID of the lobby.</param>
        private async UniTask JoinSessionByLobbyIdAsync(ulong lobbyId) {
            var joinedLobby = await SteamMatchmaking.JoinLobbyAsync(lobbyId);
            if(joinedLobby.HasValue) {
                await JoinSessionByLobbyAsync(joinedLobby.Value);
            } else {
                SetFrontStatus(SessionPhase.Error, "Target lobby not found or join failed.");
            }
        }

        /// <summary>
        /// Sets the selected gamemode and updates lobby data if hosting.
        /// </summary>
        /// <param name="mode">The gamemode ID.</param>
        public void SetGamemode(string mode) {
            ApplyRuntimeMode(mode, "MenuSelection", refreshUi: false);
            if(CurrentLobby.HasValue && CurrentLobby.Value.Owner.Id == SteamClient.SteamId) {
                CurrentLobby.Value.SetData("TargetMode", mode);
            }

            if(FrontStatusChanged != null) {
                FrontStatusChanged.Invoke(null);
            }
        }


        /// <summary>
        /// Leaves the current Steam lobby and resets networking state.
        /// </summary>
        public void LeaveLobby() {
            FlowLog.Emit(FlowEventIds.PartyLifecycle,
                ("action", "LeaveLobby"),
                ("partyId", CurrentPartyId),
                ("steamLobbyId", CurrentLobby.HasValue ? CurrentLobby.Value.Id.ToString() : "none"));

            _expectedDisconnect = true;
            if(CurrentLobby.HasValue) {
                if(!_isShuttingDown && SteamClient.IsValid && IsPartyLeader && CurrentLobby.Value.MemberCount > 2) {
                    var currentLobby = CurrentLobby.Value;
                    var newOwner = currentLobby.Members.FirstOrDefault(m => m.Id != SteamClient.SteamId);
                    if(newOwner.Id != 0) {
                        Debug.Log($"[SessionManager] Migrating host to {newOwner.Name} before leaving.");
                        currentLobby.Owner = newOwner;
                    }
                }

                CurrentLobby.Value.Leave();
                CurrentLobby = null;
            }

            Phase = SessionPhase.Menu;
            IsPartyLeader = false;
        }

        /// <summary>
        /// Transitions the local player back to the main menu and handles party reformation.
        /// </summary>
        public async UniTask LeaveToMainMenuAsync() {
            FlowLog.Emit(FlowEventIds.SessionExit,
                ("reason", "LeaveToMainMenu"),
                ("phase", Phase),
                ("gameplay", IsInGameplay));

            // Cancel any active matchmaking first
            ClearMatchmakingState();

            if(Game.Audio2.AudioService.Instance != null) {
                Game.Audio2.AudioService.Instance.StopAll();
            }

            var currentScene = SceneManager.GetActiveScene().name;
            var shouldFade = currentScene != "MainMenu";

            if(shouldFade && SceneTransitionManager.Instance != null)
                await SceneTransitionManager.Instance.FadeOut().ToUniTask();

            LeaveLobby();
            ClearUgsMatchState(); // Clear UGS match lobby state
            await CleanupNetworkAsync();

            if(currentScene != "MainMenu") {
                SceneManager.LoadScene("MainMenu");
                
                // Allow scene load to finish and refresh UI before fading in
                await UniTask.Yield();
            }

            // Recovery: Ensure the screen fades back in if we were stuck in a black screen phase
            if(SceneTransitionManager.Instance != null) {
                await SceneTransitionManager.Instance.FadeInAsync();
            }

            if(currentScene != "MainMenu") {
                if(IsPartyLeader) {
                    Debug.Log("[SessionManager] Returning to menu as Party Leader. Re-hosting party lobby...");
                    CreatePrivateLobbyAsync().Forget();
                } else if(!string.IsNullOrEmpty(CurrentPartyId)) {
                    Debug.Log("[SessionManager] Returning to menu as Party Member. Searching for leader's lobby...");
                    TryRejoinPartyLobby().Forget();
                }
            }
        }

        /// <summary>
        /// Attempts to find and rejoin the party lobby after returning to the main menu.
        /// </summary>
        private async UniTaskVoid TryRejoinPartyLobby() {
            await UniTask.Delay(1000);

            var lobbies = await SteamMatchmaking.LobbyList
                .WithKeyValue(PartyIdKey, CurrentPartyId)
                .RequestAsync();

            if(lobbies != null && lobbies.Length > 0) {
                await JoinSessionByLobbyAsync(lobbies[0]);
            } else {
                Debug.LogWarning("[SessionManager] Failed to find party lobby to rejoin.");
            }
        }

        #endregion

        #region Internal / Networking
        
        private void ApplyLocalConnectionPayload(bool isPrivateMatch) {
            if(_networkManager == null) _networkManager = NetworkManager.Singleton;
            if(_networkManager == null) return;

            var payload = new ConnectionPayload {
                partyId = CurrentPartyId,
                isPrivateMatch = isPrivateMatch,
                steamId = LocalIdentity.GetSteamId(),
                ugsPlayerId = LocalIdentity.GetUgsPlayerId(),
                displayName = LocalIdentity.GetDisplayName()
            };

            _networkManager.NetworkConfig.ConnectionData = ConnectionPayload.Encode(payload);
        }

        /// <summary>
        /// Starts the Netcode host using FacepunchTransport.
        /// </summary>
        private void StartHost() {
            var isPrivateMatch = false;
            if(CurrentLobby.HasValue) {
                isPrivateMatch = CurrentLobby.Value.GetData(GameModeKey) == "Private";
            }
            ApplyLocalConnectionPayload(isPrivateMatch);

            var transport = _networkManager.GetComponent<FacepunchTransport>();
            if(transport != null) {
                _networkManager.NetworkConfig.NetworkTransport = transport;
            } else {
                Debug.LogError(
                    "[SessionManager] FacepunchTransport missing! Falling back to default (UnityTransport), which may cause port conflicts.");
            }

            _networkManager.StartHost();
        }

        /// <summary>
        /// Shuts down the Netcode network manager.
        /// </summary>
        private async UniTask CleanupNetworkAsync() {
            if(_networkManager.IsListening) {
                _networkManager.Shutdown();
            }

            // Wait for shutdown?
            await UniTask.Yield();
        }

        /// <summary>
        /// Clears matchmaker state, cancelling any active ticket.
        /// </summary>
        private void ClearMatchmakingState() {
            Debug.Log("[SessionManager] ClearMatchmakingState called");
            
            // Cancel polling
            if(_matchmakerCts != null) {
                _matchmakerCts.Cancel();
                _matchmakerCts.Dispose();
                _matchmakerCts = null;
            }
            if(_matchmakingCts != null) {
                _matchmakingCts.Cancel();
                _matchmakingCts.Dispose();
                _matchmakingCts = null;
            }

            // Delete ticket from server if we have one
            if(!string.IsNullOrEmpty(_matchmakerTicketId)) {
                DeleteMatchmakerTicketAsync(_matchmakerTicketId).Forget();
                _matchmakerTicketId = null;
            }

            _matchmakerQueueName = null;
        }

        /// <summary>
        /// Clears UGS match lobby state to avoid stale data affecting future matches.
        /// </summary>
        private void ClearUgsMatchState() {
            Debug.Log("[SessionManager] ClearUgsMatchState called");
            
            // Leave match lobby if we're in one
            if(_ugsMatchLobby != null) {
                LeaveUgsMatchLobbyAsync().Forget();
            }
            _ugsMatchLobby = null;
            _ugsSyncInProgress = false;
            _ugsLocalReadySubmitted = false;
            _ugsClientStartedForMatch = false;
            _ugsHostPreFadedOut = false;
        }

        private async UniTaskVoid LeaveUgsMatchLobbyAsync() {
            if(_ugsMatchLobby == null) return;
            try {
                var localId = AuthenticationService.Instance.PlayerId;
                if(!string.IsNullOrEmpty(localId)) {
                    await LobbyService.Instance.RemovePlayerAsync(_ugsMatchLobby.Id, localId);
                    Debug.Log($"[SessionManager] Left UGS match lobby '{_ugsMatchLobby.Id}'");
                }
            } catch(Exception ex) {
                Debug.LogWarning($"[SessionManager] Failed to leave UGS match lobby: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the session phase and triggers status change events.
        /// </summary>
        /// <param name="phase">The new session phase.</param>
        /// <param name="message">The status message to display.</param>
        private void SetFrontStatus(SessionPhase phase, string message) {
            Phase = phase;
            if(FrontStatusChanged != null) {
                FrontStatusChanged.Invoke(message);
            }
        }

        private void RegisterNetworkCallbacks() {
            if(_networkManager == null) _networkManager = NetworkManager.Singleton;
            if(_networkManager == null) return;
            _networkManager.OnClientConnectedCallback += OnClientConnected;
            _networkManager.OnClientDisconnectCallback += OnClientDisconnected;
        }

        private void UnregisterNetworkCallbacks() {
            if(_networkManager == null) return;
            _networkManager.OnClientConnectedCallback -= OnClientConnected;
            _networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
            var activeScene = SceneManager.GetActiveScene();
            if(activeScene.IsValid()) {
            }

            if(scene.name == GameSceneName) {
                OnGameSceneLoaded();
            }
        }

        private void OnGameSceneLoaded() {
            if(TryGetAuthoritativeRuntimeMode(out var mode, out var source)) {
                if(string.Equals(SelectedGameMode, mode, StringComparison.OrdinalIgnoreCase) == false) {
                    FlowLog.Emit(FlowEventIds.AnomalyModeMismatch,
                        ("selected", SelectedGameMode),
                        ("applied", mode),
                        ("objective", "Unknown"));
                }
                ApplyRuntimeMode(mode, $"SceneLoaded/{source}", refreshUi: false);
                FlowLog.Emit(FlowEventIds.SceneLoaded,
                    ("mode", mode),
                    ("source", source));
            } else {
                Debug.LogWarning("[SessionManager] Game scene loaded without an authoritative mode. Keeping current mode.");
                FlowLog.Emit(FlowEventIds.SceneLoaded,
                    ("mode", SelectedGameMode),
                    ("source", "FallbackSelected"));
            }

            if(_networkManager.IsServer) {
                _clientsFinishedLoading.Clear();
                // We are server, we are ready.
                // Wait for clients?
                // Logic passed to CustomNetworkManager spawning.
                IsInGameplay = true;
                if(_customNetworkManager != null) {
                    _customNetworkManager.EnableGameplaySpawningAndSpawnAll();
                }
            }

            // Fade In
            if(SceneTransitionManager.Instance != null)
                SceneTransitionManager.Instance.FadeIn().ToUniTask().Forget();
        }

        private void OnClientConnected(ulong clientId) {
            // Handle connection
            if(!_networkManager.IsServer) return;
            NotifyPartyStateChanged();
            // Maybe check if we need to load game scene for them
            if(IsInGameplay) {
                // Sync scene
            }
        }

        private void OnClientDisconnected(ulong clientId) {
            if(clientId == _networkManager.LocalClientId) {
                // We disconnected
                if(!_expectedDisconnect) {
                    Debug.Log("[SessionManager] Unexpected Disconnect (Kick or Error).");
                    HandleUnexpectedDisconnect().Forget();
                } else {
                    // Reset flag
                    _expectedDisconnect = false;
                }
            }

            NotifyPartyStateChanged();
        }

        /// <summary>
        /// Handles cleanup and recovery after an unexpected network disconnect.
        /// </summary>
        private async UniTaskVoid HandleUnexpectedDisconnect() {
            FlowLog.Emit(FlowEventIds.SessionExit,
                ("reason", "UnexpectedDisconnect"),
                ("phase", Phase),
                ("gameplay", IsInGameplay));
            SetFrontStatus(SessionPhase.Error, "Disconnected from party.");

            var currentScene = SceneManager.GetActiveScene().name;
            if(currentScene != "MainMenu") {
                await LeaveToMainMenuAsync();
            } else {
                LeaveLobby();
                await CleanupNetworkAsync();

                Debug.Log("[SessionManager] Creating Personal Lobby (Self-Healing)...");
                await CreatePrivateLobbyAsync();
            }
        }

        #region UGS Lobby (Party + Match)

        private void Update() {
            if(_ugsPartyLobby == null && _ugsMatchLobby == null) return;

            // Global Watchdog: If we are stuck in a black screen phase too long, abort.
            if (Phase == SessionPhase.SynchronizingLoad) {
                if (Time.time - _phaseStartTime > 30f) {
                    Debug.LogError("[SessionManager] Stuck in SynchronizingLoad for >30s. Aborting to menu...");
                    FlowLog.Emit(FlowEventIds.AnomalySessionStuck,
                        ("phase", Phase),
                        ("elapsed", Time.time - _phaseStartTime));
                    LeaveToMainMenuAsync().Forget();
                    return;
                }
            }

            if(Time.unscaledTime >= _nextUgsHeartbeatTime) {
                _nextUgsHeartbeatTime = Time.unscaledTime + UgsHeartbeatIntervalSeconds;
                SendUgsHeartbeatsAsync().Forget();
            }

            if(Time.unscaledTime >= _nextUgsPollTime) {
                _nextUgsPollTime = Time.unscaledTime + UgsPollIntervalSeconds;
                PollUgsPartyLobbyAsync().Forget();
                PollUgsMatchLobbyAsync().Forget();
            }
        }

        public async UniTask CreateUgsPartyLobbyAsync(int maxPlayers, bool isPrivate) {
            await UgsAuthService.InitializeAndSignInAsync();

            if(string.IsNullOrEmpty(CurrentPartyId)) {
                CurrentPartyId = Guid.NewGuid().ToString();
            }

            var options = new CreateLobbyOptions();
            options.IsPrivate = isPrivate;
            options.Player = BuildUgsLobbyPlayer();
            options.Data = new Dictionary<string, Unity.Services.Lobbies.Models.DataObject>();
            options.Data[UgsPartyIdKey] = new Unity.Services.Lobbies.Models.DataObject(
                Unity.Services.Lobbies.Models.DataObject.VisibilityOptions.Member, CurrentPartyId);
            options.Data[UgsFollowMatchLobbyIdKey] = new Unity.Services.Lobbies.Models.DataObject(
                Unity.Services.Lobbies.Models.DataObject.VisibilityOptions.Member, "");
            options.Data[UgsLobbyStateKey] = new Unity.Services.Lobbies.Models.DataObject(
                Unity.Services.Lobbies.Models.DataObject.VisibilityOptions.Member, "Party");

            _ugsPartyLobby = await LobbyService.Instance.CreateLobbyAsync("HOP Party", maxPlayers, options);
            _ugsMatchLobby = null;
            _nextUgsHeartbeatTime = Time.unscaledTime + 1f;
            _nextUgsPollTime = Time.unscaledTime + 1f;
            UpdateSteamRichPresenceForUgs();
            FlowLog.Emit(FlowEventIds.PartyLifecycle,
                ("action", "CreateUgsParty"),
                ("partyId", CurrentPartyId),
                ("lobbyId", _ugsPartyLobby != null ? _ugsPartyLobby.Id : "null"),
                ("private", isPrivate),
                ("maxPlayers", maxPlayers));
        }

        public async UniTask JoinUgsPartyLobbyByCodeAsync(string code) {
            await UgsAuthService.InitializeAndSignInAsync();
            if(string.IsNullOrEmpty(code)) return;

            var options = new JoinLobbyByCodeOptions();
            options.Player = BuildUgsLobbyPlayer();

            _ugsPartyLobby = await LobbyService.Instance.JoinLobbyByCodeAsync(code, options);
            _ugsMatchLobby = null;

            if(_ugsPartyLobby != null && _ugsPartyLobby.Data != null) {
                if(_ugsPartyLobby.Data.TryGetValue(UgsPartyIdKey, out var partyIdObj)) {
                    if(partyIdObj != null && !string.IsNullOrEmpty(partyIdObj.Value)) {
                        CurrentPartyId = partyIdObj.Value;
                    }
                }
            }

            _nextUgsHeartbeatTime = Time.unscaledTime + 1f;
            _nextUgsPollTime = Time.unscaledTime + 1f;
            UpdateSteamRichPresenceForUgs();
            FlowLog.Emit(FlowEventIds.PartyLifecycle,
                ("action", "JoinUgsParty"),
                ("code", code),
                ("partyId", CurrentPartyId),
                ("lobbyId", _ugsPartyLobby != null ? _ugsPartyLobby.Id : "null"));
        }

        public async UniTask StartUgsPrivateMatchAsync(string mode, int maxPlayers) {
            await UgsAuthService.InitializeAndSignInAsync();
            if(_ugsPartyLobby == null) return;

            var localUgsId = AuthenticationService.Instance.PlayerId;
            if(string.IsNullOrEmpty(localUgsId)) return;

            if(string.IsNullOrEmpty(mode)) return;

            ApplyRuntimeMode(mode, "UgsPrivateMatchHost");
            FlowLog.Emit(FlowEventIds.QueueStarted,
                ("mode", mode),
                ("queue", "PrivateParty"),
                ("maxPlayers", maxPlayers));

            // Immediate feedback for the host: start fading out right away.
            // We'll avoid double-fading later when we mark ourselves ready.
            _ugsHostPreFadedOut = false;
            if(SceneTransitionManager.Instance != null) {
                _ugsHostPreFadedOut = true;
                Phase = SessionPhase.SynchronizingLoad;
                SetFrontStatus(SessionPhase.SynchronizingLoad, "Waiting for party...");
                await SceneTransitionManager.Instance.FadeOutAsync();
            }

            if(string.IsNullOrEmpty(CurrentPartyId)) {
                if(_ugsPartyLobby.Data != null && _ugsPartyLobby.Data.TryGetValue(UgsPartyIdKey, out var partyIdObj)) {
                    if(partyIdObj != null) CurrentPartyId = partyIdObj.Value;
                }
            }

            var expectedPlayers = new List<string>();
            if(_ugsPartyLobby.Players != null && _ugsPartyLobby.Players.Count > 0) {
                for(var i = 0; i < _ugsPartyLobby.Players.Count; i++) {
                    var p = _ugsPartyLobby.Players[i];
                    if(p == null) continue;
                    if(string.IsNullOrEmpty(p.Id)) continue;
                    expectedPlayers.Add(p.Id);
                }
            }
            if(expectedPlayers.Count == 0) {
                expectedPlayers.Add(localUgsId);
            }
            var expectedCsv = string.Join(",", expectedPlayers);

            // Create relay allocation for host.
            var alloc = await RelayService.Instance.CreateAllocationAsync(maxPlayers - 1);
            var joinCode = await RelayService.Instance.GetJoinCodeAsync(alloc.AllocationId);

            // Create match lobby.
            var create = new CreateLobbyOptions();
            create.IsPrivate = true;
            create.Player = BuildUgsLobbyPlayer();
            create.Data = new Dictionary<string, Unity.Services.Lobbies.Models.DataObject>();
            create.Data[UgsPartyIdKey] = new Unity.Services.Lobbies.Models.DataObject(
                Unity.Services.Lobbies.Models.DataObject.VisibilityOptions.Member, CurrentPartyId);
            create.Data[UgsMatchTypeKey] = new Unity.Services.Lobbies.Models.DataObject(
                Unity.Services.Lobbies.Models.DataObject.VisibilityOptions.Member, "Private");
            create.Data[UgsTargetModeKey] = new Unity.Services.Lobbies.Models.DataObject(
                Unity.Services.Lobbies.Models.DataObject.VisibilityOptions.Member, mode);
            create.Data[UgsRelayJoinCodeKey] = new Unity.Services.Lobbies.Models.DataObject(
                Unity.Services.Lobbies.Models.DataObject.VisibilityOptions.Member, joinCode);
            create.Data[UgsLobbyStateKey] = new Unity.Services.Lobbies.Models.DataObject(
                Unity.Services.Lobbies.Models.DataObject.VisibilityOptions.Member, "SynchronizingLoad");
            create.Data[UgsExpectedPlayersKey] = new Unity.Services.Lobbies.Models.DataObject(
                Unity.Services.Lobbies.Models.DataObject.VisibilityOptions.Member, expectedCsv);

            _ugsMatchLobby = await LobbyService.Instance.CreateLobbyAsync("HOP Match", maxPlayers, create);

            // Tell party members to follow into the match lobby.
            var update = new UpdateLobbyOptions();
            update.Data = new Dictionary<string, Unity.Services.Lobbies.Models.DataObject>();
            update.Data[UgsFollowMatchLobbyIdKey] = new Unity.Services.Lobbies.Models.DataObject(
                Unity.Services.Lobbies.Models.DataObject.VisibilityOptions.Member, _ugsMatchLobby.Id);
            update.Data[UgsLobbyStateKey] = new Unity.Services.Lobbies.Models.DataObject(
                Unity.Services.Lobbies.Models.DataObject.VisibilityOptions.Member, "InMatch");
            _ugsPartyLobby = await LobbyService.Instance.UpdateLobbyAsync(_ugsPartyLobby.Id, update);
            UpdateSteamRichPresenceForUgs();

            _ugsSyncInProgress = false;
            _ugsLocalReadySubmitted = false;
            _ugsClientStartedForMatch = false;
            // Keep _ugsHostPreFadedOut as-is so we can skip the second fade in HandleUgsMatchSynchronizationStartAsync.

            // Fade out and mark ourselves ready.
            await HandleUgsMatchSynchronizationStartAsync();

            // Host waits until all expected party members are ready (or are not present).
            var syncStartTime = Time.time;
            const float syncTimeout = 20f;
            while(true) {
                if(Time.time - syncStartTime > syncTimeout) {
                    Debug.LogWarning("[SessionManager] Private match sync timed out! Aborting to menu...");
                    LeaveToMainMenuAsync().Forget();
                    return;
                }

                try {
                    var refreshed = await LobbyService.Instance.GetLobbyAsync(_ugsMatchLobby.Id);
                    if(refreshed != null) _ugsMatchLobby = refreshed;

                    if(AreAllExpectedPlayersReady(_ugsMatchLobby, expectedPlayers)) {
                        break;
                    }
                } catch(LobbyServiceException ex) when (ex.Reason == LobbyExceptionReason.RateLimited) {
                    Debug.LogWarning("[SessionManager] Rate limited during sync. Retrying...");
                } catch(Exception ex) {
                    Debug.LogError($"[SessionManager] Error during sync: {ex.Message}. Aborting...");
                    LeaveToMainMenuAsync().Forget();
                    return;
                }

                await UniTask.Delay(500);
            }

            // Signal clients to connect.
            try {
                var opts = new UpdateLobbyOptions();
                opts.Data = new Dictionary<string, Unity.Services.Lobbies.Models.DataObject>();
                opts.Data[UgsLobbyStateKey] = new Unity.Services.Lobbies.Models.DataObject(
                    Unity.Services.Lobbies.Models.DataObject.VisibilityOptions.Member, "LoadingScene");
                _ugsMatchLobby = await LobbyService.Instance.UpdateLobbyAsync(_ugsMatchLobby.Id, opts);
            } catch {
                // If this fails transiently, clients will still poll and we can retry next tick.
            }

            await CleanupNetworkAsync();

            // Configure UTP Relay and start host.
            if(_networkManager == null) _networkManager = NetworkManager.Singleton;
            if(_networkManager == null) return;

            var utp = _networkManager.GetComponent<UnityTransport>();
            if(utp == null) {
                Debug.LogError("[SessionManager] UnityTransport missing on NetworkManager. Cannot start UGS relay match.");
                return;
            }

            if(TryApplyRelayToTransport(utp, alloc, null) == false) return;
            _networkManager.NetworkConfig.NetworkTransport = utp;

            ApplyLocalConnectionPayload(true);
            _networkManager.StartHost();
            Phase = SessionPhase.LoadingScene;
            _networkManager.SceneManager.LoadScene(GameSceneName, LoadSceneMode.Single);
        }

        private async UniTask JoinUgsMatchLobbyByIdAsync(string lobbyId) {
            await UgsAuthService.InitializeAndSignInAsync();
            if(string.IsNullOrEmpty(lobbyId)) return;

            Debug.Log($"[SessionManager] JoinUgsMatchLobbyByIdAsync called with lobbyId='{lobbyId}'");

            var options = new JoinLobbyByIdOptions();
            options.Player = BuildUgsLobbyPlayer();

            var matchLobby = await LobbyService.Instance.JoinLobbyByIdAsync(lobbyId, options);
            if(matchLobby == null) {
                Debug.LogError("[SessionManager] Failed to join lobby - matchLobby is null");
                return;
            }
            _ugsMatchLobby = matchLobby;
            UpdateSteamRichPresenceForUgs();
            Debug.Log($"[SessionManager] Successfully joined UGS lobby. hostId='{matchLobby.HostId}', playerCount={matchLobby.Players.Count}");
            FlowLog.Emit(FlowEventIds.PartyLifecycle,
                ("action", "JoinUgsMatchLobby"),
                ("lobbyId", matchLobby.Id),
                ("hostId", matchLobby.HostId),
                ("players", matchLobby.Players != null ? matchLobby.Players.Count : 0));

            // Refresh selected mode immediately so the Game scene loads correctly.
            TrySyncModeFromUgsMatchLobby(_ugsMatchLobby);

            // If the lobby is already in sync/load state, begin local sync now.
            if(_ugsMatchLobby != null && _ugsMatchLobby.Data != null) {
                if(_ugsMatchLobby.Data.TryGetValue(UgsLobbyStateKey, out var stateObj)) {
                    Debug.Log($"[SessionManager] Lobby state on join: '{stateObj?.Value}'");
                    if(stateObj != null && stateObj.Value == "SynchronizingLoad") {
                        HandleUgsMatchSynchronizationStartAsync().Forget();
                        return;
                    }
                }
            }

            // Start polling for lobby state changes - host may update state after we join
            Debug.Log("[SessionManager] Starting lobby state polling for non-host client...");
            StartUgsMatchLobbyPollingAsync().Forget();
        }

        private async UniTaskVoid StartUgsMatchLobbyPollingAsync() {
            // Poll until we either connect or timeout
            for(int i = 0; i < 60; i++) {
                await UniTask.Delay(1000);
                if(_ugsMatchLobby == null) break;
                if(Phase == SessionPhase.InGame) break;
                if(_ugsClientStartedForMatch) break;
                
                PollUgsMatchLobbyAsync().Forget();
            }
        }

        private void TrySyncModeFromUgsMatchLobby(Unity.Services.Lobbies.Models.Lobby lobby) {
            if(lobby == null) return;
            if(lobby.Data == null) return;
            if(!lobby.Data.TryGetValue(UgsTargetModeKey, out var modeObj)) return;
            if(modeObj == null) return;
            if(string.IsNullOrEmpty(modeObj.Value)) return;
            ApplyRuntimeMode(modeObj.Value, "UgsMatchLobbySync", refreshUi: false);
        }

        private async UniTaskVoid PollUgsMatchLobbyAsync() {
            if(_ugsMatchLobby == null) return;
            if(Phase == SessionPhase.InGame) return;

            try {
                var refreshed = await LobbyService.Instance.GetLobbyAsync(_ugsMatchLobby.Id);
                if(refreshed != null) _ugsMatchLobby = refreshed;
            } catch {
                return;
            }

            if(_ugsMatchLobby == null) return;
            TrySyncModeFromUgsMatchLobby(_ugsMatchLobby);

            if(_ugsMatchLobby.Data == null) return;
            if(!_ugsMatchLobby.Data.TryGetValue(UgsLobbyStateKey, out var stateObj)) return;
            if(stateObj == null) return;

            if(stateObj.Value == "SynchronizingLoad") {
                if(_ugsLocalReadySubmitted == false) {
                    HandleUgsMatchSynchronizationStartAsync().Forget();
                }
                return;
            }

            if(stateObj.Value == "LoadingScene") {
                // The lobby host will start the Netcode host; they should NOT also start as a relay client.
                var localUgsId = AuthenticationService.Instance.PlayerId;
                if(!string.IsNullOrEmpty(localUgsId) && _ugsMatchLobby.HostId == localUgsId) {
                    return;
                }

                if(_ugsClientStartedForMatch == false) {
                    StartUgsMatchClientAsync().Forget();
                }
            }
        }

        private async UniTask HandleUgsMatchSynchronizationStartAsync() {
            if(_ugsMatchLobby == null) return;
            if(_ugsLocalReadySubmitted) return;
            if(_ugsSyncInProgress) return;

            _ugsSyncInProgress = true;
            Phase = SessionPhase.SynchronizingLoad;
            SetFrontStatus(SessionPhase.SynchronizingLoad, "Waiting for party...");

            // Fade out via SceneTransitionManager (matches Steam sync UX).
            if(_ugsHostPreFadedOut) {
                _ugsHostPreFadedOut = false;
            } else if(SceneTransitionManager.Instance != null) {
                await SceneTransitionManager.Instance.FadeOutAsync();
            } else {
                await UniTask.Delay(500);
            }

            var localUgsId = AuthenticationService.Instance.PlayerId;
            if(string.IsNullOrEmpty(localUgsId)) {
                _ugsSyncInProgress = false;
                return;
            }

            try {
                var opts = new UpdatePlayerOptions();
                opts.Data = new Dictionary<string, Unity.Services.Lobbies.Models.PlayerDataObject>();
                opts.Data[UgsMemberReadyKey] = new Unity.Services.Lobbies.Models.PlayerDataObject(
                    Unity.Services.Lobbies.Models.PlayerDataObject.VisibilityOptions.Member, "1");

                _ugsMatchLobby = await LobbyService.Instance.UpdatePlayerAsync(_ugsMatchLobby.Id, localUgsId, opts);
                _ugsLocalReadySubmitted = true;
            } catch(LobbyServiceException ex) when (ex.Reason == LobbyExceptionReason.RateLimited) {
                Debug.LogWarning("[SessionManager] Rate limited updating ready state. Polling will retry.");
            } catch(Exception ex) {
                Debug.LogError($"[SessionManager] Failed to update ready state: {ex.Message}. Aborting to menu...");
                LeaveToMainMenuAsync().Forget();
            } finally {
                _ugsSyncInProgress = false;
            }
        }

        private bool AreAllExpectedPlayersReady(Unity.Services.Lobbies.Models.Lobby lobby, List<string> expectedPlayerIds) {
            if(lobby == null) return false;
            if(expectedPlayerIds == null) return true;
            if(expectedPlayerIds.Count == 0) return true;
            if(lobby.Players == null) return false;

            for(var i = 0; i < expectedPlayerIds.Count; i++) {
                var id = expectedPlayerIds[i];
                if(string.IsNullOrEmpty(id)) continue;

                Unity.Services.Lobbies.Models.Player found = null;
                for(var j = 0; j < lobby.Players.Count; j++) {
                    var p = lobby.Players[j];
                    if(p == null) continue;
                    if(p.Id == id) {
                        found = p;
                        break;
                    }
                }

                if(found == null) return false;
                if(found.Data == null) return false;
                if(!found.Data.TryGetValue(UgsMemberReadyKey, out var readyObj)) return false;
                if(readyObj == null) return false;
                if(readyObj.Value != "1") return false;
            }

            return true;
        }

        private async UniTaskVoid StartUgsMatchClientAsync() {
            if(_ugsMatchLobby == null) return;
            if(_ugsClientStartedForMatch) return;
            if(_ugsLocalReadySubmitted == false) return;

            if(_ugsMatchLobby.Data == null) return;
            if(!_ugsMatchLobby.Data.TryGetValue(UgsRelayJoinCodeKey, out var joinCodeObj)) return;
            if(joinCodeObj == null) return;
            var joinCode = joinCodeObj.Value;
            if(string.IsNullOrEmpty(joinCode)) return;

            TrySyncModeFromUgsMatchLobby(_ugsMatchLobby);

            _ugsClientStartedForMatch = true;
            Phase = SessionPhase.StartingClient;

            await CleanupNetworkAsync();

            if(_networkManager == null) _networkManager = NetworkManager.Singleton;
            if(_networkManager == null) return;

            var utp = _networkManager.GetComponent<UnityTransport>();
            if(utp == null) {
                Debug.LogError("[SessionManager] UnityTransport missing on NetworkManager. Cannot join UGS relay match.");
                return;
            }

            var joinAlloc = await RelayService.Instance.JoinAllocationAsync(joinCode);
            if(TryApplyRelayToTransport(utp, null, joinAlloc) == false) return;
            _networkManager.NetworkConfig.NetworkTransport = utp;

            ApplyLocalConnectionPayload(true);
            _networkManager.StartClient();
        }

        private Unity.Services.Lobbies.Models.Player BuildUgsLobbyPlayer() {
            var pid = AuthenticationService.Instance.PlayerId;
            var data = new Dictionary<string, Unity.Services.Lobbies.Models.PlayerDataObject>();
            data["displayName"] = new Unity.Services.Lobbies.Models.PlayerDataObject(
                Unity.Services.Lobbies.Models.PlayerDataObject.VisibilityOptions.Member, LocalIdentity.GetDisplayName());
            var steamId = LocalIdentity.GetSteamId();
            if(steamId != 0) {
                data["steamId"] = new Unity.Services.Lobbies.Models.PlayerDataObject(
                    Unity.Services.Lobbies.Models.PlayerDataObject.VisibilityOptions.Member, steamId.ToString());
            }
            return new Unity.Services.Lobbies.Models.Player(pid, data: data);
        }

        private static bool TryPickRelayEndpoint(List<Unity.Services.Relay.Models.RelayServerEndpoint> endpoints, string connectionType, out string host, out ushort port, out bool isSecure) {
            host = "";
            port = 0;
            isSecure = false;

            if(endpoints == null) return false;
            if(endpoints.Count == 0) return false;
            if(string.IsNullOrEmpty(connectionType)) return false;

            for(var i = 0; i < endpoints.Count; i++) {
                var ep = endpoints[i];
                if(ep.ConnectionType != connectionType) continue;
                host = ep.Host;
                port = (ushort)ep.Port;
                isSecure = ep.Secure;
                if(string.IsNullOrEmpty(host)) return false;
                if(port == 0) return false;
                return true;
            }

            return false;
        }

        private static bool TryApplyRelayToTransport(UnityTransport utp, Unity.Services.Relay.Models.Allocation hostAlloc, Unity.Services.Relay.Models.JoinAllocation clientAlloc) {
            if(utp == null) return false;

            const string connectionType = "dtls";

            if(hostAlloc == null && clientAlloc == null) return false;
            if(hostAlloc != null && clientAlloc != null) return false;

            string host;
            ushort port;
            bool isSecure;

            if(hostAlloc != null) {
                if(TryPickRelayEndpoint(hostAlloc.ServerEndpoints, connectionType, out host, out port, out isSecure) == false) {
                    Debug.LogError("[SessionManager] Relay allocation missing a DTLS endpoint.");
                    return false;
                }
                utp.UseWebSockets = false;
                utp.SetRelayServerData(host, port, hostAlloc.AllocationIdBytes, hostAlloc.Key, hostAlloc.ConnectionData, null, isSecure);
                return true;
            }

            if(TryPickRelayEndpoint(clientAlloc.ServerEndpoints, connectionType, out host, out port, out isSecure) == false) {
                Debug.LogError("[SessionManager] Relay join allocation missing a DTLS endpoint.");
                return false;
            }
            utp.UseWebSockets = false;
            utp.SetRelayServerData(host, port, clientAlloc.AllocationIdBytes, clientAlloc.Key, clientAlloc.ConnectionData, clientAlloc.HostConnectionData, isSecure);
            return true;
        }

        private void UpdateSteamRichPresenceForUgs() {
            if(!SteamClient.IsValid || !SteamClient.IsLoggedOn) return;

            if(_ugsPartyLobby != null) {
                var code = _ugsPartyLobby.LobbyCode;
                if(!string.IsNullOrEmpty(code)) {
                    SteamFriends.SetRichPresence("connect", "UGS_PARTY_CODE:" + code);
                    SteamFriends.SetRichPresence("status", "In Party");
                    return;
                }
            }

            if(_ugsMatchLobby != null) {
                var lobbyId = _ugsMatchLobby.Id;
                if(!string.IsNullOrEmpty(lobbyId)) {
                    SteamFriends.SetRichPresence("connect", "UGS_MATCH_ID:" + lobbyId);
                    SteamFriends.SetRichPresence("status", "In Match");
                    return;
                }
            }

            SteamFriends.ClearRichPresence();
        }

        private async UniTaskVoid SendUgsHeartbeatsAsync() {
            // Heartbeat only required for lobbies we host.
            var localId = AuthenticationService.Instance.PlayerId;
            if(string.IsNullOrEmpty(localId)) return;

            try {
                if(_ugsPartyLobby != null && _ugsPartyLobby.HostId == localId) {
                    await LobbyService.Instance.SendHeartbeatPingAsync(_ugsPartyLobby.Id);
                }
                if(_ugsMatchLobby != null && _ugsMatchLobby.HostId == localId) {
                    await LobbyService.Instance.SendHeartbeatPingAsync(_ugsMatchLobby.Id);
                }
            } catch {
                // Ignore transient heartbeat failures.
            }
        }

        private async UniTaskVoid PollUgsPartyLobbyAsync() {
            if(_ugsPartyLobby == null) return;
            if(Phase == SessionPhase.InGame) return;

            try {
                var refreshed = await LobbyService.Instance.GetLobbyAsync(_ugsPartyLobby.Id);
                if(refreshed != null) _ugsPartyLobby = refreshed;
            } catch {
                return;
            }

            if(_ugsPartyLobby == null) return;
            if(_ugsPartyLobby.Data == null) return;

            if(_ugsPartyLobby.Data.TryGetValue(UgsFollowMatchLobbyIdKey, out var followObj)) {
                if(followObj != null && !string.IsNullOrEmpty(followObj.Value)) {
                    // Join match lobby if we are not already in it.
                    if(_ugsMatchLobby == null || _ugsMatchLobby.Id != followObj.Value) {
                        await JoinUgsMatchLobbyByIdAsync(followObj.Value);
                    }
                }
            }
        }

        #endregion

        #region UGS Matchmaker (Ticketing)

        public void CancelUgsMatchmaking() {
            if(_matchmakerCts != null) {
                _matchmakerCts.Cancel();
                _matchmakerCts.Dispose();
                _matchmakerCts = null;
            }

            if(!string.IsNullOrEmpty(_matchmakerTicketId)) {
                DeleteMatchmakerTicketAsync(_matchmakerTicketId).Forget();
            }

            _matchmakerTicketId = null;
            _matchmakerQueueName = null;
            if(Phase != SessionPhase.InGame) {
                SetFrontStatus(SessionPhase.Menu, "");
            }
        }

        private async UniTaskVoid DeleteMatchmakerTicketAsync(string ticketId) {
            if(string.IsNullOrEmpty(ticketId)) return;
            try {
                await MatchmakerService.Instance.DeleteTicketAsync(ticketId);
            } catch {
                // Ignore transient failures; ticket will expire server-side.
            }
        }

        public async UniTask StartMatchmakerQuickPlayAsync(string mode) {
            await UgsAuthService.InitializeAndSignInAsync();
            CancelUgsMatchmaking();

            if(string.IsNullOrEmpty(mode)) {
                mode = SelectedGameMode;
            } else {
                ApplyRuntimeMode(mode, "UgsQuickPlayRequest");
            }

            var def = Game.Match.MatchSettingsManager.Instance != null
                ? Game.Match.MatchSettingsManager.Instance.GetGamemodeDef(mode)
                : default;

            var maxPlayers = 10;
            if(def.maxPlayers > 0) maxPlayers = def.maxPlayers;

            _matchmakerQueueName = GetQueueNameForMode(mode);
            if(string.IsNullOrEmpty(_matchmakerQueueName)) {
                Debug.LogError("[SessionManager] Matchmaker queue name is empty.");
                return;
            }

            FlowLog.Emit(FlowEventIds.QueueStarted,
                ("mode", mode),
                ("queue", _matchmakerQueueName),
                ("maxPlayers", maxPlayers));

            SetFrontStatus(SessionPhase.Searching, $"Searching for {mode}...");
            MatchmakingStartTime = Time.time;

            var attrs = new Dictionary<string, object> {
                ["modeId"] = mode,
                ["partySize"] = 1
            };

            var players = new List<Player>();
            var localPlayerId = AuthenticationService.Instance.PlayerId;
            if(string.IsNullOrEmpty(localPlayerId)) return;
            players.Add(new Player(localPlayerId, attrs));

            if(Debug.isDebugBuild) {
                Debug.Log($"[UGS Matchmaker] Creating ticket. mode='{mode}' queue='{_matchmakerQueueName}' playerId='{localPlayerId}'");
            }

            CreateTicketResponse resp;
            try {
                var options = new CreateTicketOptions(_matchmakerQueueName, attrs);
                resp = await MatchmakerService.Instance.CreateTicketAsync(players, options);
            } catch(Exception e) {
                Debug.LogError($"[UGS Matchmaker] CreateTicketAsync failed: {e.Message}");
                CancelUgsMatchmaking();
                return;
            }

            _matchmakerTicketId = resp != null ? resp.Id : null;
            if(string.IsNullOrEmpty(_matchmakerTicketId)) {
                Debug.LogError("[SessionManager] Matchmaker ticket id is empty.");
                return;
            }

            _matchmakerCts = new CancellationTokenSource();
            try {
                if(Debug.isDebugBuild) {
                    Debug.Log($"[UGS Matchmaker] Ticket created: '{_matchmakerTicketId}'");
                }
                await PollMatchmakerTicketAsync(mode, maxPlayers, _matchmakerCts.Token);
            } catch(OperationCanceledException) {
                // Expected when user cancels matchmaking.
            }
        }

        private async UniTask PollMatchmakerTicketAsync(string mode, int maxPlayers, CancellationToken ct) {
            while(ct.IsCancellationRequested == false) {
                TicketStatusResponse status;
                try {
                    status = await MatchmakerService.Instance.GetTicketAsync(_matchmakerTicketId);
                } catch(Exception e) {
                    Debug.LogWarning($"[SessionManager] Matchmaker poll failed: {e.Message}");
                    try {
                        await UniTask.Delay(6000, cancellationToken: ct); // 6s Backoff for errors
                    } catch(OperationCanceledException) {
                        return;
                    }
                    continue;
                }

                if(status == null) {
                    try {
                        await UniTask.Delay(6000, cancellationToken: ct); // 6s Backoff for null status
                    } catch(OperationCanceledException) {
                        return;
                    }
                    continue;
                }

                if(status.Type == typeof(MatchIdAssignment)) {
                    var assign = status.Value as MatchIdAssignment;
                    if(assign == null) {
                        Debug.LogError("[SessionManager] Matchmaker returned MatchIdAssignment but value was null.");
                        CancelUgsMatchmaking();
                        return;
                    }

                    switch(assign.Status) {
                        case MatchIdAssignment.StatusOptions.InProgress: {
                            if(Debug.isDebugBuild) {
                                Debug.Log($"[UGS Matchmaker] Ticket '{_matchmakerTicketId}' in progress...");
                            }
                            try {
                                await UniTask.Delay(6000, cancellationToken: ct); // 6s Poll Interval
                            } catch(OperationCanceledException) {
                                return;
                            }
                            continue;
                        }
                        case MatchIdAssignment.StatusOptions.Timeout:
                            Debug.LogWarning("[SessionManager] Matchmaking timed out.");
                            CancelUgsMatchmaking();
                            return;
                        case MatchIdAssignment.StatusOptions.Failed:
                            Debug.LogWarning($"[SessionManager] Matchmaking failed. Message: {assign.Message}");
                            CancelUgsMatchmaking();
                            return;
                        case MatchIdAssignment.StatusOptions.Found: {
                            if(Debug.isDebugBuild) {
                                Debug.Log($"[UGS Matchmaker] Ticket '{_matchmakerTicketId}' found matchId='{assign.MatchId}'");
                            }
                            if(string.IsNullOrEmpty(assign.MatchId)) {
                                Debug.LogError("[SessionManager] Matchmaking found but matchId is empty.");
                                CancelUgsMatchmaking();
                                return;
                            }

                            FlowLog.Emit(FlowEventIds.QueueAssigned,
                                ("queue", _matchmakerQueueName),
                                ("mode", mode),
                                ("matchId", assign.MatchId));

                            StoredMatchmakingResults results;
                            try {
                                results = await MatchmakerService.Instance.GetMatchmakingResultsAsync(assign.MatchId);
                            } catch(Exception e) {
                                Debug.LogError($"[SessionManager] Failed to fetch matchmaking results. Exception: {e.Message}");
                                CancelUgsMatchmaking();
                                return;
                            }

                            await HandleStoredMatchmakerResultsAsync(mode, maxPlayers, assign.MatchId, results);
                            return;
                        }
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }

                // Unknown/unsupported ticket type. Keep polling.
                if(Debug.isDebugBuild) {
                    var typeName = status.Type != null ? status.Type.Name : "null";
                    Debug.Log($"[UGS Matchmaker] Ticket '{_matchmakerTicketId}' status type='{typeName}'");
                }
                try {
                    await UniTask.Delay(6000, cancellationToken: ct); // 6s Catch-all Poll Interval
                } catch(OperationCanceledException) {
                    return;
                }
            }
        }

        private async UniTask HandleStoredMatchmakerResultsAsync(string mode, int maxPlayers, string matchId,
            StoredMatchmakingResults results) {
            if(results == null) return;
            if(results.MatchProperties == null) return;
            if(results.MatchProperties.Players == null) return;

            var localPlayerId = AuthenticationService.Instance.PlayerId;
            if(string.IsNullOrEmpty(localPlayerId)) return;

            var hostId = DetermineDeterministicHostId(results.MatchProperties.Players);
            if(string.IsNullOrEmpty(hostId)) return;

            if(localPlayerId == hostId) {
                await StartUgsPublicMatchAsHostAsync(mode, maxPlayers, matchId, results);
            } else {
                await JoinUgsPublicMatchByMatchIdAsync(matchId);
            }
        }

        private static string DetermineDeterministicHostId(List<Player> players) {
            if(players == null) return "";
            if(players.Count == 0) return "";

            var best = "";
            foreach(var t in players) {
                var id = t.Id;
                if(string.IsNullOrEmpty(id)) continue;
                if(string.IsNullOrEmpty(best) || string.CompareOrdinal(id, best) < 0) {
                    best = id;
                }
            }
            return best;
        }

        private static string GetQueueNameForMode(string mode) {
            if(string.IsNullOrEmpty(mode)) return "";

            // Per-mode queues (hyphenated, no whitespace) to match Dashboard configuration.
            return mode switch {
                "Hopball" => "Hopball",
                "Deathmatch" => "Deathmatch",
                "KOTH" => "KOTH",
                "Gun Tag" => "Gun-Tag",
                "Team Deathmatch" => "Team-Deathmatch",
                _ => ""
            };
        }

        private async UniTask StartUgsPublicMatchAsHostAsync(string mode, int maxPlayers, string matchId, StoredMatchmakingResults results) {
            await UgsAuthService.InitializeAndSignInAsync();
            Debug.Log($"[SessionManager] StartUgsPublicMatchAsHostAsync: mode='{mode}' maxPlayers={maxPlayers} matchId='{matchId}'");
            ApplyRuntimeMode(mode, "UgsPublicMatchHost");

            // Store the expected player IDs from the matchmaker results for sync checking
            List<string> expectedPlayerIds = null;
            if(results?.MatchProperties?.Players != null) {
                expectedPlayerIds = results.MatchProperties.Players
                    .Select(p => p.Id)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .ToList();
                Debug.Log($"[SessionManager] Expecting {expectedPlayerIds.Count} players for sync");
            }

            // Relay allocation for host.
            var alloc = await RelayService.Instance.CreateAllocationAsync(maxPlayers - 1);
            var joinCode = await RelayService.Instance.GetJoinCodeAsync(alloc.AllocationId);

            var create = new CreateLobbyOptions {
                IsPrivate = false,
                Player = BuildUgsLobbyPlayer(),
                Data = new Dictionary<string, Unity.Services.Lobbies.Models.DataObject> {
                    [UgsMatchTypeKey] = new(
                        Unity.Services.Lobbies.Models.DataObject.VisibilityOptions.Public, "Public"),
                    [UgsTargetModeKey] = new(
                        Unity.Services.Lobbies.Models.DataObject.VisibilityOptions.Public, mode),
                    [UgsRelayJoinCodeKey] = new(
                        Unity.Services.Lobbies.Models.DataObject.VisibilityOptions.Member, joinCode),
                    [UgsMatchIdKey] = new(
                        Unity.Services.Lobbies.Models.DataObject.VisibilityOptions.Public, matchId,
                        Unity.Services.Lobbies.Models.DataObject.IndexOptions.S1),
                    // Start in SynchronizingLoad state so clients know to fade out and report ready
                    [UgsLobbyStateKey] = new(
                        Unity.Services.Lobbies.Models.DataObject.VisibilityOptions.Public, "SynchronizingLoad")
                }
            };

            _ugsMatchLobby = await LobbyService.Instance.CreateLobbyAsync("HOP Match", maxPlayers, create);
            UpdateSteamRichPresenceForUgs();
            Debug.Log($"[SessionManager] Created UGS lobby in SynchronizingLoad state. lobbyId='{_ugsMatchLobby.Id}'");
            FlowLog.Emit(FlowEventIds.PartyLifecycle,
                ("action", "CreateUgsMatchHost"),
                ("matchId", matchId),
                ("lobbyId", _ugsMatchLobby.Id),
                ("mode", mode),
                ("maxPlayers", maxPlayers));

            // Host also fades out and marks self ready
            Phase = SessionPhase.SynchronizingLoad;
            SetFrontStatus(SessionPhase.SynchronizingLoad, "Waiting for players...");
            
            if(SceneTransitionManager.Instance != null) {
                await SceneTransitionManager.Instance.FadeOutAsync();
            } else {
                await UniTask.Delay(500);
            }
            _ugsHostPreFadedOut = true;

            // Mark host as ready
            var localUgsId = AuthenticationService.Instance.PlayerId;
            try {
                var opts = new UpdatePlayerOptions {
                    Data = new Dictionary<string, Unity.Services.Lobbies.Models.PlayerDataObject>
                        {
                            [UgsMemberReadyKey] = new(
                                Unity.Services.Lobbies.Models.PlayerDataObject.VisibilityOptions.Member, "1")
                        }
                };
                _ugsMatchLobby = await LobbyService.Instance.UpdatePlayerAsync(_ugsMatchLobby.Id, localUgsId, opts);
                _ugsLocalReadySubmitted = true;
                Debug.Log("[SessionManager] Host marked as ready");
            } catch(Exception ex) {
                Debug.LogWarning($"[SessionManager] Failed to mark host ready: {ex.Message}");
            }

            // Poll until all expected players have joined and are ready
            var allReady = false;
            for(var i = 0; i < 60; i++) { // 60 seconds timeout
                await UniTask.Delay(1000);
                
                try {
                    _ugsMatchLobby = await LobbyService.Instance.GetLobbyAsync(_ugsMatchLobby.Id);
                } catch {
                    continue;
                }

                if(_ugsMatchLobby == null) break;

                // Check if all expected players are in lobby and ready
                if(AreAllExpectedPlayersReady(_ugsMatchLobby, expectedPlayerIds)) {
                    allReady = true;
                    Debug.Log("[SessionManager] All expected players ready! Starting match...");
                    break;
                }

                Debug.Log($"[SessionManager] Waiting for players... lobby has {_ugsMatchLobby.Players?.Count ?? 0} players");
            }

            if(!allReady) {
                Debug.LogError("[SessionManager] Timed out waiting for all players. Aborting to menu...");
                LeaveToMainMenuAsync().Forget();
                return;
            }

            // Update lobby state to LoadingScene
            try {
                var updateOpts = new UpdateLobbyOptions {
                    Data = new Dictionary<string, Unity.Services.Lobbies.Models.DataObject> {
                        [UgsLobbyStateKey] = new(
                            Unity.Services.Lobbies.Models.DataObject.VisibilityOptions.Public, "LoadingScene")
                    }
                };
                _ugsMatchLobby = await LobbyService.Instance.UpdateLobbyAsync(_ugsMatchLobby.Id, updateOpts);
                Debug.Log("[SessionManager] Updated lobby state to 'LoadingScene'");
            } catch(Exception ex) {
                Debug.LogWarning($"[SessionManager] Failed to update lobby state: {ex.Message}");
            }

            // Now start the host
            await CleanupNetworkAsync();

            if(_networkManager == null) _networkManager = NetworkManager.Singleton;
            if(_networkManager == null) return;

            var utp = _networkManager.GetComponent<UnityTransport>();
            if(utp == null) {
                Debug.LogError("[SessionManager] UnityTransport missing on NetworkManager. Cannot start matchmaker relay match.");
                return;
            }

            if(TryApplyRelayToTransport(utp, alloc, null) == false) return;
            _networkManager.NetworkConfig.NetworkTransport = utp;

            ApplyLocalConnectionPayload(false);
            _networkManager.StartHost();
            Phase = SessionPhase.LoadingScene;
            _networkManager.SceneManager.LoadScene(GameSceneName, LoadSceneMode.Single);
        }

        private void ApplyRuntimeMode(string mode, string source, bool refreshUi = true) {
            if(string.IsNullOrWhiteSpace(mode)) return;

            var changed = SelectedGameMode != mode;
            SelectedGameMode = mode;

            var matchSettings = Game.Match.MatchSettingsManager.Instance;
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

            if(changed && refreshUi && FrontStatusChanged != null) {
                FrontStatusChanged.Invoke(null);
            }
        }

        private bool TryGetAuthoritativeRuntimeMode(out string mode, out string source) {
            if(_ugsMatchLobby != null && _ugsMatchLobby.Data != null &&
               _ugsMatchLobby.Data.TryGetValue(UgsTargetModeKey, out var ugsModeObj) &&
               ugsModeObj != null && !string.IsNullOrEmpty(ugsModeObj.Value)) {
                mode = ugsModeObj.Value;
                source = "UgsMatchLobby";
                return true;
            }

            if(CurrentLobby.HasValue) {
                var steamMode = CurrentLobby.Value.GetData(TargetModeKey);
                if(!string.IsNullOrEmpty(steamMode)) {
                    mode = steamMode;
                    source = "SteamLobby";
                    return true;
                }
            }

            if(!string.IsNullOrEmpty(SelectedGameMode)) {
                mode = SelectedGameMode;
                source = "SelectedGameMode";
                return true;
            }

            mode = null;
            source = null;
            return false;
        }

        private async UniTask JoinUgsPublicMatchByMatchIdAsync(string matchId) {
            await UgsAuthService.InitializeAndSignInAsync();
            if(string.IsNullOrEmpty(matchId)) return;

            Debug.Log($"[SessionManager] Joining match as non-host. matchId='{matchId}'");

            // Poll lobby query until the host publishes the match lobby.
            for(var i = 0; i < 30; i++) {
                Debug.Log($"[SessionManager] Polling for lobby... attempt {i+1}/30");
                try {
                    var lobby = await QueryMatchLobbyByMatchIdAsync(matchId);
                    if(lobby != null) {
                        Debug.Log($"[SessionManager] Found lobby! lobbyId='{lobby.Id}'. Joining...");
                        await JoinUgsMatchLobbyByIdAsync(lobby.Id);
                        return;
                    }
                    Debug.Log("[SessionManager] Lobby not found yet, waiting...");
                } catch(LobbyServiceException ex) when (ex.Reason == LobbyExceptionReason.RateLimited) {
                    Debug.LogWarning("[SessionManager] Rate limited querying match. Retrying...");
                } catch(Exception ex) {
                    Debug.LogError($"[SessionManager] Terminal error querying match: {ex.Message}. Aborting...");
                    break;
                }
                await UniTask.Delay(1000);
            }

            Debug.LogError("[SessionManager] Timed out or failed waiting for match lobby. Returning to menu...");
            LeaveToMainMenuAsync().Forget();
        }

        private static async UniTask<Unity.Services.Lobbies.Models.Lobby> QueryMatchLobbyByMatchIdAsync(string matchId) {
            if(string.IsNullOrEmpty(matchId)) return null;

            var opts = new QueryLobbiesOptions {
                Filters = new List<Unity.Services.Lobbies.Models.QueryFilter> {
                    new(
                        Unity.Services.Lobbies.Models.QueryFilter.FieldOptions.S1,
                        matchId,
                        Unity.Services.Lobbies.Models.QueryFilter.OpOptions.EQ)
                }
            };

            Unity.Services.Lobbies.Models.QueryResponse resp;
            try {
                resp = await LobbyService.Instance.QueryLobbiesAsync(opts);
            } catch {
                return null;
            }

            if(resp == null) return null;
            if(resp.Results == null) return null;
            return resp.Results.Count == 0 ? null : resp.Results[0];
        }

        #endregion

        /// <summary>
        /// Kicked a member from the session.
        /// </summary>
        /// <param name="targetId">The Steam ID of the member to kick.</param>
        public void KickMember(SteamId targetId) {
            if(!IsPartyLeader) return;

            if(!_networkManager.IsServer) return;
            _networkManager.DisconnectClient(targetId.Value);
            Debug.Log($"[SessionManager] Kicked Client {targetId}");
        }

        /// <summary>
        /// Promotes a member to be the new party leader.
        /// </summary>
        /// <param name="targetId">The Steam ID of the member to promote.</param>
        public void PromoteMember(SteamId targetId) {
            if(!IsPartyLeader || !CurrentLobby.HasValue) return;

            var lobby = CurrentLobby.Value;
            lobby.Owner = new Friend(targetId);
            Debug.Log($"[SessionManager] Promoted {targetId} to Host.");
        }

        /// <summary>
        /// Logic for migrating the Netcode session to a new host during host migration.
        /// </summary>
        private async UniTaskVoid MigrateNetcodeToNewHost(ulong newHostId) {
            SetFrontStatus(SessionPhase.JoiningLobby, "Migrating Host...");

            await CleanupNetworkAsync();

            if(newHostId == SteamClient.SteamId) {
                Debug.Log("[SessionManager] I am the new Host. Starting Server...");
                StartHost();
                if(CurrentLobby != null) {
                    CurrentLobby.Value.SetData(HostAddressKey, SteamClient.SteamId.ToString());
                }
                SetFrontStatus(SessionPhase.LobbyReady, "You are now Host.");
            } else {
                Debug.Log($"[SessionManager] Connecting to new Host {newHostId}...");

                var hostAddress = "";
                var retries = 0;
                while(retries < 20) {
                    if(CurrentLobby.HasValue) hostAddress = CurrentLobby.Value.GetData(HostAddressKey);
                    if(hostAddress == newHostId.ToString()) break;
                    await UniTask.Delay(500);
                    retries++;
                }

                if(hostAddress == newHostId.ToString()) {
                    var transport = _networkManager.GetComponent<FacepunchTransport>();
                    if(transport != null) {
                        transport.targetSteamId = newHostId;
                        _networkManager.StartClient();
                        SetFrontStatus(SessionPhase.LobbyReady, "Connected to new Host.");
                    }
                } else {
                    Debug.LogError("[SessionManager] Failed to resolve new Host Address.");
                    SetFrontStatus(SessionPhase.Error, "Host Migration Failed.");
                }
            }
        }

        #endregion
    }
}
