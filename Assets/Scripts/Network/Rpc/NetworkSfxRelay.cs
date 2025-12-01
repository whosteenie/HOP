using System.Collections.Generic;
using Game.Player;
using Game.Audio;
using Network.AntiCheat;
using Network.Singletons;
using Unity.Netcode;
using UnityEngine;

namespace Network.Rpc {
    public class NetworkSfxRelay : NetworkBehaviour {
        [SerializeField] private PlayerController playerController;
        // simple anti-spam per key
        private readonly Dictionary<SfxKey, float> _lastSent = new();
        private const float WalkMinInterval = 0.02f;
        private const float RunMinInterval = 0.01f;

        private const float LandMinInterval = 0.01f; // Reduced for bunnyhopping - allow more frequent land sounds

        private const float JumpMinInterval = 0.08f;
        private const float ReloadMinInterval = 0.10f;
        private const float DryMinInterval = 0.05f;
        private const float ShootMinInterval = 0.01f;
        private const float JumpPadMinInterval = 0.25f;
        private const float GrappleMinInterval = 0.10f;
        private const float BulletTrailMinInterval = 0.05f;

        private static float GetMinInterval(SfxKey key) => key switch {
            SfxKey.Walk => WalkMinInterval,
            SfxKey.Run => RunMinInterval,
            SfxKey.Land => LandMinInterval,
            SfxKey.Jump => JumpMinInterval,
            SfxKey.Reload => ReloadMinInterval,
            SfxKey.Dry => DryMinInterval,
            SfxKey.Shoot => ShootMinInterval,
            SfxKey.JumpPad => JumpPadMinInterval,
            SfxKey.Grapple => GrappleMinInterval,
            SfxKey.BulletTrail => BulletTrailMinInterval,
            _ => 0.1f
        };

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(playerController == null) {
                Debug.LogError("[NetworkSfxRelay] PlayerController not found!");
                enabled = false;
            }
        }

        /// <summary>
        /// Call this locally on the owner when an SFX-worthy event happens.
        /// attachToSelf=true parents the AudioSource to this player so it follows them.
        /// </summary>
        public void RequestWorldSfx(SfxKey key, bool attachToSelf = true, bool allowOverlap = false) {
            // optional rate limit on the caller side as well
            var t = Time.time;
            if(_lastSent.TryGetValue(key, out var last) && t - last < GetMinInterval(key)) {
                return;
            }

            _lastSent[key] = t;

            // Additional velocity check for walk/run sounds (backup to PlayerController check)
            // Check horizontal velocity only - vertical velocity shouldn't affect footstep sounds
            if(key == SfxKey.Walk || key == SfxKey.Run) {
                var horizontalVel = playerController.GetFullVelocity;
                horizontalVel.y = 0f; // Ignore vertical velocity
                if(horizontalVel.sqrMagnitude < 0.5f * 0.5f) { // ~0.5 m/s minimum speed
                    return;
                }
            }

            RequestWorldSfxServerRpc(key, attachToSelf, allowOverlap);
        }

        public void RequestWorldSfxAtPosition(SfxKey key, Vector3 worldPosition, bool allowOverlap = false) {
            if(!IsOwner) return;

            var t = Time.time;
            if(_lastSent.TryGetValue(key, out var last) && t - last < GetMinInterval(key)) {
                return;
            }

            _lastSent[key] = t;
            RequestWorldSfxAtPositionServerRpc(key, worldPosition, allowOverlap);
        }

        [Rpc(SendTo.Server)]
        private void RequestWorldSfxServerRpc(SfxKey key, bool attachToSelf, bool allowOverlap) {
            if(!IsSpawned) return;

            var config = AntiCheatConfig.Instance;
            if(config != null) {
                if(!RpcRateLimiter.TryConsume(OwnerClientId, RpcRateLimiter.Keys.WorldSfx, config.sfxRpcLimit,
                        config.rpcWindowSeconds)) {
                    AntiCheatLogger.LogRateLimit(OwnerClientId, RpcRateLimiter.Keys.WorldSfx);
                    return;
                }
            }

            var srcRef = new NetworkObjectReference(NetworkObject);
            var pos = transform.position;

            PlayWorldSfxClientRpc(key, srcRef, pos, attachToSelf, allowOverlap);
        }

        [Rpc(SendTo.Server)]
        private void RequestWorldSfxAtPositionServerRpc(SfxKey key, Vector3 worldPosition, bool allowOverlap) {
            if(!IsSpawned) return;
            var config = AntiCheatConfig.Instance;
            if(config != null) {
                if(!RpcRateLimiter.TryConsume(OwnerClientId, RpcRateLimiter.Keys.WorldSfx, config.sfxRpcLimit,
                        config.rpcWindowSeconds)) {
                    AntiCheatLogger.LogRateLimit(OwnerClientId, RpcRateLimiter.Keys.WorldSfx);
                    return;
                }
            }
            PlayWorldSfxAtPositionClientRpc(key, worldPosition, allowOverlap);
        }

        [Rpc(SendTo.Everyone)]
        private void PlayWorldSfxClientRpc(SfxKey key, NetworkObjectReference sourceRef, Vector3 pos,
            bool attachToSource, bool allowOverlap) {
            Transform parent = null;
            if(attachToSource && sourceRef.TryGet(out var no) && no != null) {
                parent = no.transform;
            }

            if(SoundFXManager.Instance != null) {
                SoundFXManager.Instance.PlayKey(key, parent, pos, allowOverlap);
            }
        }

        [Rpc(SendTo.Everyone)]
        private void PlayWorldSfxAtPositionClientRpc(SfxKey key, Vector3 worldPosition, bool allowOverlap) {
            if(SoundFXManager.Instance != null) {
                SoundFXManager.Instance.PlayKey(key, null, worldPosition, allowOverlap);
            }
        }

        /// <summary>
        /// Stop a currently playing world sound (e.g., cancel reload sound when switching weapons)
        /// </summary>
        public void StopWorldSfx(SfxKey key) {
            if(!IsOwner) return; // Only owner can request to stop sounds
            StopWorldSfxServerRpc(key);
        }

        [Rpc(SendTo.Server)]
        private void StopWorldSfxServerRpc(SfxKey key) {
            if(!IsSpawned) return;
            StopWorldSfxClientRpc(key);
        }

        [Rpc(SendTo.Everyone)]
        private void StopWorldSfxClientRpc(SfxKey key) {
            if(SoundFXManager.Instance != null) {
                SoundFXManager.Instance.StopSound(key);
            }
        }
    }
}