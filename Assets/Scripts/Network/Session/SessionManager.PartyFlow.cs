using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Network.Core;
using Network.Diagnostics;
using Network.Singletons;
using Steamworks;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using UnityEngine;

namespace Network.Session {
    public sealed partial class SessionManager {
        private bool _isCreatingPartyLobby;

        #region Party + Match Flow

        /// <summary>
        /// Creates the UGS party lobby and optionally mirrors party context to a Steam social lobby.
        /// </summary>
        /// <param name="maxPlayers">Maximum members allowed in the party lobby.</param>
        /// <param name="isPrivate">Whether the UGS party lobby should be private.</param>
        public async UniTask CreatePartyLobbyAsync(int maxPlayers, bool isPrivate) {
            if(_ugsPartyLobby != null) {
                return;
            }

            if(_isCreatingPartyLobby) {
                var waitStart = Time.realtimeSinceStartup;
                while(_isCreatingPartyLobby && Time.realtimeSinceStartup - waitStart < 5f) {
                    try {
                        await UniTask.DelayFrame(1, cancellationToken: SessionLifetimeToken);
                    } catch(OperationCanceledException) {
                        return;
                    }
                }

                if(_ugsPartyLobby != null) {
                    return;
                }
            }

            _isCreatingPartyLobby = true;
            try {
                await EnsureSignedInAsync();

                if(string.IsNullOrEmpty(CurrentPartyId)) {
                    CurrentPartyId = Guid.NewGuid().ToString();
                }

                var options = new CreateLobbyOptions {
                    IsPrivate = isPrivate,
                    Player = BuildLobbyPlayer(),
                    Data = new Dictionary<string, DataObject> {
                        [UgsPartyIdKey] = new(DataObject.VisibilityOptions.Member, CurrentPartyId),
                        [UgsFollowMatchLobbyIdKey] = new(DataObject.VisibilityOptions.Member, ""),
                        [UgsLobbyStateKey] = new(DataObject.VisibilityOptions.Member, "Party")
                    }
                };

                _ugsPartyLobby = await LobbyService.Instance.CreateLobbyAsync("HOP Party", maxPlayers, options);
                _ugsMatchLobby = null;
                IsPartyLeader = _ugsPartyLobby != null && _ugsPartyLobby.HostId == AuthenticationService.Instance.PlayerId;
                await UnsubscribeMatchLobbyEventsAsync("CreatePartyLobbyAsync/ResetMatch");
                await EnsurePartyLobbyEventsSubscriptionAsync("CreatePartyLobbyAsync");

                if(SteamClient.IsValid && SteamClient.IsLoggedOn) {
                    if(!CurrentLobby.HasValue) {
                        var socialLobbyCreated = await CreateSteamSocialLobbyAsync(maxPlayers);
                        if(!socialLobbyCreated && Debug.isDebugBuild) {
                            Debug.LogWarning("[SessionManager] UGS party created, but Steam social lobby creation failed.");
                        }
                    } else if(CurrentLobby.Value.Owner.Id == SteamClient.SteamId) {
                        CurrentLobby.Value.SetData(SessionManager.PartyIdKey, CurrentPartyId);
                        CurrentLobby.Value.SetData(SessionManager.TargetModeKey, SelectedGameMode);
                        UpdateLocalDisplayNameInLobby();
                    }
                }

                _nextUgsHeartbeatTime = Time.unscaledTime + 1f;
                UpdateSteamRichPresence();
                FlowLog.Emit(FlowEventIds.PartyLifecycle,
                    ("action", "CreateUgsParty"),
                    ("partyId", CurrentPartyId),
                    ("lobbyId", _ugsPartyLobby != null ? _ugsPartyLobby.Id : "null"),
                    ("private", isPrivate),
                    ("maxPlayers", maxPlayers));
            } finally {
                _isCreatingPartyLobby = false;
            }
        }

        private async UniTask JoinPartyLobbyByCodeAsync(string code) {
            await EnsureSignedInAsync();
            if(string.IsNullOrEmpty(code)) return;

            var options = new JoinLobbyByCodeOptions {
                Player = BuildLobbyPlayer()
            };

            _ugsPartyLobby = await LobbyService.Instance.JoinLobbyByCodeAsync(code, options);
            _ugsMatchLobby = null;
            IsPartyLeader = _ugsPartyLobby != null && _ugsPartyLobby.HostId == AuthenticationService.Instance.PlayerId;
            await UnsubscribeMatchLobbyEventsAsync("JoinPartyLobbyByCodeAsync/ResetMatch");
            await EnsurePartyLobbyEventsSubscriptionAsync("JoinPartyLobbyByCodeAsync");

            if(_ugsPartyLobby is { Data: not null }) {
                if(_ugsPartyLobby.Data.TryGetValue(UgsPartyIdKey, out var partyIdObj)) {
                    if(partyIdObj != null && !string.IsNullOrEmpty(partyIdObj.Value)) {
                        CurrentPartyId = partyIdObj.Value;
                    }
                }
            }

            _nextUgsHeartbeatTime = Time.unscaledTime + 1f;
            UpdateSteamRichPresence();
            FlowLog.Emit(FlowEventIds.PartyLifecycle,
                ("action", "JoinUgsParty"),
                ("code", code),
                ("partyId", CurrentPartyId),
                ("lobbyId", _ugsPartyLobby != null ? _ugsPartyLobby.Id : "null"));
        }

        private void SyncPartyIdFromPartyLobby() {
            if(string.IsNullOrEmpty(CurrentPartyId) == false) return;
            if(_ugsPartyLobby?.Data == null) return;

            if(_ugsPartyLobby.Data.TryGetValue(UgsPartyIdKey, out var partyIdObj) == false) return;
            if(partyIdObj == null || string.IsNullOrEmpty(partyIdObj.Value)) return;

            CurrentPartyId = partyIdObj.Value;
        }

        private async UniTask PreFadePrivateHostAsync() {
            // Immediate feedback for the host: start fading out right away.
            // We'll avoid double-fading later when we mark ourselves ready.
            _ugsHostPreFadedOut = false;
            if(SceneTransitionManager.Instance == null) return;

            _ugsHostPreFadedOut = true;
            Phase = SessionPhase.SynchronizingLoad;
            SetFrontStatus(SessionPhase.SynchronizingLoad, "Waiting for party...");
            await SceneTransitionManager.Instance.FadeOutAsync();
        }

        private async UniTask PreFadePublicHostAsync() {
            Phase = SessionPhase.SynchronizingLoad;
            SetFrontStatus(SessionPhase.SynchronizingLoad, "Waiting for players...");

            await FadeOutWithFallbackAsync();
            _ugsHostPreFadedOut = true;
        }

        private async UniTask CreatePrivateMatchLobbyAsync(string mode, int maxPlayers, string joinCode,
            string expectedCsv) {
            var create = BuildPrivateMatchCreateOptions(mode, joinCode, expectedCsv);
            _ugsMatchLobby = await LobbyService.Instance.CreateLobbyAsync("HOP Match", maxPlayers, create);
            await EnsureMatchLobbyEventsSubscriptionAsync("CreatePrivateMatchLobbyAsync");
            TryJoinVoiceForActiveMatch("CreatePrivateMatchLobbyAsync");

            // Tell party members to follow into the match lobby.
            var update = BuildPartyFollowMatchOptions(_ugsMatchLobby.Id);
            _ugsPartyLobby = await LobbyService.Instance.UpdateLobbyAsync(_ugsPartyLobby.Id, update);
            await EnsurePartyLobbyEventsSubscriptionAsync("CreatePrivateMatchLobbyAsync/PartyUpdate");
            UpdateSteamRichPresence();
        }

        private async UniTask CreatePublicMatchLobbyAsHostAsync(string mode, int maxPlayers, string matchId,
            string joinCode) {
            var create = BuildPublicMatchCreateOptions(mode, joinCode, matchId);
            _ugsMatchLobby = await LobbyService.Instance.CreateLobbyAsync("HOP Match", maxPlayers, create);
            await EnsureMatchLobbyEventsSubscriptionAsync("CreatePublicMatchLobbyAsHostAsync");
            TryJoinVoiceForActiveMatch("CreatePublicMatchLobbyAsHostAsync");
            UpdateSteamRichPresence();
            LogPublicLobbySnapshot("CreatePublicMatchLobbyAsHostAsync");
            if(Debug.isDebugBuild) {
                Debug.Log($"[SessionManager] Created UGS lobby in SynchronizingLoad state. lobbyId='{_ugsMatchLobby.Id}'");
            }
            FlowLog.Emit(FlowEventIds.PartyLifecycle,
                ("action", "CreateUgsMatchHost"),
                ("matchId", matchId),
                ("lobbyId", _ugsMatchLobby.Id),
                ("mode", mode),
                ("maxPlayers", maxPlayers));
        }

        /// <summary>
        /// Starts a private match from the current UGS party lobby and drives sync-to-load for all members.
        /// </summary>
        /// <param name="mode">Game mode to apply to the created match lobby.</param>
        /// <param name="maxPlayers">Maximum players for relay allocation and match lobby creation.</param>
        public async UniTask StartPrivateMatchAsync(string mode, int maxPlayers) {
            if(!TryBeginSessionOperation("StartPrivateMatchAsync")) return;
            try {
                await EnsureSignedInAsync();
                if(_ugsPartyLobby == null) return;

                var localUgsId = AuthenticationService.Instance.PlayerId;
                if(string.IsNullOrEmpty(localUgsId)) return;

                if(string.IsNullOrEmpty(mode)) return;

                ApplyRuntimeMode(mode, "UgsPrivateMatchHost");
                FlowLog.Emit(FlowEventIds.QueueStarted,
                    ("mode", mode),
                    ("queue", "PrivateParty"),
                    ("maxPlayers", maxPlayers));

                await PreFadePrivateHostAsync();

                SyncPartyIdFromPartyLobby();

                var expectedPlayers = BuildExpectedPlayerIdsFromPartyLobby(_ugsPartyLobby, localUgsId);
                SetExpectedGamePlayerCount(expectedPlayers.Count, "UgsPrivateMatchHost");
                var expectedCsv = string.Join(",", expectedPlayers);

                // Create relay allocation for host.
                var (alloc, joinCode) = await CreateRelayAllocationWithJoinCodeAsync(maxPlayers);

                // Create match lobby and publish follow target for party members.
                await CreatePrivateMatchLobbyAsync(mode, maxPlayers, joinCode, expectedCsv);

                _ugsSyncInProgress = false;
                _ugsLocalReadySubmitted = false;
                _ugsClientStartedForMatch = false;
                // Keep _ugsHostPreFadedOut as-is so we can skip the second fade in StartMatchSynchronizationAsync.

                // Fade out and mark ourselves ready.
                await StartMatchSynchronizationAsync();

                // Host waits until all expected party members are ready (or are not present).
                if(await WaitForPrivateMatchSyncReadyAsync(expectedPlayers) == false) {
                    await LeaveToMainMenuAsync();
                    return;
                }

                // Signal clients to connect.
                var loadingSceneSet = await TrySetMatchLobbyStateAsync("LoadingScene",
                    DataObject.VisibilityOptions.Member, "StartPrivateMatchAsync");
                if(!loadingSceneSet) {
                    Debug.LogWarning(
                        "[SessionManager] Failed to set private match lobby state to LoadingScene. Clients may remain in sync state.");
                }

                if(await TryStartHostWithRelayAsync(alloc, true, "StartPrivateMatchAsync") == false) {
                    await LeaveToMainMenuAsync();
                    return;
                }

                if(!TryLoadGameplaySceneAsHost("StartPrivateMatchAsync/LoadScene")) {
                    await LeaveToMainMenuAsync();
                }
            } finally {
                EndSessionOperation();
            }
        }

        private async UniTask<bool> JoinMatchLobbyByIdAsync(string lobbyId) {
            await EnsureSignedInAsync();
            if(string.IsNullOrEmpty(lobbyId)) {
                Debug.LogWarning("[SessionManager] JoinMatchLobbyByIdAsync called with an empty lobby id.");
                return false;
            }

            if(Debug.isDebugBuild) {
                Debug.Log($"[SessionManager] JoinMatchLobbyByIdAsync called with lobbyId='{lobbyId}'");
            }

            var options = new JoinLobbyByIdOptions {
                Player = BuildLobbyPlayer()
            };
            Lobby matchLobby;
            try {
                matchLobby = await LobbyService.Instance.JoinLobbyByIdAsync(lobbyId, options);
            } catch(LobbyServiceException ex) when(ex.Reason is LobbyExceptionReason.LobbyNotFound or LobbyExceptionReason.EntityNotFound) {
                Debug.LogWarning($"[SessionManager] Match lobby '{lobbyId}' no longer exists.");
                return false;
            } catch(LobbyServiceException ex) {
                if(Debug.isDebugBuild) {
                    Debug.LogWarning(
                        $"[SessionManager] Failed to join match lobby '{lobbyId}' (reason: {ex.Reason}): {ex.Message}");
                }
                return false;
            } catch(Exception ex) {
                if(Debug.isDebugBuild) {
                    Debug.LogWarning($"[SessionManager] Failed to join match lobby '{lobbyId}': {ex.Message}");
                }
                return false;
            }

            if(matchLobby == null) {
                Debug.LogError("[SessionManager] Failed to join lobby - matchLobby is null");
                return false;
            }

            _ugsMatchLobby = matchLobby;
            await EnsureMatchLobbyEventsSubscriptionAsync("JoinMatchLobbyByIdAsync");
            TryJoinVoiceForActiveMatch("JoinMatchLobbyByIdAsync");
            UpdateSteamRichPresence();
            if(Debug.isDebugBuild) {
                Debug.Log(
                    $"[SessionManager] Successfully joined UGS lobby. hostId='{matchLobby.HostId}', playerCount={matchLobby.Players.Count}");
            }
            FlowLog.Emit(FlowEventIds.PartyLifecycle,
                ("action", "JoinUgsMatchLobby"),
                ("lobbyId", matchLobby.Id),
                ("hostId", matchLobby.HostId),
                ("players", matchLobby.Players != null ? matchLobby.Players.Count : 0));

            HandleMatchLobbySnapshot("JoinMatchLobbyByIdAsync/Initial");
            return true;
        }

        private static Player BuildLobbyPlayer() {
            var pid = AuthenticationService.Instance.PlayerId;
            var data = new Dictionary<string, PlayerDataObject> {
                ["displayName"] = new(PlayerDataObject.VisibilityOptions.Member, LocalIdentity.GetDisplayName())
            };
            var steamId = LocalIdentity.GetSteamId();
            if(steamId != 0) {
                data["steamId"] = new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, steamId.ToString());
            }

            return new Player(pid, data: data);
        }

        #endregion
    }
}
