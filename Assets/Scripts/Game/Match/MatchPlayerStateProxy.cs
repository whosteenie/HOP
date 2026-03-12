using System;
using Network.Core;
using Unity.Netcode;
using UnityEngine;

namespace Game.Match {
    [DisallowMultipleComponent]
    public class MatchPlayerStateProxy : NetworkBehaviour {
        public static event Action<ulong, MatchPlayerStateProxy> StateRegistered;
        public static event Action<ulong, MatchPlayerStateProxy> StateUnregistered;

        private static readonly System.Collections.Generic.Dictionary<ulong, MatchPlayerStateProxy> StateByClientId =
            new();

        public static bool TryGetForPlayer(ulong playerClientId, out MatchPlayerStateProxy proxy) {
            return StateByClientId.TryGetValue(playerClientId, out proxy) && proxy != null;
        }

        public static MatchPlayerStateProxy GetForPlayer(ulong playerClientId) {
            TryGetForPlayer(playerClientId, out var proxy);
            return proxy;
        }

        public NetworkVariable<ulong> representedClientId = new(ulong.MaxValue,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public NetworkVariable<float> netHealth = new(100f,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public NetworkVariable<bool> netIsDead = new(false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public NetworkVariable<int> kills = new(0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public NetworkVariable<int> deaths = new(0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public NetworkVariable<int> assists = new(0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public NetworkVariable<float> damageDealt = new(0f,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public NetworkVariable<int> equippedWeaponIndex = new(-1,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public NetworkVariable<int> tags = new(0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public NetworkVariable<int> tagged = new(0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public NetworkVariable<int> timeTagged = new(0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public NetworkVariable<bool> isTagged = new(false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private bool _sessionOwnerCallbacksRegistered;
        private ulong _registeredClientId = ulong.MaxValue;

        public ulong RepresentedClientId => representedClientId.Value;

        public void InitializeForPlayer(ulong playerClientId) {
            representedClientId.Value = playerClientId;
            netHealth.Value = 100f;
            netIsDead.Value = false;
            kills.Value = 0;
            deaths.Value = 0;
            assists.Value = 0;
            damageDealt.Value = 0f;
            equippedWeaponIndex.Value = -1;
            tags.Value = 0;
            tagged.Value = 0;
            timeTagged.Value = 0;
            isTagged.Value = false;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            representedClientId.OnValueChanged += OnRepresentedClientIdChanged;
            RegisterSessionOwnerCallbacks();
            NetworkAuthority.TryConfigureSessionOwnerObject(this);
            TryRegisterLocalState();
        }

        public override void OnNetworkDespawn() {
            base.OnNetworkDespawn();
            representedClientId.OnValueChanged -= OnRepresentedClientIdChanged;
            UnregisterLocalState(_registeredClientId);
            UnregisterSessionOwnerCallbacks();
        }

        public override void OnDestroy() {
            base.OnDestroy();
            UnregisterLocalState(_registeredClientId);
            UnregisterSessionOwnerCallbacks();
        }

        private void RegisterSessionOwnerCallbacks() {
            if(_sessionOwnerCallbacksRegistered || NetworkManager == null) {
                return;
            }

            NetworkManager.OnSessionOwnerPromoted += OnSessionOwnerPromoted;
            _sessionOwnerCallbacksRegistered = true;
        }

        private void UnregisterSessionOwnerCallbacks() {
            if(!_sessionOwnerCallbacksRegistered || NetworkManager == null) {
                return;
            }

            NetworkManager.OnSessionOwnerPromoted -= OnSessionOwnerPromoted;
            _sessionOwnerCallbacksRegistered = false;
        }

        private void OnSessionOwnerPromoted(ulong _) {
            if(NetworkAuthority.HasGlobalAuthority(this)) {
                NetworkAuthority.TryConfigureSessionOwnerObject(this);
            }
        }

        private void OnRepresentedClientIdChanged(ulong previousClientId, ulong newClientId) {
            UnregisterLocalState(previousClientId);
            TryRegisterLocalState();
        }

        private void TryRegisterLocalState() {
            var playerClientId = representedClientId.Value;
            if(playerClientId == ulong.MaxValue) {
                return;
            }

            StateByClientId[playerClientId] = this;
            _registeredClientId = playerClientId;
            StateRegistered?.Invoke(playerClientId, this);
        }

        private void UnregisterLocalState(ulong playerClientId) {
            if(playerClientId == ulong.MaxValue) {
                return;
            }

            if(StateByClientId.TryGetValue(playerClientId, out var current) && current == this) {
                StateByClientId.Remove(playerClientId);
                StateUnregistered?.Invoke(playerClientId, this);
            }

            if(_registeredClientId == playerClientId) {
                _registeredClientId = ulong.MaxValue;
            }
        }
    }
}
