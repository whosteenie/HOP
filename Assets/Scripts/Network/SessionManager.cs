using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Audio;
using Game.Match;
using Game.Menu;
using Game.UI;
using Network.Core;
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
        public enum SessionPhase {
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
        public SessionPhase Phase { get; private set; }
        public bool IsInGameplay { get; private set; }
        public string SelectedGameMode { get; private set; } = "Deathmatch";

        private const string GameSceneName = "Game";
        public string CurrentPartyId { get; private set; }
        public bool IsPartyLeader { get; private set; }

        private string _cachedSceneName;
        private const string HostAddressKey = "HostAddress";
        private const string GameModeKey = "GameMode";
        private const string PartyIdKey = "PartyId";
        private const string FollowLobbyIdKey = "FollowLobbyId";
        private const string LobbyStateKey = "LobbyState";
        private const string TargetModeKey = "TargetMode";
        private const string MemberReadyKey = "ReadyToLoad";
        private readonly List<ulong> _clientsFinishedLoading = new();
        private CustomNetworkManager _customNetworkManager;
        private NetworkManager networkManager;
        private bool _isLeaving;
        private bool _hasCompletedInitialLoad;
        private CancellationTokenSource _matchmakingCts;

        public bool IsSearching {
            get {
                if (Phase == SessionPhase.Searching || Phase == SessionPhase.CreatingLobby || 
                    Phase == SessionPhase.JoiningLobby || Phase == SessionPhase.StartingClient ||
                    Phase == SessionPhase.SynchronizingLoad || Phase == SessionPhase.LoadingScene) return true;
                
                if (Phase == SessionPhase.LobbyReady) {
                    // We are only "Locked/Searching" if the lobby is public (queueing)
                    return CurrentLobby.HasValue && CurrentLobby.Value.GetData(GameModeKey) == "Public";
                }
                return false;
            }
        }

        public bool ShowMatchmakingStatus {
            get {
                if (Phase == SessionPhase.Menu || Phase == SessionPhase.InGame || Phase == SessionPhase.Error || 
                    Phase == SessionPhase.SynchronizingLoad || Phase == SessionPhase.LoadingScene) return false;
                
                // Don't show status card for private lobbies when they are ready (except when syncing load)
                if (Phase == SessionPhase.LobbyReady || Phase == SessionPhase.StartingHost) {
                    if (CurrentLobby.HasValue && CurrentLobby.Value.GetData(GameModeKey) == "Private") return false;
                }
                
                return IsSearching;
            }
        }

        // Events
        public event Action<string> FrontStatusChanged;

        #region Unity Lifecycle

        protected override void Awake() {
            if (HasInstance && Instance != this) {
                Destroy(gameObject);
                return;
            }
            DontDestroyOnLoad(gameObject);
            
            networkManager = NetworkManager.Singleton;
            if (networkManager != null) {
                _customNetworkManager = networkManager.GetComponent<CustomNetworkManager>();
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
            // Ensure Steam is init (via SteamManager singleton usually, but let's double check or wait)
            if (SteamManager.Instance == null) {
                Debug.LogError("[SessionManager] SteamManager not found!");
            }
        }

        private void OnDestroy() {
            LeaveLobby();
        }

        #endregion

        #region Steam Callbacks
        private void OnLobbyDataChanged(Lobby lobby) {
            if (lobby.Id == CurrentLobby?.Id) {
                // Handle GameMode display
                var mode = lobby.GetData(TargetModeKey);
                if (!string.IsNullOrEmpty(mode) && mode != SelectedGameMode) {
                    SelectedGameMode = mode;
                    FrontStatusChanged?.Invoke(null); // Force UI Refresh
                }

                // Handle Party Persistence
                var partyId = lobby.GetData(PartyIdKey);
                if (!string.IsNullOrEmpty(partyId) && partyId != CurrentPartyId) {
                    CurrentPartyId = partyId;
                }

                // Handle "Follow Leader" Migration
                var followIdStr = lobby.GetData(FollowLobbyIdKey);
                if (!string.IsNullOrEmpty(followIdStr) && Phase != SessionPhase.InGame) {
                    ulong followId = ulong.Parse(followIdStr);
                    if (followId != lobby.Id) {
                        Debug.Log($"[SessionManager] Leader moved to lobby {followId}. Following...");
                        JoinSessionByLobbyIdAsync(followId).Forget();
                    }
                }

                // Handle Synchronization
                var state = lobby.GetData(LobbyStateKey);
                if (state == "SynchronizingLoad") {
                    if (Phase != SessionPhase.SynchronizingLoad && Phase != SessionPhase.LoadingScene) {
                        HandleSynchronizationStart().Forget();
                    }
                } else if (state == "LoadingScene") {
                    if (Phase != SessionPhase.LoadingScene) {
                        Phase = SessionPhase.LoadingScene;
                        BeginSceneLoad();
                    }
                }
            }
        }

        private async UniTask HandleSynchronizationStart() {
            SetFrontStatus(SessionPhase.SynchronizingLoad, "Waiting for party...");
            
            // Trigger Fade Out via SceneTransitionManager
            if (SceneTransitionManager.Instance != null) {
                await SceneTransitionManager.Instance.FadeOutAsync();
            } else {
                // Fallback if no transition manager
                await UniTask.Delay(500);
            }

            // Once fully black, report ready
            CurrentLobby?.SetMemberData(MemberReadyKey, "true");
        }

        private void OnLobbyMemberDataChanged(Lobby lobby, Friend friend) {
            if (lobby.Id != CurrentLobby?.Id) return;

            // Host monitors member readiness
            if (IsPartyLeader && Phase == SessionPhase.SynchronizingLoad) {
                CheckAllMembersReady();
            }
        }

        private void CheckAllMembersReady() {
            if (!CurrentLobby.HasValue) return;

            var members = CurrentLobby.Value.Members.ToList();
            bool allReady = true;
            foreach (var member in members) {
                if (CurrentLobby.Value.GetMemberData(member, MemberReadyKey) != "true") {
                    allReady = false;
                    break;
                }
            }

            if (allReady) {
                Debug.Log("[SessionManager] All members ready! Starting scene transition...");
                CurrentLobby.Value.SetData(LobbyStateKey, "LoadingScene");
            }
        }

        private void BeginSceneLoad() {
             var mode = CurrentLobby?.GetData(TargetModeKey);
             if (!string.IsNullOrEmpty(mode)) SelectedGameMode = mode;
             
             if (IsPartyLeader) {
                 networkManager.SceneManager.LoadScene(GameSceneName, LoadSceneMode.Single);
             }
        }

        private void OnLobbyMemberJoined(Lobby lobby, Friend friend) {
            Debug.Log($"[SessionManager] Member Joined: {friend.Name}");
            RefreshPlayerList();
        }

        private void OnLobbyMemberLeave(Lobby lobby, Friend friend) {
            Debug.Log($"[SessionManager] Member Left: {friend.Name}");
            RefreshPlayerList();
        }

        private async void OnGameLobbyJoinRequested(Lobby lobby, SteamId id) {
            Debug.Log($"[SessionManager] Accepted Invite to Lobby {lobby.Id}");
            await JoinSessionByLobbyAsync(lobby);
        }
        
        private void RefreshPlayerList() {
             // TODO: Create a simple IReadOnlyPlayer wrapper for UI if needed
             // For now, we rely on the Party UI pulling directly from Steam or we publish a generic event.
             // We can publish players via EventBus if we map them.
        }

        #endregion

        public async UniTask StartPrivateMatchSync(string mode) {
            if (!IsPartyLeader || !CurrentLobby.HasValue) return;

            SelectedGameMode = mode;
            // Set targets for everyone
            CurrentLobby.Value.SetData(TargetModeKey, mode);
            
            // IMPORTANT: Setting GameModeKey to the actual gamemode so it can be read by game logic.
            // "Private" lobbies generally use "Private" as the key to avoid matchmaking, but
            // some systems read this key to know what to spawn.
            // If checking matchmaking, we filter by 'GameMode' = 'Public' AND 'TargetMode' = 'Hopball'.
            // So for private matches, we can likely safely set this to the checked mode IF
            // we ensure we are NOT searchable.
            // BUT, if SetPrivate() is called, it shouldn't matter what the data is?
            // Wait, FindGameAsync filters by `.WithKeyValue(GameModeKey, "Public")`.
            // So as long as we don't set it to "Public", we are safe?
            // No, user said "we are still only in 'Private' gamemode". 
            // This suggests Game Logic reads the 'GameMode' data key.
            // So we MUST set it to 'mode' (e.g. "Hopball").
            CurrentLobby.Value.SetData(GameModeKey, mode);

            // Start the synchronization phase
            Phase = SessionPhase.SynchronizingLoad;
            
            // Clear previous ready states
            foreach (var member in CurrentLobby.Value.Members) {
                CurrentLobby.Value.SetMemberData(MemberReadyKey, "false");
            }

            // Broadcast state - this triggers OnLobbyDataChanged on everyone including us
            // But since we are already in Phase = SynchronizingLoad, OnLobbyDataChanged might skip us?
            // Actually OnLobbyDataChanged check is: if (Phase != SynchronizingLoad ...)
            // So we need to handle our own fade to black here OR reset phase?
            // Better: Let OnLobbyDataChanged handle it for everyone. 
            // So we DON'T set Phase here manually yet?
            // Or we do set it, but we also call HandleSynchronizationStart() manually?
            
            // Let's reset phase so OnLobbyDataChanged catches it, 
            // OR just call the method directly.
            
            // Trigger UI update
            FrontStatusChanged?.Invoke("Synchronizing party...");
            
            // Set data to trigger everyone else
            CurrentLobby.Value.SetData(LobbyStateKey, "SynchronizingLoad");
            
            // We need to also lock ourselves in.
            await HandleSynchronizationStart();
        }

        #region Public API - Matchmaking

        /// <summary>
        /// Starts a "Private" Lobby (Friends Only) and waits for connects.
        /// Replaces "StartSessionAsHost".
        /// </summary>
        public async UniTask<bool> CreatePrivateLobbyAsync() {
            SetFrontStatus(SessionPhase.CreatingLobby, "Creating Private Lobby...");
            
            // 1. Leave current
            LeaveLobby();
            await CleanupNetworkAsync();

            try {
                // 2. Create Steam Lobby
                var result = await SteamMatchmaking.CreateLobbyAsync(16);
                if (!result.HasValue) {
                    SetFrontStatus(SessionPhase.Error, "Failed to create lobby.");
                    return false;
                }

                CurrentLobby = result.Value;
                CurrentLobby.Value.SetPrivate(); // Friends Only
                CurrentLobby.Value.SetData(HostAddressKey, SteamClient.SteamId.ToString());
                CurrentLobby.Value.SetData(GameModeKey, "Private");

                // Initialize Party ID if we don't have one
                if (string.IsNullOrEmpty(CurrentPartyId)) {
                    CurrentPartyId = Guid.NewGuid().ToString();
                }
                IsPartyLeader = true; // We created this!
                CurrentLobby.Value.SetData(PartyIdKey, CurrentPartyId);
                CurrentLobby.Value.SetMemberData(PartyIdKey, CurrentPartyId);

                SetFrontStatus(SessionPhase.LobbyReady, "Lobby Ready. Invite Friends!");
                
                // 3. Start Host (using FacepunchTransport)
                StartHost();
                return true;
            }
            catch (Exception ex) {
                Debug.LogError(ex);
                SetFrontStatus(SessionPhase.Error, "Error creating lobby.");
                return false;
            }
        }

        /// <summary>
        /// "Quick Play": Searches for an open lobby. If none, creates a public one.
        /// </summary>
        public async UniTask FindGameAsync(string mode = null) {
            // Cancel any previous search
            CancelMatchmaking();
            
            if (!string.IsNullOrEmpty(mode)) {
                SelectedGameMode = mode;
            }
            
            _matchmakingCts = new CancellationTokenSource();
            var token = _matchmakingCts.Token;

            try {
                // Cleanup before we start searching so Phase=Menu doesn't flicker
                LeaveLobby();
                await CleanupNetworkAsync();

                SetFrontStatus(SessionPhase.Searching, $"Searching for {SelectedGameMode}...");

                if (token.IsCancellationRequested) return;

                // 1. Search
                var lobbies = await SteamMatchmaking.LobbyList
                    .WithKeyValue(GameModeKey, "Public")
                    .WithKeyValue("TargetMode", SelectedGameMode)
                    .RequestAsync();

                if (token.IsCancellationRequested) return;

                if (lobbies != null) {
                    foreach (var lobby in lobbies) {
                        if (token.IsCancellationRequested) return;
                        if (lobby.MemberCount + (CurrentLobby?.MemberCount ?? 1) <= lobby.MaxMembers) {
                            Debug.Log($"[SessionManager] Found Lobby {lobby.Id}, Joining with Party...");
                            
                            // 1a. Tell our party members to follow us
                            if (CurrentLobby.HasValue) {
                                CurrentLobby.Value.SetData(FollowLobbyIdKey, lobby.Id.ToString());
                                // Small delay to let the data propagate before we leave?
                                // Actually Steam is pretty good, but let's wait a frame.
                                await UniTask.Yield(); 
                            }

                            await JoinSessionByLobbyAsync(lobby);
                            return;
                        }
                    }
                }

                if (token.IsCancellationRequested) return;

                // 2. No lobby found -> Create Public or Convert Existing
                if (CurrentLobby.HasValue && CurrentLobby.Value.GetData(GameModeKey) == "Private") {
                     Debug.Log($"[SessionManager] Reusing private lobby for public game.");
                     CurrentLobby.Value.SetPublic();
                     CurrentLobby.Value.SetJoinable(true);
                     CurrentLobby.Value.SetData(GameModeKey, "Public");
                     CurrentLobby.Value.SetData("TargetMode", SelectedGameMode);
                     
                     StartHost(); // Ensure hosting
                     SetFrontStatus(SessionPhase.LobbyReady, "Waiting for players...");
                } else {
                    Debug.Log($"[SessionManager] No {SelectedGameMode} lobbies found. Creating new public lobby.");
                    SetFrontStatus(SessionPhase.CreatingLobby, $"Creating {SelectedGameMode} Lobby...");
                    
                    var result = await SteamMatchmaking.CreateLobbyAsync(16);
                    if (token.IsCancellationRequested) {
                        if (result.HasValue) result.Value.Leave();
                        return;
                    }

                    if (result.HasValue) {
                        CurrentLobby = result.Value;
                        CurrentLobby.Value.SetPublic();
                        CurrentLobby.Value.SetJoinable(true);
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
            } catch (OperationCanceledException) {
                Debug.Log("[SessionManager] Matchmaking Cancelled.");
            } finally {
                if (_matchmakingCts != null) {
                    _matchmakingCts.Dispose();
                    _matchmakingCts = null;
                }
            }
        }

        public void CancelMatchmaking() {
            Debug.Log($"[SessionManager] CancelMatchmaking Called. Current Phase: {Phase}");
            if (_matchmakingCts != null) {
                _matchmakingCts.Cancel();
                _matchmakingCts.Dispose();
                _matchmakingCts = null;
            }
            
            // If we are hosting a public lobby (or were searching), revert to private lobby
            if (CurrentLobby.HasValue && IsPartyLeader) {
                // Keep the lobby, just make it private
                CurrentLobby.Value.SetPrivate();
                CurrentLobby.Value.SetData(GameModeKey, "Private");
                // Reset phase to LobbyReady (Private) which hides the status card
                SetFrontStatus(SessionPhase.LobbyReady, "");
            } else {
                // If we were just searching as a client without a lobby (or joined one), and want to cancel?
                // Actually, if we are Client in a public lobby, leaving means leaving the lobby.
                // But the requirement says "flip your lobby back to private", implying we are the host/party leader.
                
                if (Phase != SessionPhase.InGame && Phase != SessionPhase.Menu) {
                    // Fallback for non-hosts or hard resets
                    if (!IsPartyLeader && CurrentLobby.HasValue) {
                         LeaveLobby();
                         CleanupNetworkAsync().Forget();
                         SetFrontStatus(SessionPhase.Menu, "");
                    } else {
                        // We are host but something is weird, or we didn't have a lobby yet (pure searching)
                        // If we didn't have a lobby, we should create a private one?
                        // Or Just go to menu.
                         SetFrontStatus(SessionPhase.Menu, "");
                         // If we want to support "Cancel Search -> Create Private Lobby", we'd do that here.
                         // But for now, if we have a lobby, we keep it. Use CreatePrivateLobbyAsync if null?
                         if (!CurrentLobby.HasValue) {
                             CreatePrivateLobbyAsync().Forget();
                         }
                    }
                }
            }
        }

        /// <summary>
        /// Joins a specific Steam Lobby.
        /// </summary>
        public async UniTask JoinSessionByLobbyAsync(Lobby lobby) {
            SetFrontStatus(SessionPhase.JoiningLobby, "Joining...");
            
            var result = await lobby.Join();
            if (result != RoomEnter.Success) {
                SetFrontStatus(SessionPhase.Error, $"Failed to join: {result}");
                return;
            }

            CurrentLobby = lobby;
            
            // Sync Party ID from lobby
            var lobbyPartyId = lobby.GetData(PartyIdKey);
            if (!string.IsNullOrEmpty(lobbyPartyId)) {
                CurrentPartyId = lobbyPartyId;
                // If we joined a lobby with a party ID, we are NOT the global leader (unless it's ours)
                if (lobby.Owner.Id != SteamClient.SteamId) {
                    IsPartyLeader = false;
                }
            } else if (lobby.Owner.Id == SteamClient.SteamId) {
                // We are host of a lobby that has no party ID? (Shouldn't happen with our logic)
                IsPartyLeader = true;
            }

            // Tag ourselves as being in this party
            if (!string.IsNullOrEmpty(CurrentPartyId)) {
                lobby.SetMemberData(PartyIdKey, CurrentPartyId);
            }

            // Wait for Host Address to be set
            SetFrontStatus(SessionPhase.StartingClient, "Connecting to Host...");
            
            // 4. Get Host Data & Connect
            string hostAddress = lobby.GetData(HostAddressKey);
            
            // Retry logic if host hasn't set data yet
            int retries = 0;
            while(string.IsNullOrEmpty(hostAddress) && retries < 10) {
                 await UniTask.Delay(500);
                 hostAddress = lobby.GetData(HostAddressKey);
                 retries++;
            }

            if (string.IsNullOrEmpty(hostAddress)) {
                SetFrontStatus(SessionPhase.Error, "Host address not found.");
                LeaveLobby();
                return;
            }

            if (!ulong.TryParse(hostAddress, out ulong steamId)) {
                SetFrontStatus(SessionPhase.Error, "Invalid Host ID.");
                LeaveLobby();
                return;
            }

            // Configure Transport
            var transport = networkManager.GetComponent<FacepunchTransport>();
            if (transport == null) {
                Debug.LogError("FacepunchTransport missing on NetworkManager!");
                return;
            }
            transport.targetSteamId = steamId;

            Debug.Log($"[SessionManager] Starting Client connecting to {steamId}");
            networkManager.StartClient();
        }

        public async UniTask JoinSessionByLobbyIdAsync(ulong lobbyId) {
            // Steamworks.Data.Lobby doesn't have a direct "By ID" async fetch in some versions
            // but we can join by ID directly.
            // However, Facepunch.Steamworks allows Lobby query by Id via LobbyList.
            var lobbies = await SteamMatchmaking.LobbyList
                .WithSlotsAvailable(0) // Any slot count
                .RequestAsync();
            
            if (lobbies != null) {
                var target = lobbies.FirstOrDefault(l => l.Id == lobbyId);
                // If not found in common list, try to join directly (Steam allows this)
                // In Facepunch, JoinLobby(id) returns UniTask<Lobby?>
                var joinedLobby = await SteamMatchmaking.JoinLobbyAsync(lobbyId);
                if (joinedLobby.HasValue) {
                    await JoinSessionByLobbyAsync(joinedLobby.Value);
                } else {
                    SetFrontStatus(SessionPhase.Error, "Target lobby not found or join failed.");
                }
            }
        }

        public void SetGamemode(string mode) {
            SelectedGameMode = mode;
            if (CurrentLobby.HasValue && CurrentLobby.Value.Owner.Id == SteamClient.SteamId) {
                CurrentLobby.Value.SetData("TargetMode", mode);
            }
            FrontStatusChanged?.Invoke(null); // Trigger UI Refresh
        }

        public void ToggleGamemodeDropdown() {
             // This is a UI-specific interaction, but if the MainMenuSessionManager delegates it to us
             // we probably don't have the UI reference here.
             // Wait, the error was that MainMenuManager was calling sessionManager.ToggleGamemodeDropdown()
             // which is MainMenuSessionManager. MainMenuSessionManager THEN calls SessionManager?
             // Ah, I added `SessionManager.Instance.ToggleGamemodeDropdown()` in Step 5143 replacement.
             // But SessionManager is a logic singleton, it shouldn't know about Dropdown UI.
             // MainMenuSessionManager should handle the UI toggle logic itself.
             // I made a mistake in the previous replacement by trying to delegate it.
             
             // I will fix MainMenuSessionManager in the next step. 
             // But since I am editing SessionManager now, I will NOT add it here to keep separation of concerns.
        }
        
        public void LeaveLobby() {
            if (CurrentLobby.HasValue) {
                CurrentLobby.Value.Leave();
                CurrentLobby = null;
            }
            Phase = SessionPhase.Menu;
        }
        
        public async UniTask LeaveToMainMenuAsync() {
            // Stop Audio
            EventBus.Publish(new StopAllSoundsEvent());

            string currentScene = SceneManager.GetActiveScene().name;
            bool shouldFade = currentScene != "MainMenu";

             // Fade
             if (shouldFade && SceneTransitionManager.Instance != null)
                 await SceneTransitionManager.Instance.FadeOut().ToUniTask();

            LeaveLobby();
            await CleanupNetworkAsync();
            
            // Unload additive scenes / Load Menu
            if (currentScene != "MainMenu") {
                SceneManager.LoadScene("MainMenu");
            }

            if (shouldFade && SceneTransitionManager.Instance != null)
                 await SceneTransitionManager.Instance.FadeIn().ToUniTask();

            // Self-Healing Party Reformation
            if (currentScene != "MainMenu") {
                if (IsPartyLeader) {
                    Debug.Log("[SessionManager] Returning to menu as Party Leader. Re-hosting party lobby...");
                    CreatePrivateLobbyAsync().Forget();
                } else if (!string.IsNullOrEmpty(CurrentPartyId)) {
                    Debug.Log("[SessionManager] Returning to menu as Party Member. Searching for leader's lobby...");
                    TryRejoinPartyLobby().Forget();
                }
            }
        }

        private async UniTaskVoid TryRejoinPartyLobby() {
            // Wait for leader to potentially host
            await UniTask.Delay(1000); 

            var lobbies = await SteamMatchmaking.LobbyList
                .WithKeyValue(PartyIdKey, CurrentPartyId)
                .RequestAsync();

            if (lobbies != null && lobbies.Length > 0) {
                await JoinSessionByLobbyAsync(lobbies[0]);
            } else {
                Debug.LogWarning("[SessionManager] Failed to find party lobby to rejoin.");
            }
        }

        #endregion

        #region Internal / Networking

        private void StartHost() {
            var transport = networkManager.GetComponent<FacepunchTransport>();
            // Host does not set targetSteamId usually, or sets it to self? 
            // Transport.StartServer() handles it via creating a Socket.
            networkManager.StartHost();
        }

        private async UniTask CleanupNetworkAsync() {
            if (networkManager.IsListening) {
                networkManager.Shutdown();
            }
            // Wait for shutdown?
            await UniTask.Yield();
        }

        private void SetFrontStatus(SessionPhase phase, string message) {
            Phase = phase;
            FrontStatusChanged?.Invoke(message);
        }

        private void RegisterNetworkCallbacks() {
            if (networkManager == null) networkManager = NetworkManager.Singleton;
            if (networkManager != null) {
                networkManager.OnClientConnectedCallback += OnClientConnected;
                networkManager.OnClientDisconnectCallback += OnClientDisconnected;
            }
        }
        
        private void UnregisterNetworkCallbacks() {
            if (networkManager != null) {
                networkManager.OnClientConnectedCallback -= OnClientConnected;
                networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
            var activeScene = SceneManager.GetActiveScene();
            if (activeScene.IsValid()) _cachedSceneName = activeScene.name;
            
            if (scene.name == GameSceneName) {
                 OnGameSceneLoaded();
            }
        }

        private void OnGameSceneLoaded() {
            if (networkManager.IsServer) {
                _clientsFinishedLoading.Clear();
                // We are server, we are ready.
                // Wait for clients?
                // Logic passed to CustomNetworkManager spawning.
                IsInGameplay = true;
                _customNetworkManager?.EnableGameplaySpawningAndSpawnAll();
            }
            
            // Fade In
            if (SceneTransitionManager.Instance != null)
                SceneTransitionManager.Instance.FadeIn().ToUniTask().Forget();
        }

        private void OnClientConnected(ulong clientId) {
           // Handle connection
           if (networkManager.IsServer) {
               RefreshPlayerList(); 
               // Maybe check if we need to load game scene for them
               if (IsInGameplay) {
                    // Sync scene
               }
           }
        }

        private void OnClientDisconnected(ulong clientId) {
             if (clientId == networkManager.LocalClientId) {
                 // We disconnected
                 HandleUnexpectedDisconnect().Forget();
             }
        }
        
        private async UniTaskVoid HandleUnexpectedDisconnect() {
            SetFrontStatus(SessionPhase.Error, "Disconnected.");
            await LeaveToMainMenuAsync();
        }

        #endregion
    }
}