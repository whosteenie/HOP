using Game.Player;
using Game.Player.Core;
using Network.Core;
using Network.Session;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Game.Match {
    [DisallowMultipleComponent]
    public class MatchPlayerStateAuthority : NetworkBehaviour {
        public static MatchPlayerStateAuthority Instance { get; private set; }

        [SerializeField] private MatchPlayerStateProxy playerStatePrefab;

        private bool _callbacksRegistered;

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            NetworkAuthority.TryConfigureSessionOwnerObject(this);
            RegisterCallbacks();

            if(NetworkAuthority.HasGlobalAuthority(this)) {
                EnsureAllConnectedPlayerStates();
            }
        }

        public override void OnNetworkDespawn() {
            base.OnNetworkDespawn();
            UnregisterCallbacks();
        }

        public override void OnDestroy() {
            base.OnDestroy();
            UnregisterCallbacks();
            if(Instance == this) {
                Instance = null;
            }
        }

        public MatchPlayerStateProxy GetPlayerState(ulong playerClientId) {
            return MatchPlayerStateProxy.GetForPlayer(playerClientId);
        }

        private MatchPlayerStateProxy EnsurePlayerState(ulong playerClientId) {
            if(MatchPlayerStateProxy.TryGetForPlayer(playerClientId, out var existing)) {
                if(existing != null && existing.NetworkObject != null && existing.NetworkObject.IsSpawned) {
                    return existing;
                }
            }

            if(!NetworkAuthority.HasGlobalAuthority(this)) {
                return existing;
            }

            if(playerStatePrefab == null) {
                Debug.LogError("[MatchPlayerStateAuthority] Player state prefab is not assigned.", this);
                return null;
            }

            var instance = Instantiate(playerStatePrefab);
            instance.name = $"PlayerState_{playerClientId}";
            instance.NetworkObject.Spawn();
            NetworkAuthority.TryConfigureSessionOwnerObject(instance);
            instance.InitializeForPlayer(playerClientId);
            EnsureVisibleToAllClients(instance.NetworkObject);
            return instance;
        }

        private void ApplyIdentityForPlayerOnAuthority(ulong playerClientId, ulong submittedSteamId,
            FixedString128Bytes submittedUgsId, FixedString64Bytes submittedPlayerName) {
            if(!NetworkAuthority.HasGlobalAuthority(this)) {
                return;
            }

            var state = EnsurePlayerState(playerClientId);
            if(state == null) {
                return;
            }

            var resolvedSteamId = submittedSteamId;
            var resolvedUgsId = submittedUgsId.ToString();
            var sessionManager = SessionManager.Instance;
            if(sessionManager != null &&
               !string.IsNullOrWhiteSpace(resolvedUgsId) &&
               sessionManager.TryResolveDistributedAuthorityPlayerMetadata(resolvedUgsId, out _,
                   out var authoritativeSteamId) &&
               authoritativeSteamId != 0) {
                resolvedSteamId = authoritativeSteamId;
            }

            state.steamId.Value = resolvedSteamId;
            state.ugsId.Value = submittedUgsId;

            var displayName = submittedPlayerName.ToString().Trim();
            if(string.IsNullOrWhiteSpace(displayName)) {
                displayName = "Player";
            }

            state.playerName.Value = new FixedString64Bytes(displayName);
        }

        private void RegisterCallbacks() {
            if(_callbacksRegistered || NetworkManager == null) {
                return;
            }

            NetworkManager.OnClientConnectedCallback += OnClientConnected;
            NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
            NetworkManager.OnSessionOwnerPromoted += OnSessionOwnerPromoted;
            _callbacksRegistered = true;
        }

        private void UnregisterCallbacks() {
            if(!_callbacksRegistered || NetworkManager == null) {
                return;
            }

            NetworkManager.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
            NetworkManager.OnSessionOwnerPromoted -= OnSessionOwnerPromoted;
            _callbacksRegistered = false;
        }

        private void OnClientConnected(ulong clientId) {
            if(!NetworkAuthority.HasGlobalAuthority(this)) {
                return;
            }

            EnsurePlayerState(clientId);
        }

        private void OnClientDisconnected(ulong clientId) {
            if(!NetworkAuthority.HasGlobalAuthority(this)) {
                return;
            }

            var state = MatchPlayerStateProxy.GetForPlayer(clientId);
            if(state == null || state.NetworkObject == null || !state.NetworkObject.IsSpawned) {
                return;
            }

            state.NetworkObject.Despawn();
        }

        private void OnSessionOwnerPromoted(ulong _) {
            if(!NetworkAuthority.HasGlobalAuthority(this)) return;
            NetworkAuthority.TryConfigureSessionOwnerObject(this);
            EnsureAllConnectedPlayerStates();
        }

        private void EnsureAllConnectedPlayerStates() {
            if(NetworkManager == null) {
                return;
            }

            foreach(var clientId in NetworkManager.ConnectedClientsIds) {
                EnsurePlayerState(clientId);
            }
        }

        private void EnsureVisibleToAllClients(NetworkObject networkObject) {
            if(networkObject == null || NetworkManager == null) {
                return;
            }

            foreach(var clientId in NetworkManager.ConnectedClientsIds) {
                if(networkObject.IsNetworkVisibleTo(clientId)) {
                    continue;
                }

                networkObject.NetworkShow(clientId);
            }
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void RequestIdentitySyncAuthorityServerRpc(NetworkObjectReference playerRef, ulong submittedSteamId,
            FixedString128Bytes submittedUgsId, FixedString64Bytes submittedPlayerName, RpcParams rpcParams = default) {
            if(!NetworkAuthority.HasGlobalAuthority(this)) {
                return;
            }

            var senderClientId = rpcParams.Receive.SenderClientId;
            if(!playerRef.TryGet(out var playerObject) || playerObject == null) {
                return;
            }

            var player = playerObject.GetComponent<PlayerController>();
            if(player == null || player.OwnerClientId != senderClientId) {
                return;
            }

            ApplyIdentityForPlayerOnAuthority(player.OwnerClientId, submittedSteamId, submittedUgsId,
                submittedPlayerName);
        }
    }
}