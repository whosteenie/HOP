using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Match;
using Game.Menu;
using Game.Social;
using Network.Core;
using Network.Diagnostics;
using Network.Events;
using Steamworks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Matchmaker.Models;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Network.Session {
    public sealed partial class SessionManager {
        #region Core Session Operations

        private const string MultiplayerSessionType = "HOP.Match";
        private const string MultiplayerSessionModeKey = "mode";
        private const string MultiplayerSessionMatchTypeKey = "matchType";

        private enum DistributedAuthoritySessionJoinResult {
            Success,
            RateLimited,
            Failed
        }

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

        private static List<string> BuildExpectedPlayerIdsFromPartyLobby(Lobby partyLobby,
            string fallbackLocalUgsId) {
            var expected = new List<string>();
            if(partyLobby is { Players: not null }) {
                foreach(var player in partyLobby.Players) {
                    if(player == null || string.IsNullOrEmpty(player.Id)) continue;
                    expected.Add(player.Id);
                }
            }

            if(expected.Count == 0 && string.IsNullOrEmpty(fallbackLocalUgsId) == false) {
                expected.Add(fallbackLocalUgsId);
            }

            return expected;
        }

        private static List<string> BuildExpectedPlayerIdsFromMatchResults(StoredMatchmakingResults results) {
            if(results?.MatchProperties?.Players == null) {
                return null;
            }

            return results.MatchProperties.Players
                .Select(p => p != null ? p.Id : null)
                .Where(id => string.IsNullOrEmpty(id) == false)
                .ToList();
        }

        private async UniTask<bool> TrySetMatchLobbyStateAsync(string lobbyState,
            DataObject.VisibilityOptions visibility, string context) {
            if(_ugsMatchLobby == null || string.IsNullOrEmpty(_ugsMatchLobby.Id)) {
                Debug.LogWarning(
                    $"[SessionManager] Cannot set match lobby state to '{lobbyState}' during {context}: no active match lobby.");
                return false;
            }

            try {
                var stateObject = visibility == DataObject.VisibilityOptions.Public
                    ? new DataObject(visibility, lobbyState, DataObject.IndexOptions.S4)
                    : new DataObject(visibility, lobbyState);
                var update = new UpdateLobbyOptions {
                    Data = new Dictionary<string, DataObject> {
                        [UgsLobbyStateKey] = stateObject
                    }
                };
                _ugsMatchLobby = await LobbyService.Instance.UpdateLobbyAsync(_ugsMatchLobby.Id, update);
                if(visibility == DataObject.VisibilityOptions.Public) {
                    LogPublicLobbySnapshot($"StateUpdate/{context}");
                }
                return true;
            } catch(Exception ex) {
                Debug.LogWarning(
                    $"[SessionManager] Failed to set UGS match lobby state to '{lobbyState}' during {context}: {ex.Message}");
                return false;
            }
        }

        private void LogPublicLobbySnapshot(string context) {
            if(_ugsMatchLobby == null) {
                Debug.LogWarning($"[SessionManager] PublicLobbySnapshot({context}): lobby is null.");
                return;
            }

            var data = _ugsMatchLobby.Data;
            var matchType = data != null && data.TryGetValue(UgsMatchTypeKey, out var matchTypeObj) && matchTypeObj != null
                ? matchTypeObj.Value
                : "";
            if(!string.Equals(matchType, "Public", StringComparison.OrdinalIgnoreCase)) {
                return;
            }

            var mode = data != null && data.TryGetValue(UgsTargetModeKey, out var modeObj) && modeObj != null
                ? modeObj.Value
                : "";
            var state = data != null && data.TryGetValue(UgsLobbyStateKey, out var stateObj) && stateObj != null
                ? stateObj.Value
                : "";
            var matchId = data != null && data.TryGetValue(UgsMatchIdKey, out var matchIdObj) && matchIdObj != null
                ? matchIdObj.Value
                : "";

            var playerCount = _ugsMatchLobby.Players != null ? _ugsMatchLobby.Players.Count : 0;
            Debug.Log(
                $"[SessionManager] PublicLobbySnapshot({context}): lobbyId='{_ugsMatchLobby.Id}' hostId='{_ugsMatchLobby.HostId}' players={playerCount}/{_ugsMatchLobby.MaxPlayers} mode='{mode}' state='{state}' matchId='{matchId}'");
        }

        private SessionOptions BuildDistributedAuthoritySessionOptions(int maxPlayers, bool isPrivateMatch) {
            var options = new SessionOptions {
                Name = $"HOP {SelectedGameMode}",
                MaxPlayers = Mathf.Max(1, maxPlayers),
                IsPrivate = isPrivateMatch,
                Type = MultiplayerSessionType,
                SessionProperties = new Dictionary<string, SessionProperty> {
                    [MultiplayerSessionModeKey] = new(SelectedGameMode ?? string.Empty, VisibilityPropertyOptions.Public),
                    [MultiplayerSessionMatchTypeKey] = new(isPrivateMatch ? "Private" : "Public",
                        VisibilityPropertyOptions.Public)
                },
                PlayerProperties = new Dictionary<string, PlayerProperty> {
                    ["displayName"] = new(LocalIdentity.GetDisplayName(), VisibilityPropertyOptions.Member)
                }
            };

            if(string.IsNullOrEmpty(CurrentPartyId) == false) {
                options.PlayerProperties["partyId"] = new(CurrentPartyId, VisibilityPropertyOptions.Member);
            }

            return options.WithDistributedAuthorityNetwork();
        }

        private void BindActiveMultiplayerSession(ISession session, string contextLabel) {
            if(ReferenceEquals(_activeMultiplayerSession, session)) {
                return;
            }

            UnbindActiveMultiplayerSession();
            _activeMultiplayerSession = session;
            if(_activeMultiplayerSession == null) {
                return;
            }

            _activeMultiplayerSession.SessionHostChanged += OnActiveSessionHostChanged;
            _activeMultiplayerSession.SessionMigrated += OnActiveSessionMigrated;
            _activeMultiplayerSession.RemovedFromSession += OnActiveSessionRemovedFromSession;
            _activeMultiplayerSession.Deleted += OnActiveSessionDeleted;

            if(Debug.isDebugBuild) {
                Debug.Log(
                    $"[SessionManager] Bound DA session ({contextLabel}) id='{_activeMultiplayerSession.Id}' code='{_activeMultiplayerSession.Code}' host='{_activeMultiplayerSession.Host}'.");
            }

            EventBus.Publish(new SessionJoinedEvent(_activeMultiplayerSession.Code));
        }

        private void UnbindActiveMultiplayerSession() {
            if(_activeMultiplayerSession == null) {
                return;
            }

            _activeMultiplayerSession.SessionHostChanged -= OnActiveSessionHostChanged;
            _activeMultiplayerSession.SessionMigrated -= OnActiveSessionMigrated;
            _activeMultiplayerSession.RemovedFromSession -= OnActiveSessionRemovedFromSession;
            _activeMultiplayerSession.Deleted -= OnActiveSessionDeleted;
            _activeMultiplayerSession = null;
        }

        private void OnActiveSessionHostChanged(string newHostId) {
            if(Debug.isDebugBuild) {
                Debug.Log($"[SessionManager] DA session host changed to '{newHostId}'.");
            }

            LaunchSessionTask(RefreshActiveMultiplayerSessionAsync("HostChanged"), "DistributedAuthority/RefreshHostChanged");
            LaunchSessionTask(RefreshMatchLobbyAfterDistributedAuthorityHostChangeAsync("HostChanged", newHostId),
                "DistributedAuthority/RefreshMatchLobbyHostChanged");
            NotifyPartyStateChanged();
        }

        private void OnActiveSessionMigrated() {
            if(Debug.isDebugBuild) {
                Debug.Log("[SessionManager] DA session migration completed.");
            }

            LaunchSessionTask(RefreshActiveMultiplayerSessionAsync("Migrated"), "DistributedAuthority/RefreshMigrated");
            LaunchSessionTask(RefreshMatchLobbyAfterDistributedAuthorityHostChangeAsync("Migrated", null),
                "DistributedAuthority/RefreshMatchLobbyMigrated");
            if(_networkManager != null && _networkManager.IsListening && IsGameplaySceneName(SceneManager.GetActiveScene().name)) {
                SetFrontStatus(SessionPhase.InGame, "");
            }

            NotifyPartyStateChanged();
        }

        private void OnActiveSessionRemovedFromSession() {
            if(Debug.isDebugBuild) {
                Debug.LogWarning("[SessionManager] Removed from active DA session.");
            }

            UnbindActiveMultiplayerSession();
        }

        private void OnActiveSessionDeleted() {
            if(Debug.isDebugBuild) {
                Debug.LogWarning("[SessionManager] Active DA session was deleted.");
            }

            UnbindActiveMultiplayerSession();
        }

        private async UniTask<string> CreateDistributedAuthoritySessionAsync(int maxPlayers, bool isPrivateMatch,
            string contextLabel) {
            await CleanupNetworkAsync();
            ApplyLocalConnectionPayload(isPrivateMatch);

            try {
                var hostSession = await MultiplayerService.Instance.CreateSessionAsync(
                    BuildDistributedAuthoritySessionOptions(maxPlayers, isPrivateMatch));
                BindActiveMultiplayerSession(hostSession, contextLabel);
                return hostSession.Code;
            } catch(Exception ex) {
                Debug.LogError($"[SessionManager] Failed to create DA session during {contextLabel}: {ex}");
                UnbindActiveMultiplayerSession();
                return null;
            }
        }

        private async UniTask<DistributedAuthoritySessionJoinResult> JoinDistributedAuthoritySessionAsync(
            string sessionCode, bool isPrivateMatch,
            string contextLabel) {
            if(string.IsNullOrWhiteSpace(sessionCode)) {
                Debug.LogError($"[SessionManager] Cannot join DA session during {contextLabel}: session code is empty.");
                return DistributedAuthoritySessionJoinResult.Failed;
            }

            await CleanupNetworkAsync();
            ApplyLocalConnectionPayload(isPrivateMatch);

            try {
                var joinOptions = new JoinSessionOptions {
                    Type = MultiplayerSessionType,
                    PlayerProperties = new Dictionary<string, PlayerProperty> {
                        ["displayName"] = new(LocalIdentity.GetDisplayName(), VisibilityPropertyOptions.Member)
                    }
                };
                if(string.IsNullOrEmpty(CurrentPartyId) == false) {
                    joinOptions.PlayerProperties["partyId"] = new(CurrentPartyId, VisibilityPropertyOptions.Member);
                }

                var session = await MultiplayerService.Instance.JoinSessionByCodeAsync(sessionCode, joinOptions);
                BindActiveMultiplayerSession(session, contextLabel);
                return DistributedAuthoritySessionJoinResult.Success;
            } catch(SessionException ex) when(ex.Error == SessionError.RateLimitExceeded) {
                Debug.LogWarning(
                    $"[SessionManager] Rate limited joining DA session '{sessionCode}' during {contextLabel}. Retrying...");
                UnbindActiveMultiplayerSession();
                return DistributedAuthoritySessionJoinResult.RateLimited;
            } catch(Exception ex) {
                Debug.LogError(
                    $"[SessionManager] Failed to join DA session '{sessionCode}' during {contextLabel}: {ex}");
                UnbindActiveMultiplayerSession();
                return DistributedAuthoritySessionJoinResult.Failed;
            }
        }

        private async UniTask LeaveActiveMultiplayerSessionAsync(string contextLabel) {
            if(_activeMultiplayerSession == null) {
                return;
            }

            var activeSession = _activeMultiplayerSession;
            UnbindActiveMultiplayerSession();

            try {
                await activeSession.LeaveAsync();
                if(Debug.isDebugBuild) {
                    Debug.Log($"[SessionManager] Left DA session during {contextLabel}.");
                }
            } catch(Exception ex) {
                Debug.LogWarning($"[SessionManager] Failed to leave DA session during {contextLabel}: {ex.Message}");
            }
        }

        private async UniTask RefreshActiveMultiplayerSessionAsync(string reason) {
            if(_activeMultiplayerSession == null || _isLeaving || _isShuttingDown) {
                return;
            }

            try {
                await UniTask.Delay(250);
                if(_activeMultiplayerSession == null || _isLeaving || _isShuttingDown) {
                    return;
                }

                await _activeMultiplayerSession.RefreshAsync();
            } catch(Exception ex) {
                if(Debug.isDebugBuild) {
                    Debug.LogWarning($"[SessionManager] Failed to refresh active DA session after {reason}: {ex.Message}");
                }
            }
        }

        private async UniTask RefreshMatchLobbyAfterDistributedAuthorityHostChangeAsync(string reason,
            string expectedHostId) {
            if(_isLeaving || _isShuttingDown || _ugsMatchLobby == null || string.IsNullOrEmpty(_ugsMatchLobby.Id)) {
                return;
            }

            var targetLobbyId = _ugsMatchLobby.Id;
            var previousHostId = _ugsMatchLobby.HostId;
            const int maxAttempts = 3;

            for(var attempt = 1; attempt <= maxAttempts; attempt++) {
                try {
                    var delayMs = attempt == 1 ? 250 : 500;
                    await UniTask.Delay(delayMs, cancellationToken: SessionLifetimeToken);
                } catch(OperationCanceledException) {
                    return;
                }

                if(_isLeaving || _isShuttingDown || _ugsMatchLobby == null ||
                   !string.Equals(_ugsMatchLobby.Id, targetLobbyId, StringComparison.Ordinal)) {
                    return;
                }

                try {
                    var refreshedLobby = await LobbyService.Instance.GetLobbyAsync(targetLobbyId);
                    if(refreshedLobby == null) {
                        continue;
                    }

                    _ugsMatchLobby = refreshedLobby;

                    if(Debug.isDebugBuild) {
                        Debug.Log(
                            $"[SessionManager] Refreshed match lobby after DA {reason}. lobbyId='{targetLobbyId}' hostId='{refreshedLobby.HostId}' prevHostId='{previousHostId}' attempt={attempt}/{maxAttempts}");
                    }

                    var hostMatchesExpectation = string.IsNullOrEmpty(expectedHostId) ||
                                                string.Equals(refreshedLobby.HostId, expectedHostId,
                                                    StringComparison.Ordinal);
                    if(IsLocalPlayerLobbyHost(refreshedLobby)) {
                        // Force the promoted host to resume keepalive immediately so in-progress backfill stays discoverable.
                        _nextMatchHeartbeatTime = 0f;
                        _matchHeartbeatBackoffUntil = 0f;
                        _matchHeartbeatRateLimitStreak = 0;

                        if(Debug.isDebugBuild) {
                            Debug.Log(
                                $"[SessionManager] Local player now owns match lobby heartbeats after DA {reason}. lobbyId='{targetLobbyId}'.");
                        }
                    }

                    if(hostMatchesExpectation || IsLocalPlayerLobbyHost(refreshedLobby)) {
                        return;
                    }

                    previousHostId = refreshedLobby.HostId;
                } catch(LobbyServiceException ex) when(ex.Reason is LobbyExceptionReason.LobbyNotFound
                                                        or LobbyExceptionReason.EntityNotFound) {
                    if(Debug.isDebugBuild) {
                        Debug.LogWarning(
                            $"[SessionManager] Match lobby '{targetLobbyId}' no longer exists while refreshing after DA {reason}.");
                    }
                    return;
                } catch(Exception ex) {
                    if(Debug.isDebugBuild) {
                        Debug.LogWarning(
                            $"[SessionManager] Failed to refresh match lobby after DA {reason} (attempt {attempt}/{maxAttempts}): {ex.Message}");
                    }
                }
            }
        }

        private bool TryLoadGameplaySceneAsHost(string contextLabel) {
            if(TryGetNetworkManager(contextLabel, out var networkManager) == false) {
                return false;
            }

            SelectMapForCurrentMode(contextLabel);
            Phase = SessionPhase.LoadingScene;
            networkManager.SceneManager.LoadScene(SelectedMapSceneName, LoadSceneMode.Single);
            return true;
        }

        public string SelectedMapId { get; private set; }
        private string SelectedMapSceneName { get; set; }

        /// <summary>When true, SelectMapForCurrentMode uses existing SelectedMapId/SelectedMapSceneName from private match draft data.</summary>
        private bool _privateMatchMapPreset;

        private void SelectMapForCurrentMode(string context) {
            var usedPreset = _privateMatchMapPreset;
            if(_privateMatchMapPreset) {
                _privateMatchMapPreset = false;
                if(Debug.isDebugBuild) {
                    Debug.Log(
                        $"[SessionManager] Using preset private match map ({context}) mapId='{SelectedMapId}' scene='{SelectedMapSceneName}'.");
                }
            } else if(MatchMapService.TrySelectRandomSceneForGamemode(SelectedGameMode, out var sceneName, out var mapId)) {
                SelectedMapSceneName = sceneName;
                SelectedMapId = mapId;
            } else {
                SelectedMapSceneName = MatchMapService.DefaultGameplaySceneName;
                SelectedMapId = MatchMapService.DefaultMapId;
            }

            if(Debug.isDebugBuild && !usedPreset) {
                Debug.Log(
                    $"[SessionManager] Map selected ({context}) mode='{SelectedGameMode}' mapId='{SelectedMapId}' scene='{SelectedMapSceneName}'.");
            }

            if(!CurrentLobby.HasValue || CurrentLobby.Value.Owner.Id != SteamClient.SteamId) return;
            CurrentLobby.Value.SetData("TargetMapId", SelectedMapId ?? string.Empty);
            CurrentLobby.Value.SetData("TargetMapScene", SelectedMapSceneName ?? string.Empty);
        }

        /// <summary>
        /// Sets the map from a private match draft (map id). Skips random selection when loading the gameplay scene.
        /// </summary>
        private void SetSelectedMapFromId(string mapId) {
            if(string.IsNullOrWhiteSpace(mapId)) return;
            if(!MatchMapService.TryGetSceneByMapId(mapId, out var sceneName)) return;
            SelectedMapId = mapId;
            SelectedMapSceneName = sceneName;
            _privateMatchMapPreset = true;
            if(Debug.isDebugBuild) {
                Debug.Log($"[SessionManager] Private match map set: mapId='{mapId}' scene='{SelectedMapSceneName}'.");
            }
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
            IReadOnlyDictionary<ulong, int> teamAssignments) {
            if(!string.IsNullOrWhiteSpace(mode)) {
                ApplyRuntimeMode(mode, "PrivateMatchDraft", refreshUi: false);
            }

            if(!string.IsNullOrWhiteSpace(mapId)) {
                SetSelectedMapFromId(mapId);
            }

            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings != null) {
                matchSettings.matchDurationSeconds = Mathf.Max(0, matchTimerSeconds);
                matchSettings.preMatchCountdownEnabled = usePreMatchCountdown;
                matchSettings.swapWeaponsOnDeath = swapWeaponsOnDeath;
                matchSettings.scoreToWin = Mathf.Max(0, scoreToWin);
                matchSettings.kothHillSpeed = Mathf.Max(1, kothHillSpeed);
                matchSettings.taggedPlayers = Mathf.Max(1, taggedPlayers);
            }

            PrivateMatchTeamAssignments.Set(teamAssignments);

            if(Debug.isDebugBuild) {
                Debug.Log(
                    $"[SessionManager] ApplyPrivateMatchSettings: mode='{mode}' mapId='{mapId}' timer={matchTimerSeconds} preMatchCountdown={usePreMatchCountdown} swapWeaponsOnDeath={swapWeaponsOnDeath} scoreToWin={scoreToWin} kothHillSpeed={kothHillSpeed} tagged={taggedPlayers} teams={teamAssignments?.Count ?? 0}");
            }
        }

        private static UpdatePlayerOptions BuildReadyToLoadUpdatePlayerOptions() {
            return new UpdatePlayerOptions {
                Data = new Dictionary<string, PlayerDataObject> {
                    [UgsMemberReadyKey] = new(PlayerDataObject.VisibilityOptions.Member, "1")
                }
            };
        }

        private CreateLobbyOptions BuildPrivateMatchCreateOptions(string mode, string networkJoinCode,
            string expectedCsv) {
            return new CreateLobbyOptions {
                IsPrivate = true,
                Player = BuildLobbyPlayer(),
                Data = new Dictionary<string, DataObject> {
                    [UgsPartyIdKey] = new(DataObject.VisibilityOptions.Member, CurrentPartyId),
                    [UgsMatchTypeKey] = new(DataObject.VisibilityOptions.Member, "Private"),
                    [UgsTargetModeKey] = new(DataObject.VisibilityOptions.Member, mode),
                    [UgsRelayJoinCodeKey] = new(DataObject.VisibilityOptions.Member, networkJoinCode),
                    [UgsLobbyStateKey] = new(DataObject.VisibilityOptions.Member, "SynchronizingLoad"),
                    [UgsExpectedPlayersKey] = new(DataObject.VisibilityOptions.Member, expectedCsv)
                }
            };
        }

        private static UpdateLobbyOptions BuildPartyFollowMatchOptions(string matchLobbyId) {
            return new UpdateLobbyOptions {
                Data = new Dictionary<string, DataObject> {
                    [UgsFollowMatchLobbyIdKey] = new(DataObject.VisibilityOptions.Member, matchLobbyId),
                    [UgsLobbyStateKey] = new(DataObject.VisibilityOptions.Member, "InMatch")
                }
            };
        }

        private static CreateLobbyOptions BuildPublicMatchCreateOptions(string mode, string networkJoinCode, string matchId) {
            return new CreateLobbyOptions {
                IsPrivate = false,
                Player = BuildLobbyPlayer(),
                Data = new Dictionary<string, DataObject> {
                    [UgsMatchTypeKey] = new(DataObject.VisibilityOptions.Public, "Public", DataObject.IndexOptions.S3),
                    [UgsTargetModeKey] = new(DataObject.VisibilityOptions.Public, mode, DataObject.IndexOptions.S2),
                    [UgsRelayJoinCodeKey] = new(DataObject.VisibilityOptions.Member, networkJoinCode),
                    [UgsMatchIdKey] = new(DataObject.VisibilityOptions.Public, matchId, DataObject.IndexOptions.S1),
                    [UgsLobbyStateKey] =
                        new(DataObject.VisibilityOptions.Public, "SynchronizingLoad", DataObject.IndexOptions.S4)
                }
            };
        }

        private void ApplyLocalConnectionPayload(bool isPrivateMatch) {
            if(TryGetNetworkManager("ApplyLocalConnectionPayload", out var networkManager) == false) return;

            if(_customNetworkManager == null) {
                _customNetworkManager = networkManager.GetComponent<CustomNetworkManager>();
            }
            _customNetworkManager?.ConfigureSessionMetadata(isPrivateMatch);

            var payload = new ConnectionPayload {
                partyId = CurrentPartyId,
                isPrivateMatch = isPrivateMatch,
                steamId = LocalIdentity.GetSteamId(),
                ugsPlayerId = LocalIdentity.GetUgsPlayerId(),
                displayName = LocalIdentity.GetDisplayName()
            };

            networkManager.NetworkConfig.ConnectionData = ConnectionPayload.Encode(payload);
        }

        /// <summary>
        /// Shuts down the Netcode network manager.
        /// </summary>
        private async UniTask CleanupNetworkAsync() {
            await LeaveActiveMultiplayerSessionAsync("CleanupNetworkAsync");

            if(TryGetNetworkManager("CleanupNetworkAsync", out var networkManager) == false) return;

            if(networkManager.IsListening || networkManager.ShutdownInProgress) {
                networkManager.Shutdown();

                const int maxWaitFrames = 240;
                var waited = 0;
                while(waited < maxWaitFrames &&
                      networkManager != null &&
                      (networkManager.IsListening || networkManager.ShutdownInProgress)) {
                    waited++;
                    await UniTask.Yield();
                }

                if(networkManager != null && (networkManager.IsListening || networkManager.ShutdownInProgress)) {
                    Debug.LogWarning("[SessionManager] CleanupNetworkAsync timed out waiting for NGO shutdown.");
                }
            }

            // Give transport/internal callbacks one extra frame to settle.
            await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
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

        private static async UniTask TryLeaveVoiceChannelAsync() {
            if(VoiceManager.Instance == null) return;
            if(!VoiceManager.Instance.IsLoggedIn) return;

            try {
                await VoiceManager.Instance.LeaveChannelAsync();
            } catch(Exception ex) {
                Debug.LogWarning($"[SessionManager] Voice leave failed during session transition: {ex.Message}");
            }
        }

        private void TryJoinVoiceForSteamSocialLobby(ulong lobbyId, string context) {
            if(lobbyId == 0) return;
            if(_isLeaving || _isShuttingDown) return;
            if(VoiceManager.Instance == null || !VoiceManager.Instance.IsLoggedIn) return;

            LaunchSessionTask(
                VoiceManager.Instance.EnsureChannelJoinedAsync("match_" + lobbyId, context: context).AsUniTask(),
                $"VoiceJoinSteamSocialLobby/{context}");
            if(Debug.isDebugBuild) {
                Debug.Log($"[SessionManager] Requested voice join for Steam social lobby '{lobbyId}' ({context}).");
            }
        }

        private void TryJoinVoiceForActiveMatch(string context) {
            if(_isLeaving || _isShuttingDown) return;
            if(VoiceManager.Instance == null || !VoiceManager.Instance.IsLoggedIn) return;

            if(!TryGetActiveMatchVoiceChannelName(out var channelName)) {
                if(Debug.isDebugBuild) {
                    Debug.Log($"[SessionManager] No active match channel available for voice join ({context}).");
                }
                return;
            }

            LaunchSessionTask(
                VoiceManager.Instance.EnsureChannelJoinedAsync(channelName, context: context).AsUniTask(),
                $"VoiceJoinMatch/{context}");
            if(Debug.isDebugBuild) {
                Debug.Log($"[SessionManager] Requested voice join for active match channel '{channelName}' ({context}).");
            }
        }

        public bool TryGetActiveVoiceChannelName(out string channelName) {
            return TryGetActiveMatchVoiceChannelName(out channelName);
        }

        private bool TryGetActiveMatchVoiceChannelName(out string channelName) {
            channelName = null;

            if(_ugsMatchLobby != null && string.IsNullOrEmpty(_ugsMatchLobby.Id) == false) {
                channelName = "match_" + _ugsMatchLobby.Id;
                return true;
            }

            if(!CurrentLobby.HasValue || CurrentLobby.Value.Id == 0) return false;
            channelName = "match_" + CurrentLobby.Value.Id;
            return true;

        }

        private static async UniTask<bool> WaitForActiveSceneAsync(string expectedSceneName, float timeoutSeconds,
            CancellationToken cancellationToken) {
            var start = Time.realtimeSinceStartup;
            while(Time.realtimeSinceStartup - start < timeoutSeconds) {
                if(cancellationToken.IsCancellationRequested) {
                    return false;
                }

                var activeScene = SceneManager.GetActiveScene();
                if(activeScene.IsValid() && activeScene.name == expectedSceneName) {
                    return true;
                }

                await UniTask.DelayFrame(1, cancellationToken: cancellationToken);
            }

            return false;
        }

        private static async UniTask<bool> WaitForMainMenuReadyAsync(float timeoutSeconds, CancellationToken cancellationToken) {
            var start = Time.realtimeSinceStartup;
            while(Time.realtimeSinceStartup - start < timeoutSeconds) {
                if(cancellationToken.IsCancellationRequested) {
                    return false;
                }

                if(MainMenuManager.Instance != null) {
                    return true;
                }

                await UniTask.DelayFrame(1, cancellationToken: cancellationToken);
            }

            return false;
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

            // Cancel polling
            if(_matchmakerCts != null) {
                _matchmakerCts.Cancel();
                _matchmakerCts.Dispose();
                _matchmakerCts = null;
            }

            // Delete ticket from server if we have one
            if(!string.IsNullOrEmpty(_matchmakerTicketId)) {
                await DeleteMatchmakerTicketAsync(_matchmakerTicketId);
                _matchmakerTicketId = null;
            }

            _matchmakerQueueName = null;
        }

        /// <summary>
        /// Clears UGS match lobby state to avoid stale data affecting future matches.
        /// </summary>
        private async UniTask ClearMatchStateAsync() {
            if(Debug.isDebugBuild) {
                Debug.Log("[SessionManager] ClearMatchState called");
            }

            // Leave match lobby if we're in one
            var matchLobbyId = _ugsMatchLobby != null ? _ugsMatchLobby.Id : null;
            if(!string.IsNullOrEmpty(matchLobbyId)) {
                await LeaveMatchLobbyAsync(matchLobbyId);
            }

            CompleteAndClearPlayersReadyWaiter(false);
            await UnsubscribeMatchLobbyEventsAsync("ClearMatchStateAsync");
            _ugsMatchLobby = null;
            _ugsSyncInProgress = false;
            _ugsLocalReadySubmitted = false;
            _ugsClientStartedForMatch = false;
            _ugsHostPreFadedOut = false;
            ResetDistributedAuthorityJoinRetryState();
            _lastFailedFollowMatchLobbyId = null;

            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings != null) {
                // Private-match-only runtime override: public matches should keep the normal pre-match flow.
                matchSettings.preMatchCountdownEnabled = true;
                matchSettings.swapWeaponsOnDeath = true;
            }

            UpdateSteamRichPresence();
        }

        private static async UniTask LeaveMatchLobbyAsync(string lobbyId) {
            if(string.IsNullOrEmpty(lobbyId)) return;
            try {
                var localId = AuthenticationService.Instance.PlayerId;
                if(!string.IsNullOrEmpty(localId)) {
                    await LobbyService.Instance.RemovePlayerAsync(lobbyId, localId);
                    if(Debug.isDebugBuild) {
                        Debug.Log($"[SessionManager] Left UGS match lobby '{lobbyId}'");
                    }
                }
            } catch(Exception ex) {
                Debug.LogWarning($"[SessionManager] Failed to leave UGS match lobby '{lobbyId}': {ex.Message}");
            }
        }

        private async UniTask ResetPartyFollowStateIfHostAsync() {
            if(_ugsPartyLobby == null) return;

            try {
                var localId = AuthenticationService.Instance.PlayerId;
                if(string.IsNullOrEmpty(localId)) return;
                if(_ugsPartyLobby.HostId != localId) return;

                var followAlreadyCleared = _ugsPartyLobby.Data != null &&
                                           _ugsPartyLobby.Data.TryGetValue(UgsFollowMatchLobbyIdKey,
                                               out var followObj) &&
                                           (followObj == null || string.IsNullOrEmpty(followObj.Value));

                if(followAlreadyCleared) return;

                var update = new UpdateLobbyOptions {
                    Data = new Dictionary<string, DataObject> {
                        [UgsFollowMatchLobbyIdKey] = new(DataObject.VisibilityOptions.Member, ""),
                        [UgsLobbyStateKey] = new(DataObject.VisibilityOptions.Member, "Party")
                    }
                };

                _ugsPartyLobby = await LobbyService.Instance.UpdateLobbyAsync(_ugsPartyLobby.Id, update);
                _lastFailedFollowMatchLobbyId = null;
                if(Debug.isDebugBuild) {
                    Debug.Log("[SessionManager] Cleared stale followMatchLobbyId on party lobby.");
                }
            } catch(LobbyServiceException ex) when(ex.Reason is LobbyExceptionReason.LobbyNotFound
                                                       or LobbyExceptionReason.EntityNotFound) {
                _ugsPartyLobby = null;
                await UnsubscribePartyLobbyEventsAsync("ResetPartyFollowStateIfHostAsync/LobbyMissing");
            } catch(Exception ex) {
                Debug.LogWarning($"[SessionManager] Failed to clear followMatchLobbyId on party lobby: {ex.Message}");
            }
        }

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
            networkManager.OnClientConnectedCallback += OnClientConnected;
            networkManager.OnClientDisconnectCallback += OnClientDisconnected;
            networkManager.OnClientStopped += OnClientStopped;
            networkManager.OnSessionOwnerPromoted += OnSessionOwnerPromoted;
        }

        private void UnregisterNetworkCallbacks() {
            if(_networkManager == null) return;
            _networkManager.OnClientConnectedCallback -= OnClientConnected;
            _networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
            _networkManager.OnClientStopped -= OnClientStopped;
            _networkManager.OnSessionOwnerPromoted -= OnSessionOwnerPromoted;
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

        private bool TryGetAuthoritativeRuntimeMode(out string mode, out string source) {
            if(_ugsMatchLobby is { Data: not null } &&
               _ugsMatchLobby.Data.TryGetValue(UgsTargetModeKey, out var ugsModeObj) &&
               ugsModeObj != null && !string.IsNullOrEmpty(ugsModeObj.Value)) {
                mode = ugsModeObj.Value;
                source = "UgsMatchLobby";
                return true;
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

        #endregion
    }
}
