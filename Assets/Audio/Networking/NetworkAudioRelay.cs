using Network.AntiCheat;
using Network.Diagnostics;
using Unity.Netcode;
using UnityEngine;

namespace Audio.Networking {
    [DisallowMultipleComponent]
    public sealed class NetworkAudioRelay : NetworkBehaviour {
        /// <summary>
        /// Client-side convenience API for requesting a networked SFX play.
        /// Typically called by the local owner when an SFX-worthy event happens.
        /// </summary>
        public void RequestPlay(string soundId, Vector3 worldPosition, bool allowOverlap = true, uint seed = 0) {
            if(!IsOwner) return;
            if(string.IsNullOrWhiteSpace(soundId)) return;
            if(seed == 0) {
                seed = (uint)Random.Range(1, int.MaxValue);
            }
            RequestPlayServerRpc(soundId, worldPosition, default, attachTo: false, allowOverlap: allowOverlap, seed: seed);
        }

        public void RequestPlayAttached(string soundId, NetworkObjectReference attachTo, bool allowOverlap = true, uint seed = 0) {
            if(!IsOwner) return;
            if(string.IsNullOrWhiteSpace(soundId)) return;
            if(seed == 0) {
                seed = (uint)Random.Range(1, int.MaxValue);
            }
            RequestPlayServerRpc(soundId, Vector3.zero, attachTo, attachTo: true, allowOverlap: allowOverlap, seed: seed);
        }

        public void RequestStop(string soundId) {
            if(!IsOwner) return;
            if(string.IsNullOrWhiteSpace(soundId)) return;
            RequestStopServerRpc(soundId);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        private void RequestPlayServerRpc(string soundId, Vector3 worldPosition, NetworkObjectReference attachRef,
            bool attachTo, bool allowOverlap, uint seed) {

            var config = AntiCheatConfig.Instance;
            if(config != null) {
                if(!RpcRateLimiter.TryConsume(OwnerClientId, RpcRateLimiter.Keys.WorldSfx, config.sfxRpcLimit,
                        config.rpcWindowSeconds)) {

                    AntiCheatLogger.LogRateLimit(OwnerClientId, RpcRateLimiter.Keys.WorldSfx);
                    return;
                }
            }

            // Minimal validation: avoid huge strings / spam.
            if(soundId.Length > 128) return;

            // Use reliable delivery for sounds that must not be dropped (e.g. jumppad launch after grapple).
            if(soundId == "gameplay.jumppad") {
                PlayCriticalClientRpc(soundId, worldPosition, attachRef, attachTo, allowOverlap, seed, OwnerClientId);
            } else {
                PlayClientRpc(soundId, worldPosition, attachRef, attachTo, allowOverlap, seed);
            }
        }

        [Rpc(SendTo.Everyone, Delivery = RpcDelivery.Reliable)]
        private void PlayCriticalClientRpc(string soundId, Vector3 worldPosition, NetworkObjectReference attachRef,
            bool attachTo, bool allowOverlap, uint seed, ulong requestingClientId) {
            PlayClientRpcImpl(soundId, worldPosition, attachRef, attachTo, allowOverlap, seed, requestingClientId);
        }

        [Rpc(SendTo.Everyone, Delivery = RpcDelivery.Unreliable)]
        private void PlayClientRpc(string soundId, Vector3 worldPosition, NetworkObjectReference attachRef,
            bool attachTo, bool allowOverlap, uint seed) {
            PlayClientRpcImpl(soundId, worldPosition, attachRef, attachTo, allowOverlap, seed, ulong.MaxValue);
        }

        private void PlayClientRpcImpl(string soundId, Vector3 worldPosition, NetworkObjectReference attachRef,
            bool attachTo, bool allowOverlap, uint seed, ulong requestingClientId = ulong.MaxValue) {

            var svc = Game.Audio2.AudioService.Instance;
            if(svc == null) {
                return;
            }

            if(!allowOverlap) {
                svc.Stop(soundId);
            }

            // Resolve parent if attaching; if lookup fails, fall back to world position.
            if(attachTo) {
                if(DebugHelpers.TryGetNetworkObjectSafe(attachRef, out var no, NetworkManager.LocalClientId,
                       "NetworkAudioRelay.PlayClientRpc")) {
                    svc.PlayAttached(soundId, no.transform, seed);
                    return;
                }
            }

            svc.Play(soundId, worldPosition, seed);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        private void RequestStopServerRpc(string soundId) {
            var config = AntiCheatConfig.Instance;
            if(config != null) {
                if(!RpcRateLimiter.TryConsume(OwnerClientId, RpcRateLimiter.Keys.WorldSfx, config.sfxRpcLimit,
                        config.rpcWindowSeconds)) {
                    AntiCheatLogger.LogRateLimit(OwnerClientId, RpcRateLimiter.Keys.WorldSfx);
                    return;
                }
            }

            if(soundId.Length > 128) return;
            StopClientRpc(soundId);
        }

        [Rpc(SendTo.Everyone, Delivery = RpcDelivery.Unreliable)]
        private void StopClientRpc(string soundId) {
            var svc = Game.Audio2.AudioService.Instance;
            if(svc == null) return;
            svc.Stop(soundId);
        }
    }
}

