using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Diagnostics;
using Network.Contracts;
using Network.Core;
using Unity.Netcode;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Network.Session {
    /// <summary>
    /// Owns distributed authority (DA) create/join, match-lobby refresh after host change, and network cleanup.
    /// Shuts down Netcode/UGS session and NGO: leave DA session, then shutdown NetworkManager.
    /// </summary>
    public static class SessionNetworkLifecycle {
        // Game-provided identity hooks. Until wired by a game adapter, these
        // return placeholder values (0 / empty / "Player") so the network layer
        // can run without depending directly on Game.Social.LocalIdentity.
        public static Func<ulong> GetSteamIdProvider { get; set; } = () => 0UL;
        public static Func<string> GetUgsPlayerIdProvider { get; set; } = () => "";
        public static Func<string> GetDisplayNameProvider { get; set; } = () => "Player";
        private const int ShutdownMaxWaitFrames = 240;
        private const string MultiplayerSessionType = "HOP.Match";
        private const string MultiplayerSessionModeKey = "mode";
        private const string MultiplayerSessionMatchTypeKey = "matchType";
        private const string MultiplayerSessionPartyIdKey = "partyId";
        private const string MultiplayerSessionSteamIdKey = "steamId";

        private const float DistributedAuthorityJoinRetryBaseDelaySeconds = 2f;
        private const float DistributedAuthorityJoinRetryMaxDelaySeconds = 20f;
        private const int DistributedAuthorityJoinRetryMaxExponent = 4;
        private const float DistributedAuthorityStartupDisconnectGraceSeconds = 8f;

        private static int distributedAuthorityStartupDepth;
        private static float distributedAuthorityStartupUntilTime;

        private static ISession activeSession;
        private static Action<string> onHostChanged;
        private static Action onMigrated;
        private static Action onRemoved;
        private static Action onDeleted;

        /// <summary>Current bound DA session, or null.</summary>
        public static ISession GetActiveSession() => activeSession;

        /// <summary>True when a DA session is bound (for disconnect handling).</summary>
        public static bool HasActiveSession => activeSession != null;

        /// <summary>Binds a DA session, subscribes to its events, and publishes SessionJoinedEvent. Call after create/join.</summary>
        public static void BindActiveSession(ISession session, ISessionContext ctx, IDistributedAuthorityActions daActions, Func<bool> isInGameplayAndListening) {
            UnbindActiveSession();
            if(session == null) return;
            activeSession = session;
            onHostChanged = newHostId => OnActiveSessionHostChanged(ctx, daActions, session, newHostId);
            onMigrated = () => OnActiveSessionMigrated(ctx, daActions, session, isInGameplayAndListening);
            onRemoved = () => {
                if(Debug.isDebugBuild) DevLog.LogWarning("[SessionManager] Removed from active DA session.");
                UnbindActiveSession();
            };
            onDeleted = () => {
                if(Debug.isDebugBuild) DevLog.LogWarning("[SessionManager] Active DA session was deleted.");
                UnbindActiveSession();
            };
            session.SessionHostChanged += onHostChanged;
            session.SessionMigrated += onMigrated;
            session.RemovedFromSession += onRemoved;
            session.Deleted += onDeleted;
            if(Debug.isDebugBuild)
                DevLog.Log($"[SessionManager] Bound DA session id='{session.Id}' code='{session.Code}' host='{session.Host}'.");
        }

        /// <summary>Unbinds the current DA session and unsubscribes from its events.</summary>
        public static void UnbindActiveSession() {
            var session = activeSession;
            if(session == null) return;
            if(onHostChanged != null) { session.SessionHostChanged -= onHostChanged; onHostChanged = null; }
            if(onMigrated != null) { session.SessionMigrated -= onMigrated; onMigrated = null; }
            if(onRemoved != null) { session.RemovedFromSession -= onRemoved; onRemoved = null; }
            if(onDeleted != null) { session.Deleted -= onDeleted; onDeleted = null; }
            activeSession = null;
        }

        /// <summary>True while DA create/join is in progress; used to suppress disconnect handling during the grace window.</summary>
        public static bool IsDaStartupInFlight =>
            distributedAuthorityStartupDepth > 0 && Time.unscaledTime <= distributedAuthorityStartupUntilTime;

        private static void BeginDaStartupWindow(string contextLabel) {
            distributedAuthorityStartupDepth = Math.Max(0, distributedAuthorityStartupDepth) + 1;
            distributedAuthorityStartupUntilTime = Time.unscaledTime + DistributedAuthorityStartupDisconnectGraceSeconds;
            if(Debug.isDebugBuild)
                DevLog.Log($"[SessionManager] DA startup window begin ({contextLabel}). depth={distributedAuthorityStartupDepth}");
        }

        private static void EndDaStartupWindow(string contextLabel) {
            distributedAuthorityStartupDepth = Math.Max(0, distributedAuthorityStartupDepth - 1);
            if(distributedAuthorityStartupDepth == 0)
                distributedAuthorityStartupUntilTime = 0f;
            if(Debug.isDebugBuild)
                DevLog.Log($"[SessionManager] DA startup window end ({contextLabel}). depth={distributedAuthorityStartupDepth}");
        }

        /// <summary>Applies connection payload and session metadata to the NetworkManager. Call after cleanup, before create/join.</summary>
        public static void ApplyLocalConnectionPayload(ISessionContext ctx, bool isPrivateMatch) {
            if(ctx == null || !ctx.TryGetNetworkManager("ApplyLocalConnectionPayload", out var networkManager))
                return;
            var payload = new ConnectionPayload {
                partyId = ctx.CurrentPartyId ?? "",
                isPrivateMatch = isPrivateMatch,
                steamId = GetSteamIdProvider != null ? GetSteamIdProvider() : 0UL,
                ugsPlayerId = GetUgsPlayerIdProvider != null ? GetUgsPlayerIdProvider() : "",
                displayName = GetDisplayNameProvider != null ? GetDisplayNameProvider() : "Player"
            };
            networkManager.NetworkConfig.ConnectionData = ConnectionPayload.Encode(payload);
        }

        public static float ComputeDaJoinRetryDelaySeconds(int attempt) {
            var exponent = Mathf.Clamp(attempt, 0, DistributedAuthorityJoinRetryMaxExponent);
            var rawBackoff = DistributedAuthorityJoinRetryBaseDelaySeconds * Mathf.Pow(2f, exponent);
            var jitter = UnityEngine.Random.Range(0f, 1.25f);
            return Mathf.Min(DistributedAuthorityJoinRetryMaxDelaySeconds, rawBackoff + jitter);
        }

        /// <summary>
        /// Leaves the active UGS multiplayer session and shuts down the Netcode NetworkManager.
        /// </summary>
        public static async UniTask CleanupNetworkAsync(ISessionContext ctx, INetworkLifecycleActions actions) {
            await actions.LeaveActiveSessionAsync("CleanupNetworkAsync");

            if(ctx.TryGetNetworkManager("CleanupNetworkAsync", out var networkManager) == false) return;

            if(networkManager.IsListening || networkManager.ShutdownInProgress) {
                networkManager.Shutdown();

                var waited = 0;
                while(waited < ShutdownMaxWaitFrames &&
                      networkManager != null &&
                      (networkManager.IsListening || networkManager.ShutdownInProgress)) {
                    waited++;
                    await UniTask.Yield();
                }

                if(networkManager != null && (networkManager.IsListening || networkManager.ShutdownInProgress)) {
                    DevLog.LogWarning("[SessionManager] CleanupNetworkAsync timed out waiting for NGO shutdown.");
                }
            }

            await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
        }

        #region Distributed Authority create / join / refresh

        /// <summary>
        /// Creates a DA session, cleans up first, applies connection payload, binds session via actions.
        /// </summary>
        public static async UniTask<string> CreateDaSessionAsync(
            ISessionContext ctx,
            IDistributedAuthorityActions daActions,
            INetworkLifecycleActions lifecycleActions,
            int maxPlayers,
            bool isPrivateMatch,
            string contextLabel) {
            BeginDaStartupWindow(contextLabel);
            try {
                await lifecycleActions.CleanupNetworkAsync();
                ApplyLocalConnectionPayload(ctx, isPrivateMatch);

                const int maxAttempts = 2;
                for(var attempt = 1; attempt <= maxAttempts; attempt++) {
                    try {
                        var options = BuildDaSessionOptions(ctx, maxPlayers, isPrivateMatch);
                        var hostSession = await MultiplayerService.Instance.CreateSessionAsync(options);
                        daActions.BindActiveSession(hostSession);
                        return hostSession.Code;
                    } catch(Exception ex) when(attempt < maxAttempts && IsRetryableDaStartupException(ex)) {
                        DevLog.LogWarning(
                            $"[SessionManager] DA create canceled during {contextLabel} (attempt {attempt}/{maxAttempts}). Retrying...");
                        await UniTask.Delay(350, cancellationToken: ctx.SessionLifetimeToken);
                    } catch(Exception ex) {
                        DevLog.LogError($"[SessionManager] Failed to create DA session during {contextLabel}: {ex}");
                        daActions.UnbindActiveSession();
                        return null;
                    }
                }

                return null;
            } finally {
                EndDaStartupWindow(contextLabel);
            }
        }

        /// <summary>
        /// Joins a DA session by code, cleans up first, applies connection payload, binds session via actions.
        /// </summary>
        public static async UniTask<DaSessionJoinResult> JoinDaSessionAsync(
            string sessionCode,
            bool isPrivateMatch,
            ISessionContext ctx,
            IDistributedAuthorityActions daActions,
            INetworkLifecycleActions lifecycleActions,
            string contextLabel) {
            if(string.IsNullOrWhiteSpace(sessionCode)) {
                DevLog.LogError($"[SessionManager] Cannot join DA session during {contextLabel}: session code is empty.");
                return DaSessionJoinResult.Failed;
            }

            BeginDaStartupWindow(contextLabel);
            try {
                await lifecycleActions.CleanupNetworkAsync();
                ApplyLocalConnectionPayload(ctx, isPrivateMatch);

                const int maxAttempts = 2;
                for(var attempt = 1; attempt <= maxAttempts; attempt++) {
                    try {
                        var joinOptions = BuildJoinSessionOptions(ctx);
                        var session = await MultiplayerService.Instance.JoinSessionByCodeAsync(sessionCode, joinOptions);
                        daActions.BindActiveSession(session);
                        return DaSessionJoinResult.Success;
                    } catch(SessionException ex) when(ex.Error == SessionError.RateLimitExceeded) {
                        DevLog.LogWarning(
                            $"[SessionManager] Rate limited joining DA session '{sessionCode}' during {contextLabel}. Retrying...");
                        daActions.UnbindActiveSession();
                        return DaSessionJoinResult.RateLimited;
                    } catch(Exception ex) when(attempt < maxAttempts && IsRetryableDaStartupException(ex)) {
                        DevLog.LogWarning(
                            $"[SessionManager] DA join canceled for code '{sessionCode}' during {contextLabel} (attempt {attempt}/{maxAttempts}). Retrying...");
                        await UniTask.Delay(350, cancellationToken: ctx.SessionLifetimeToken);
                    } catch(Exception ex) {
                        DevLog.LogError(
                            $"[SessionManager] Failed to join DA session '{sessionCode}' during {contextLabel}: {ex}");
                        daActions.UnbindActiveSession();
                        return DaSessionJoinResult.Failed;
                    }
                }

                return DaSessionJoinResult.Failed;
            } finally {
                EndDaStartupWindow(contextLabel);
            }
        }

        private static JoinSessionOptions BuildJoinSessionOptions(ISessionContext ctx) {
            var displayName = GetDisplayNameProvider != null ? GetDisplayNameProvider() : "Player";
            var steamId = GetSteamIdProvider != null ? GetSteamIdProvider() : 0UL;

            var joinOptions = new JoinSessionOptions {
                Type = MultiplayerSessionType,
                PlayerProperties = new Dictionary<string, PlayerProperty> {
                    ["displayName"] = new(displayName, VisibilityPropertyOptions.Member)
                }
            };
            if(!string.IsNullOrEmpty(ctx.CurrentPartyId)) {
                joinOptions.PlayerProperties[MultiplayerSessionPartyIdKey] =
                    new PlayerProperty(ctx.CurrentPartyId, VisibilityPropertyOptions.Member);
            }

            if(steamId != 0) {
                joinOptions.PlayerProperties[MultiplayerSessionSteamIdKey] =
                    new PlayerProperty(steamId.ToString(), VisibilityPropertyOptions.Member);
            }

            return joinOptions;
        }

        /// <summary>
        /// Builds SessionOptions for DA create (name, max players, session/player properties, DA network).
        /// </summary>
        private static SessionOptions BuildDaSessionOptions(ISessionContext ctx, int maxPlayers, bool isPrivateMatch) {
            var displayName = GetDisplayNameProvider != null ? GetDisplayNameProvider() : "Player";
            var steamId = GetSteamIdProvider != null ? GetSteamIdProvider() : 0UL;

            var options = new SessionOptions {
                Name = $"HOP {ctx.SelectedGameMode}",
                MaxPlayers = Mathf.Max(1, maxPlayers),
                IsPrivate = isPrivateMatch,
                Type = MultiplayerSessionType,
                SessionProperties = new Dictionary<string, SessionProperty> {
                    [MultiplayerSessionModeKey] = new(ctx.SelectedGameMode ?? string.Empty),
                    [MultiplayerSessionMatchTypeKey] = new(isPrivateMatch ? "Private" : "Public")
                },
                PlayerProperties = new Dictionary<string, PlayerProperty> {
                    ["displayName"] = new(displayName, VisibilityPropertyOptions.Member)
                }
            };

            if(!string.IsNullOrEmpty(ctx.CurrentPartyId)) {
                options.PlayerProperties[MultiplayerSessionPartyIdKey] =
                    new PlayerProperty(ctx.CurrentPartyId, VisibilityPropertyOptions.Member);
            }

            if(steamId != 0) {
                options.PlayerProperties[MultiplayerSessionSteamIdKey] =
                    new PlayerProperty(steamId.ToString(), VisibilityPropertyOptions.Member);
            }

            return options.WithDistributedAuthorityNetwork();
        }

        /// <summary>
        /// Resolves partyId and steamId for a UGS player from DA session players, then match lobby, then party lobby.
        /// </summary>
        private static bool TryResolveDaPlayerMetadata(
            IReadOnlyList<IReadOnlyPlayer> sessionPlayers,
            IReadOnlyList<Player> matchLobbyPlayers,
            IReadOnlyList<Player> partyLobbyPlayers,
            string ugsPlayerId,
            out string partyId,
            out ulong steamId) {
            partyId = null;
            steamId = 0;

            if(string.IsNullOrWhiteSpace(ugsPlayerId))
                return false;

            if(TryResolveFromSessionPlayers(sessionPlayers, ugsPlayerId, out partyId, out steamId))
                return true;
            return TryResolveFromLobbyPlayers(matchLobbyPlayers, ugsPlayerId, out partyId, out steamId) || TryResolveFromLobbyPlayers(partyLobbyPlayers, ugsPlayerId, out partyId, out steamId);
        }

        /// <summary>
        /// Resolves partyId and steamId using the currently bound DA session, then match lobby, then party lobby.
        /// </summary>
        public static bool TryResolveDaPlayerMetadata(
            IReadOnlyList<Player> matchLobbyPlayers,
            IReadOnlyList<Player> partyLobbyPlayers,
            string ugsPlayerId,
            out string partyId,
            out ulong steamId) =>
            TryResolveDaPlayerMetadata(activeSession?.Players, matchLobbyPlayers, partyLobbyPlayers, ugsPlayerId, out partyId, out steamId);

        private static bool TryResolveFromSessionPlayers(
            IReadOnlyList<IReadOnlyPlayer> players, string ugsPlayerId, out string partyId, out ulong steamId) {
            partyId = null;
            steamId = 0;
            if(players == null) return false;

            foreach(var player in players) {
                if(player == null || !string.Equals(player.Id, ugsPlayerId, StringComparison.Ordinal))
                    continue;
                if(player.Properties == null) return true;
                if(player.Properties.TryGetValue(MultiplayerSessionPartyIdKey, out var partyProperty) &&
                   partyProperty != null && !string.IsNullOrWhiteSpace(partyProperty.Value))
                    partyId = partyProperty.Value;
                if(player.Properties.TryGetValue(MultiplayerSessionSteamIdKey, out var steamProperty) &&
                   steamProperty != null)
                    ulong.TryParse(steamProperty.Value, out steamId);
                return true;
            }
            return false;
        }

        private static bool TryResolveFromLobbyPlayers(
            IReadOnlyList<Player> players, string ugsPlayerId, out string partyId, out ulong steamId) {
            partyId = null;
            steamId = 0;
            if(players == null) return false;

            foreach(var player in players) {
                if(player == null || !string.Equals(player.Id, ugsPlayerId, StringComparison.Ordinal))
                    continue;
                if(player.Data == null) return true;
                if(player.Data.TryGetValue(MultiplayerSessionPartyIdKey, out var partyProperty) &&
                   partyProperty != null && !string.IsNullOrWhiteSpace(partyProperty.Value))
                    partyId = partyProperty.Value;
                if(player.Data.TryGetValue(MultiplayerSessionSteamIdKey, out var steamProperty) &&
                   steamProperty != null)
                    ulong.TryParse(steamProperty.Value, out steamId);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Refreshes match lobby after DA host change; updates context and notifies when local player is promoted to host.
        /// </summary>
        private static async UniTask RefreshMatchLobbyAfterHostChangeAsync(
            ISessionContext ctx,
            IDistributedAuthorityActions daActions,
            string reason,
            string expectedHostId) {
            if(ctx.IsLeaving || ctx.IsShuttingDown || ctx.UgsMatchLobby == null || string.IsNullOrEmpty(ctx.UgsMatchLobby.Id)) {
                return;
            }

            var targetLobbyId = ctx.UgsMatchLobby.Id;
            var previousHostId = ctx.UgsMatchLobby.HostId;
            const int maxAttempts = 3;
            var nextDelayMs = 250;

            for(var attempt = 1; attempt <= maxAttempts; attempt++) {
                try {
                    await UniTask.Delay(nextDelayMs, cancellationToken: ctx.SessionLifetimeToken);
                } catch(OperationCanceledException) {
                    return;
                }

                if(ctx.IsLeaving || ctx.IsShuttingDown || ctx.UgsMatchLobby == null ||
                   !string.Equals(ctx.UgsMatchLobby.Id, targetLobbyId, StringComparison.Ordinal)) {
                    return;
                }

                try {
                    var refreshedLobby = await LobbyService.Instance.GetLobbyAsync(targetLobbyId);
                    if(refreshedLobby == null) continue;

                    ctx.SetUgsMatchLobby(refreshedLobby);

                    if(Debug.isDebugBuild) {
                        DevLog.Log(
                            $"[SessionManager] Refreshed match lobby after DA {reason}. lobbyId='{targetLobbyId}' hostId='{refreshedLobby.HostId}' prevHostId='{previousHostId}' attempt={attempt}/{maxAttempts}");
                    }

                    var hostMatchesExpectation = string.IsNullOrEmpty(expectedHostId) ||
                                                string.Equals(refreshedLobby.HostId, expectedHostId, StringComparison.Ordinal);
                    if(daActions.IsLocalPlayerMatchLobbyHost(refreshedLobby)) {
                        daActions.OnPromotedToMatchHost();
                        if(Debug.isDebugBuild) {
                            DevLog.Log(
                                $"[SessionManager] Local player now owns match lobby heartbeats after DA {reason}. lobbyId='{targetLobbyId}'.");
                        }
                    }

                    if(hostMatchesExpectation || daActions.IsLocalPlayerMatchLobbyHost(refreshedLobby)) return;

                    previousHostId = refreshedLobby.HostId;
                } catch(LobbyServiceException ex) when(ex.Reason is LobbyExceptionReason.LobbyNotFound or LobbyExceptionReason.EntityNotFound) {
                    if(Debug.isDebugBuild) {
                        DevLog.LogWarning(
                            $"[SessionManager] Match lobby '{targetLobbyId}' no longer exists while refreshing after DA {reason}.");
                    }
                    return;
                } catch(LobbyServiceException ex) when(ex.Reason == LobbyExceptionReason.RateLimited) {
                    nextDelayMs = Mathf.Min(4000, nextDelayMs * 2);
                    if(Debug.isDebugBuild) {
                        DevLog.Log(
                            $"[SessionManager] Match lobby refresh after DA {reason} was rate-limited (attempt {attempt}/{maxAttempts}). Backing off for {nextDelayMs}ms.");
                    }
                } catch(Exception ex) {
                    nextDelayMs = Mathf.Max(nextDelayMs, 500);
                    if(Debug.isDebugBuild) {
                        DevLog.LogWarning(
                            $"[SessionManager] Failed to refresh match lobby after DA {reason} (attempt {attempt}/{maxAttempts}): {ex.Message}");
                    }
                }
            }
        }

        private static bool IsRetryableDaStartupException(Exception ex) {
            switch(ex) {
                case null:
                    return false;
                case TaskCanceledException or OperationCanceledException:
                case SessionException { Error: SessionError.NetworkSetupFailed } sessionEx when ContainsCancellationException(sessionEx):
                    return true;
                default:
                    return ContainsCancellationException(ex);
            }
        }

        private static bool ContainsCancellationException(Exception ex) {
            for(var current = ex; current != null; current = current.InnerException) {
                if(current is TaskCanceledException or OperationCanceledException) return true;
            }
            return false;
        }

        /// <summary>Refreshes the active DA session after a delay. Call from DA event handlers.</summary>
        private static async UniTask RefreshActiveSessionAsync(ISessionContext ctx, ISession session, string reason) {
            if(session == null || ctx.IsLeaving || ctx.IsShuttingDown) return;
            try {
                await UniTask.Delay(250, cancellationToken: ctx.SessionLifetimeToken);
                if(ctx.IsLeaving || ctx.IsShuttingDown) return;
                await session.RefreshAsync();
            } catch(Exception ex) {
                if(Debug.isDebugBuild)
                    DevLog.LogWarning(
                        $"[SessionManager] Failed to refresh active DA session after {reason}: {ex.Message}");
            }
        }

        /// <summary>Handles DA session host changed: refresh session, refresh match lobby, notify party.</summary>
        private static void OnActiveSessionHostChanged(ISessionContext ctx, IDistributedAuthorityActions daActions,
            ISession session, string newHostId) {
            if(Debug.isDebugBuild)
                DevLog.Log($"[SessionManager] DA session host changed to '{newHostId}'.");
            ctx.LaunchSessionTask(RefreshActiveSessionAsync(ctx, session, "HostChanged"),
                "DistributedAuthority/RefreshHostChanged");
            ctx.LaunchSessionTask(
                RefreshMatchLobbyAfterHostChangeAsync(ctx, daActions, "HostChanged", newHostId),
                "DistributedAuthority/RefreshMatchLobbyHostChanged");
            ctx.NotifyPartyStateChanged();
        }

        /// <summary>Handles DA session migrated: refresh session, refresh match lobby, set InGame if in gameplay scene.</summary>
        private static void OnActiveSessionMigrated(ISessionContext ctx, IDistributedAuthorityActions daActions,
            ISession session, Func<bool> isInGameplayAndListening) {
            if(Debug.isDebugBuild)
                DevLog.Log("[SessionManager] DA session migration completed.");
            ctx.LaunchSessionTask(RefreshActiveSessionAsync(ctx, session, "Migrated"),
                "DistributedAuthority/RefreshMigrated");
            ctx.LaunchSessionTask(
                RefreshMatchLobbyAfterHostChangeAsync(ctx, daActions, "Migrated", null),
                "DistributedAuthority/RefreshMatchLobbyMigrated");
            if(isInGameplayAndListening != null && isInGameplayAndListening())
                ctx.SetFrontStatus(SessionPhase.InGame, "");
            ctx.NotifyPartyStateChanged();
        }

        /// <summary>Leaves the given DA session (call after unbinding). Used by manager's LeaveActiveSessionAsync.</summary>
        public static async UniTask LeaveSessionAsync(ISession session, string contextLabel) {
            if(session == null) return;
            try {
                await session.LeaveAsync();
                if(Debug.isDebugBuild)
                    DevLog.Log($"[SessionManager] Left DA session during {contextLabel}.");
            } catch(Exception ex) {
                DevLog.LogWarning($"[SessionManager] Failed to leave DA session during {contextLabel}: {ex.Message}");
            }
        }

        #endregion

        #region NGO network callbacks and disconnect handling

        private static NetworkManager registeredNetworkManager;
        private static Action<ulong> onClientConnected;
        private static Action<ulong> onClientDisconnected;
        private static Action<bool> onClientStopped;
        private static NetworkManager.OnSessionOwnerPromotedDelegateHandler onSessionOwnerPromoted;
        
        /// <summary>
        /// Runs the same logic as the OnClientStopped callback. Used by the registered delegate and by PlayMode tests.
        /// </summary>
        private static void RunOnClientStoppedLogic(
            ISessionContext ctx,
            NetworkManager networkManager,
            Func<bool> hasActiveSession,
            Action<string> triggerUnexpectedDisconnect) {
            if(ctx.IsExpectedDisconnect || ctx.IsLeaving) return;
            if(IsDaStartupInFlight) {
                if(Debug.isDebugBuild)
                    DevLog.Log("[SessionManager] Ignoring client-stopped callback during DA startup window.");
                return;
            }
            if(networkManager != null && networkManager.IsListening) return;
            if(networkManager != null && hasActiveSession()) {
                ctx.LaunchSessionTask(
                    VerifyDaStopAsync(ctx, networkManager, () =>
                        triggerUnexpectedDisconnect("OnClientStopped/DistributedAuthority")),
                    "DistributedAuthority/VerifyClientStopped");
                return;
            }
            DevLog.Log("[SessionManager] Client stopped unexpectedly. Sending to main menu.");
            triggerUnexpectedDisconnect("OnClientStopped");
        }

        /// <summary>
        /// Subscribes to NGO NetworkManager callbacks for client connect/disconnect, client stopped, and session owner promoted.
        /// Call from SessionManager OnEnable after resolving NetworkManager.
        /// </summary>
        public static void RegisterNetworkCallbacks(
            NetworkManager networkManager,
            ISessionContext ctx,
            ISceneFlowActions sceneActions,
            Func<bool> hasActiveSession,
            Action<string> triggerUnexpectedDisconnect) {
            if(networkManager == null) return;
            if(registeredNetworkManager != null && !ReferenceEquals(registeredNetworkManager, networkManager)) {
                UnregisterNetworkCallbacks(registeredNetworkManager);
            }

            registeredNetworkManager = networkManager;

            onClientConnected = _ => {
                if(!NetworkAuthority.HasGlobalAuthority(networkManager)) return;
                ctx.NotifyPartyStateChanged();
            };

            onClientDisconnected = clientId => {
                if(clientId != networkManager.LocalClientId) {
                    ctx.NotifyPartyStateChanged();
                    return;
                }
                if(IsDaStartupInFlight) {
                    if(Debug.isDebugBuild)
                        DevLog.Log("[SessionManager] Ignoring local disconnect during DA startup window.");
                    ctx.NotifyPartyStateChanged();
                    return;
                }
                if(!ctx.IsExpectedDisconnect) {
                    DevLog.Log("[SessionManager] Unexpected local disconnect.");
                    triggerUnexpectedDisconnect("OnClientDisconnected");
                } else {
                    ctx.SetIsExpectedDisconnect(false);
                }
                ctx.NotifyPartyStateChanged();
            };

            onClientStopped = _ => RunOnClientStoppedLogic(ctx, networkManager, hasActiveSession, triggerUnexpectedDisconnect);

            onSessionOwnerPromoted = sessionOwnerPromoted => {
                if(networkManager == null || !networkManager.DistributedAuthorityMode) return;
                if(Debug.isDebugBuild) {
                    DevLog.Log(
                        $"[SessionManager] Session owner promoted to client {sessionOwnerPromoted}. LocalSessionOwner={networkManager.LocalClient is { IsSessionOwner: true }}");
                }
                if(networkManager.IsListening && !ctx.IsLeaving && !ctx.IsShuttingDown &&
                   sceneActions.IsGameplaySceneName(SceneManager.GetActiveScene().name)) {
                    ctx.SetFrontStatus(SessionPhase.InGame, "");
                }
                ctx.NotifyPartyStateChanged();
            };

            networkManager.OnClientConnectedCallback += onClientConnected;
            networkManager.OnClientDisconnectCallback += onClientDisconnected;
            networkManager.OnClientStopped += onClientStopped;
            networkManager.OnSessionOwnerPromoted += onSessionOwnerPromoted;
        }

        /// <summary>
        /// Unsubscribes from NGO callbacks. Call from SessionManager OnDisable.
        /// </summary>
        public static void UnregisterNetworkCallbacks(NetworkManager networkManager) {
            if(networkManager == null) return;
            if(onClientConnected != null) {
                networkManager.OnClientConnectedCallback -= onClientConnected;
                networkManager.OnClientDisconnectCallback -= onClientDisconnected;
                networkManager.OnClientStopped -= onClientStopped;
                networkManager.OnSessionOwnerPromoted -= onSessionOwnerPromoted;
                onClientConnected = null;
                onClientDisconnected = null;
                onClientStopped = null;
                onSessionOwnerPromoted = null;
            }
            if(ReferenceEquals(registeredNetworkManager, networkManager))
                registeredNetworkManager = null;
        }

        private static async UniTask VerifyDaStopAsync(
            ISessionContext ctx,
            NetworkManager networkManager,
            Action triggerUnexpectedDisconnect) {
            await UniTask.Delay(TimeSpan.FromSeconds(3), cancellationToken: ctx.SessionLifetimeToken);

            if(ctx.IsExpectedDisconnect || ctx.IsLeaving || ctx.IsShuttingDown) return;
            if(networkManager != null && networkManager.IsListening) {
                if(Debug.isDebugBuild)
                    DevLog.Log("[SessionManager] DA client stop recovered during grace period.");
                return;
            }
            DevLog.Log(
                "[SessionManager] DA client remained stopped after migration grace period. Sending to main menu.");
            triggerUnexpectedDisconnect();
        }

        #endregion
    }
}
