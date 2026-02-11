using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using Game.Match;
using Game.Menu;
using Game.Social;
using Network.Core;
using Network.Diagnostics;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Matchmaker.Models;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityUtils;

namespace Network {
    public sealed partial class SessionManager {
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

            transport = networkManager.GetComponent<UnityTransport>();
            if(transport != null) {
                return true;
            }

            Debug.LogError($"[SessionManager] UnityTransport missing on NetworkManager during {operationName}.");
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
                var update = new UpdateLobbyOptions {
                    Data = new Dictionary<string, DataObject> {
                        [UgsLobbyStateKey] = new(visibility, lobbyState)
                    }
                };
                _ugsMatchLobby = await LobbyService.Instance.UpdateLobbyAsync(_ugsMatchLobby.Id, update);
                return true;
            } catch(Exception ex) {
                Debug.LogWarning(
                    $"[SessionManager] Failed to set UGS match lobby state to '{lobbyState}' during {context}: {ex.Message}");
                return false;
            }
        }

        private async UniTask<bool> TryStartHostWithRelayAsync(Allocation hostAllocation, bool isPrivateMatch,
            string contextLabel) {
            await CleanupNetworkAsync();

            if(TryGetUnityTransport(contextLabel, out var networkManager, out var utp) == false) {
                return false;
            }

            if(TryApplyRelayToTransport(utp, hostAllocation, null) == false) {
                Debug.LogError($"[SessionManager] Failed to apply relay host allocation during {contextLabel}.");
                return false;
            }

            networkManager.NetworkConfig.NetworkTransport = utp;
            ApplyLocalConnectionPayload(isPrivateMatch);
            if(networkManager.StartHost()) {
                return true;
            }

            Debug.LogError($"[SessionManager] Failed to start host during {contextLabel}.");
            return false;
        }

        private bool TryLoadGameplaySceneAsHost(string contextLabel) {
            if(TryGetNetworkManager(contextLabel, out var networkManager) == false) {
                return false;
            }

            Phase = SessionPhase.LoadingScene;
            networkManager.SceneManager.LoadScene(GameSceneName, LoadSceneMode.Single);
            return true;
        }

        private static UpdatePlayerOptions BuildReadyToLoadUpdatePlayerOptions() {
            return new UpdatePlayerOptions {
                Data = new Dictionary<string, PlayerDataObject> {
                    [UgsMemberReadyKey] = new(PlayerDataObject.VisibilityOptions.Member, "1")
                }
            };
        }

        private CreateLobbyOptions BuildPrivateMatchCreateOptions(string mode, string relayJoinCode,
            string expectedCsv) {
            return new CreateLobbyOptions {
                IsPrivate = true,
                Player = BuildLobbyPlayer(),
                Data = new Dictionary<string, DataObject> {
                    [UgsPartyIdKey] = new(DataObject.VisibilityOptions.Member, CurrentPartyId),
                    [UgsMatchTypeKey] = new(DataObject.VisibilityOptions.Member, "Private"),
                    [UgsTargetModeKey] = new(DataObject.VisibilityOptions.Member, mode),
                    [UgsRelayJoinCodeKey] = new(DataObject.VisibilityOptions.Member, relayJoinCode),
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

        private static CreateLobbyOptions BuildPublicMatchCreateOptions(string mode, string relayJoinCode, string matchId) {
            return new CreateLobbyOptions {
                IsPrivate = false,
                Player = BuildLobbyPlayer(),
                Data = new Dictionary<string, DataObject> {
                    [UgsMatchTypeKey] = new(DataObject.VisibilityOptions.Public, "Public"),
                    [UgsTargetModeKey] = new(DataObject.VisibilityOptions.Public, mode),
                    [UgsRelayJoinCodeKey] = new(DataObject.VisibilityOptions.Member, relayJoinCode),
                    [UgsMatchIdKey] = new(DataObject.VisibilityOptions.Public, matchId, DataObject.IndexOptions.S1),
                    [UgsLobbyStateKey] = new(DataObject.VisibilityOptions.Public, "SynchronizingLoad")
                }
            };
        }

        private static async UniTask<(Allocation allocation, string joinCode)> CreateRelayAllocationWithJoinCodeAsync(
            int maxPlayers) {
            var allocation = await RelayService.Instance.CreateAllocationAsync(maxPlayers - 1);
            var joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
            return (allocation, joinCode);
        }

        private void ApplyLocalConnectionPayload(bool isPrivateMatch) {
            if(TryGetNetworkManager("ApplyLocalConnectionPayload", out var networkManager) == false) return;

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

            VoiceManager.Instance.JoinChannelAsync("match_" + lobbyId).Forget();
            if(Debug.isDebugBuild) {
                Debug.Log($"[SessionManager] Requested voice join for Steam social lobby '{lobbyId}' ({context}).");
            }
        }

        private static async UniTask<bool> WaitForActiveSceneAsync(string expectedSceneName, float timeoutSeconds) {
            var start = Time.realtimeSinceStartup;
            while(Time.realtimeSinceStartup - start < timeoutSeconds) {
                var activeScene = SceneManager.GetActiveScene();
                if(activeScene.IsValid() && activeScene.name == expectedSceneName) {
                    return true;
                }

                await UniTask.Yield();
            }

            return false;
        }

        private static async UniTask<bool> WaitForMainMenuReadyAsync(float timeoutSeconds) {
            var start = Time.realtimeSinceStartup;
            while(Time.realtimeSinceStartup - start < timeoutSeconds) {
                if(FindFirstObjectByType<MainMenuManager>() != null) {
                    return true;
                }

                await UniTask.Yield();
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
        private void ClearMatchmakingState() {
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
                DeleteMatchmakerTicketAsync(_matchmakerTicketId).Forget();
                _matchmakerTicketId = null;
            }

            _matchmakerQueueName = null;
        }

        /// <summary>
        /// Clears UGS match lobby state to avoid stale data affecting future matches.
        /// </summary>
        private void ClearMatchState() {
            if(Debug.isDebugBuild) {
                Debug.Log("[SessionManager] ClearMatchState called");
            }

            // Leave match lobby if we're in one
            var matchLobbyId = _ugsMatchLobby != null ? _ugsMatchLobby.Id : null;
            if(!string.IsNullOrEmpty(matchLobbyId)) {
                LeaveMatchLobbyAsync(matchLobbyId).Forget();
            }

            _ugsMatchLobby = null;
            _ugsSyncInProgress = false;
            _ugsLocalReadySubmitted = false;
            _ugsClientStartedForMatch = false;
            _ugsHostPreFadedOut = false;
            _lastFailedFollowMatchLobbyId = null;
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
            if(FrontStatusChanged != null) {
                FrontStatusChanged.Invoke(message);
            }
        }

        private void RegisterNetworkCallbacks() {
            if(TryGetNetworkManager("RegisterNetworkCallbacks", out var networkManager) == false) return;
            networkManager.OnClientConnectedCallback += OnClientConnected;
            networkManager.OnClientDisconnectCallback += OnClientDisconnected;
        }

        private void UnregisterNetworkCallbacks() {
            if(_networkManager == null) return;
            _networkManager.OnClientConnectedCallback -= OnClientConnected;
            _networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
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

            if(changed && refreshUi && FrontStatusChanged != null) {
                FrontStatusChanged.Invoke(null);
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
