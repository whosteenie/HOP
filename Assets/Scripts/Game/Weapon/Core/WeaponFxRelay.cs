using System.Collections.Generic;
using Diagnostics;
using Game.Player.Core;
using Network.AntiCheat;
using Network.Core;
using Unity.Netcode;
using UnityEngine;

namespace Game.Weapon.Core {
    [DefaultExecutionOrder(7005)] // Run after UpperBodyPitch + SpineProxy LateUpdate passes.
    public class WeaponFxRelay : NetworkBehaviour {
        [SerializeField] private PlayerController playerController;
        private NetworkObject _playerNetworkObject;
        private readonly List<PendingRemoteShotFx> _pendingRemoteShotFx = new();

        private struct PendingRemoteShotFx {
            public Weapon Weapon;
            public Vector3 EndPoint;
            public Vector3 HitNormal;
            public Vector3 ShooterVelocity;
            public bool MadeImpact;
            public bool HitPlayer;
            public NetworkObjectReference HitPlayerRef;
            public bool PlayMuzzleFlash;
        }

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(playerController == null) {
                playerController = this.GetComponentSafe<PlayerController>("NetworkFxRelay.ValidateComponents");
            }

            if(playerController == null) {
                enabled = false;
                return;
            }

            if(_playerNetworkObject == null) {
                _playerNetworkObject = playerController.NetworkObject;
            }
        }

        private void LateUpdate() {
            if(_pendingRemoteShotFx.Count == 0) return;

            foreach(var pending in _pendingRemoteShotFx) {
                var weapon = pending.Weapon;
                if(weapon == null) continue;

                if(pending.PlayMuzzleFlash) {
                    weapon.PlayNetworkedMuzzleFlash(pending.EndPoint);
                }

                var hasStartPoint = weapon.TryGetRemoteWorldMuzzlePosition(out var startPoint);
                if(hasStartPoint) {
                    weapon.SpawnTracerLocal(startPoint, pending.EndPoint, pending.HitNormal, pending.MadeImpact,
                        pending.HitPlayer, pending.HitPlayerRef, pending.ShooterVelocity);
                }
            }

            _pendingRemoteShotFx.Clear();
        }

        public void RequestShotFx(Vector3 endPoint, Vector3 hitNormal, bool madeImpact,
            bool hitPlayer, NetworkObjectReference hitPlayerRef, bool playMuzzleFlash = true,
            Vector3 shooterVelocity = default) {
            if(!playerController.IsOwner || !_playerNetworkObject.IsSpawned) return;

            RequestShotFxServerRpc(endPoint, hitNormal, madeImpact, hitPlayer, hitPlayerRef, playMuzzleFlash,
                shooterVelocity);
        }

        private void QueueRemoteShotFx(Vector3 endPoint, Vector3 hitNormal, bool madeImpact, bool hitPlayer,
            NetworkObjectReference hitPlayerRef, bool playMuzzleFlash, Vector3 shooterVelocity) {
            ValidateComponents();
            if(_playerNetworkObject == null) return;
            if(_playerNetworkObject.IsOwner) return;
            var weapon = playerController.CurrentWeapon;
            if(weapon == null) return;

            _pendingRemoteShotFx.Add(new PendingRemoteShotFx {
                Weapon = weapon,
                EndPoint = endPoint,
                HitNormal = hitNormal,
                ShooterVelocity = shooterVelocity,
                MadeImpact = madeImpact,
                HitPlayer = hitPlayer,
                HitPlayerRef = hitPlayerRef,
                PlayMuzzleFlash = playMuzzleFlash
            });
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        private void RequestShotFxServerRpc(Vector3 endPoint, Vector3 hitNormal, bool madeImpact,
            bool hitPlayer, NetworkObjectReference hitPlayerRef, bool playMuzzleFlash,
            Vector3 shooterVelocity, RpcParams rpcParams = default) {
            if(!NetworkAuthority.HasGlobalAuthority(this)) {
                return;
            }

            ValidateComponents();
            if(playerController == null || _playerNetworkObject == null) {
                return;
            }

            var senderClientId = rpcParams.Receive.SenderClientId;
            if(playerController.OwnerClientId != senderClientId) {
                AntiCheatLogger.LogAuthorityViolate("WeaponFxRelay.RequestShotFxServerRpc", senderClientId);
                return;
            }

            BroadcastShotFxClientRpc(endPoint, hitNormal, madeImpact, hitPlayer, hitPlayerRef, playMuzzleFlash,
                shooterVelocity);
        }

        [Rpc(SendTo.Everyone)]
        private void BroadcastShotFxClientRpc(Vector3 endPoint, Vector3 hitNormal, bool madeImpact,
            bool hitPlayer, NetworkObjectReference hitPlayerRef, bool playMuzzleFlash, Vector3 shooterVelocity) {
            QueueRemoteShotFx(endPoint, hitNormal, madeImpact, hitPlayer, hitPlayerRef, playMuzzleFlash,
                shooterVelocity);
        }
    }
}
