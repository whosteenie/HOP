using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Social;
using Network.Events;
using Network.Singletons;
using Network.Steam;
using Steamworks;
using Steamworks.Data;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityUtils;

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
        private SessionPhase Phase { get; set; }
        public bool IsInGameplay { get; private set; }
        public string SelectedGameMode { get; private set; } = "Deathmatch";

        private const string GameSceneName = "Game";
        public string CurrentPartyId { get; private set; }
        public bool IsPartyLeader { get; private set; }

        private const string HostAddressKey = "HostAddress";
        private const string GameModeKey = "GameMode";
        private const string PartyIdKey = "PartyId";
        private const string FollowLobbyIdKey = "FollowLobbyId";
        private const string LobbyStateKey = "LobbyState";
        private const string TargetModeKey = "TargetMode";
        private const string MemberReadyKey = "ReadyToLoad";
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
        }

        private void OnDisable() {
            UnregisterNetworkCallbacks();
            SceneManager.sceneLoaded -= OnSceneLoaded;

            SteamMatchmaking.OnLobbyMemberJoined -= OnLobbyMemberJoined;
            SteamMatchmaking.OnLobbyMemberLeave -= OnLobbyMemberLeave;
            SteamMatchmaking.OnLobbyDataChanged -= OnLobbyDataChanged;
            SteamMatchmaking.OnLobbyMemberDataChanged -= OnLobbyMemberDataChanged;
            SteamFriends.OnGameLobbyJoinRequested -= OnGameLobbyJoinRequested;
        }

        private void Start() {
            if(SteamManager.Instance == null) {
                Debug.LogError("[SessionManager] SteamManager not found!");
            }
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
            if(lobby.Id == CurrentLobby?.Id) {
                // Handle GameMode display
                var mode = lobby.GetData(TargetModeKey);
                if(!string.IsNullOrEmpty(mode) && mode != SelectedGameMode) {
                    SelectedGameMode = mode;
                    if(FrontStatusChanged != null) {
                        FrontStatusChanged.Invoke(null); // Force UI Refresh
                    }
                }

                // Handle Party Persistence
                var partyId = lobby.GetData(PartyIdKey);
                if(!string.IsNullOrEmpty(partyId) && partyId != CurrentPartyId) {
                    CurrentPartyId = partyId;
                }

                if(lobby.Owner.Id != 0) {
                    bool amIHost = lobby.Owner.Id == SteamClient.SteamId;

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
                    ulong followId = ulong.Parse(followIdStr);
                    if(followId != lobby.Id) {
                        Debug.Log($"[SessionManager] Leader moved to lobby {followId}. Following...");
                        JoinSessionByLobbyIdAsync(followId).Forget();
                    }
                }

                // Handle Synchronization
                var state = lobby.GetData(LobbyStateKey);
                if(state == "SynchronizingLoad") {
                    if(Phase != SessionPhase.SynchronizingLoad && Phase != SessionPhase.LoadingScene) {
                        HandleSynchronizationStart().Forget();
                    }
                } else if(state == "LoadingScene") {
                    if(Phase != SessionPhase.LoadingScene) {
                        Phase = SessionPhase.LoadingScene;
                        BeginSceneLoad();
                    }
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
            bool allReady = true;
            foreach(var member in members) {
                if(CurrentLobby.Value.GetMemberData(member, MemberReadyKey) != "true") {
                    allReady = false;
                    break;
                }
            }

            if(allReady) {
                Debug.Log("[SessionManager] All members ready! Starting scene transition...");
                CurrentLobby.Value.SetData(LobbyStateKey, "LoadingScene");
            }
        }

        /// <summary>
        /// initiates the Netcode scene load.
        /// </summary>
        private void BeginSceneLoad() {
            string mode = null;
            if(CurrentLobby != null) {
                mode = CurrentLobby.Value.GetData(TargetModeKey);
            }
            if(!string.IsNullOrEmpty(mode)) SelectedGameMode = mode;

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

        /// <summary>
        /// Triggers the OnPartyStateChanged event to notify UI listeners.
        /// </summary>
        private static void NotifyPartyStateChanged() {
            if(HasInstance) {
                var instance = Instance;
                if(instance.OnPartyStateChanged != null) {
                    instance.OnPartyStateChanged.Invoke();
                }
            }
        }

        #endregion

        /// <summary>
        /// Synchronizes the start of a private match across all party members.
        /// </summary>
        /// <param name="mode">The gamemode ID to start.</param>
        public async UniTask StartPrivateMatchSync(string mode) {
            if(!IsPartyLeader || !CurrentLobby.HasValue) return;

            SelectedGameMode = mode;
            // Set targets for everyone
            CurrentLobby.Value.SetData(TargetModeKey, mode);

            CurrentLobby.Value.SetData(GameModeKey, mode);

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

                // Join Voice Channel (only if logged in, otherwise it will be joined after login)
                if (VoiceManager.Instance != null && VoiceManager.Instance.IsLoggedIn) {
                    VoiceManager.Instance.JoinChannelAsync("match_" + CurrentLobby.Value.Id).Forget();
                }

                SetFrontStatus(SessionPhase.LobbyReady, "Lobby Ready. Invite Friends!");

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
                SelectedGameMode = mode;
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
            SelectedGameMode = mode;
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
            EventBus.Publish(new StopAllSoundsEvent());

            var currentScene = SceneManager.GetActiveScene().name;
            var shouldFade = currentScene != "MainMenu";

            if(shouldFade && SceneTransitionManager.Instance != null)
                await SceneTransitionManager.Instance.FadeOut().ToUniTask();

            LeaveLobby();
            await CleanupNetworkAsync();

            if(currentScene != "MainMenu") {
                SceneManager.LoadScene("MainMenu");
            }

            if(shouldFade && SceneTransitionManager.Instance != null)
                await SceneTransitionManager.Instance.FadeIn().ToUniTask();

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

        /// <summary>
        /// Starts the Netcode host using FacepunchTransport.
        /// </summary>
        private void StartHost() {
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