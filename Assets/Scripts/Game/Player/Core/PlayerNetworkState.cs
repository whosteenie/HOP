using System.Collections;
using Game.Match;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player.Core {
    internal sealed class PlayerNetworkState {
        private readonly PlayerController _player;
        private MatchPlayerStateProxy _cachedPlayerState;
        private MatchPlayerStateProxy _boundPlayerState;
        private Coroutine _identitySyncRoutine;
        private bool _identitySyncCompleted;

        public PlayerNetworkState(PlayerController player) {
            _player = player;
        }

        public MatchPlayerStateProxy PlayerState => ResolvePlayerState();

        public void Subscribe() {
            MatchPlayerStateProxy.StateRegistered -= OnPlayerStateRegistered;
            MatchPlayerStateProxy.StateRegistered += OnPlayerStateRegistered;
            MatchPlayerStateProxy.StateUnregistered -= OnPlayerStateUnregistered;
            MatchPlayerStateProxy.StateUnregistered += OnPlayerStateUnregistered;
            TryBindStateSubscriptions();
        }

        public void Unsubscribe() {
            MatchPlayerStateProxy.StateRegistered -= OnPlayerStateRegistered;
            MatchPlayerStateProxy.StateUnregistered -= OnPlayerStateUnregistered;
            UnbindStateSubscriptions();
        }

        public MatchPlayerStateProxy ResolvePlayerState() {
            if(_cachedPlayerState != null &&
               _cachedPlayerState.RepresentedClientId == _player.OwnerClientId &&
               _cachedPlayerState.NetworkObject != null &&
               _cachedPlayerState.NetworkObject.IsSpawned) {
                return _cachedPlayerState;
            }

            _cachedPlayerState = MatchPlayerStateProxy.GetForPlayer(_player.OwnerClientId);
            return _cachedPlayerState;
        }

        private void OnPlayerStateRegistered(ulong playerClientId, MatchPlayerStateProxy proxy) {
            if(playerClientId != _player.OwnerClientId) return;
            _cachedPlayerState = proxy;
            TryBindStateSubscriptions();
        }

        private void OnPlayerStateUnregistered(ulong playerClientId, MatchPlayerStateProxy proxy) {
            if(playerClientId != _player.OwnerClientId) return;

            if(_boundPlayerState == proxy) {
                UnbindStateSubscriptions();
            }

            if(_cachedPlayerState == proxy) {
                _cachedPlayerState = null;
            }
        }

        public void TryBindStateSubscriptions() {
            var playerState = ResolvePlayerState();
            if(playerState == null || _boundPlayerState == playerState) return;

            UnbindStateSubscriptions();
            playerState.netHealth.OnValueChanged -= _player.HandleResolvedHealthChanged;
            playerState.netHealth.OnValueChanged += _player.HandleResolvedHealthChanged;
            playerState.netIsDead.OnValueChanged -= _player.HandleResolvedDeathChanged;
            playerState.netIsDead.OnValueChanged += _player.HandleResolvedDeathChanged;
            _boundPlayerState = playerState;

            // Push the current snapshot immediately so owner HUD/state stays correct even if no value
            // changes fire after rebinding during scene/menu transitions.
            _player.HandleResolvedHealthChanged(playerState.netHealth.Value, playerState.netHealth.Value);
        }

        private void UnbindStateSubscriptions() {
            if(_boundPlayerState == null) return;
            _boundPlayerState.netHealth.OnValueChanged -= _player.HandleResolvedHealthChanged;
            _boundPlayerState.netIsDead.OnValueChanged -= _player.HandleResolvedDeathChanged;
            _boundPlayerState = null;
        }

        public void BeginIdentitySync(ulong localSteamId, string ugsPlayerId, string playerDisplayName) {
            CancelPendingIdentitySync();
            _identitySyncCompleted = false;
            _identitySyncRoutine = _player.StartCoroutine(SendIdentityWhenAuthorityReady(localSteamId,
                string.IsNullOrEmpty(ugsPlayerId) ? string.Empty : ugsPlayerId,
                string.IsNullOrWhiteSpace(playerDisplayName) ? "Player" : playerDisplayName));
        }

        public void CancelPendingIdentitySync() {
            if(_identitySyncRoutine == null) return;
            _player.StopCoroutine(_identitySyncRoutine);
            _identitySyncRoutine = null;
        }

        private IEnumerator SendIdentityWhenAuthorityReady(ulong localSteamId, string ugsPlayerId, string playerDisplayName) {
            while(_player != null && _player.IsSpawned && !_identitySyncCompleted) {
                var authority = MatchPlayerStateAuthority.Instance;
                if(authority != null &&
                   authority.NetworkObject != null &&
                   authority.NetworkObject.IsSpawned &&
                   _player.NetworkObject != null &&
                   _player.NetworkObject.IsSpawned) {
                    authority.RequestIdentitySyncServerRpc(
                        new NetworkObjectReference(_player.NetworkObject),
                        localSteamId,
                        new FixedString128Bytes(ugsPlayerId),
                        new FixedString64Bytes(playerDisplayName));
                    _identitySyncCompleted = true;
                    _identitySyncRoutine = null;
                    yield break;
                }

                yield return null;
            }

            _identitySyncRoutine = null;
        }
    }
}
