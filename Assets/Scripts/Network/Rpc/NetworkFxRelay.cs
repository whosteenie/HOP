using System.Collections.Generic;
using Game.Player;
using Game.Weapons;
using Network.Diagnostics;
using Unity.Netcode;
using UnityEngine;

namespace Network.Rpc {
    [DefaultExecutionOrder(7005)] // Run after UpperBodyPitch + SpineProxy LateUpdate passes.
    public class NetworkFxRelay : NetworkBehaviour {
        [SerializeField] private PlayerController playerController;
        private NetworkObject _playerNetworkObject;
        private WeaponManager _playerWeaponManager;
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

            if(_playerWeaponManager == null) {
                _playerWeaponManager = playerController.WeaponManager;
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

            RequestShotFxServerRpc(_playerNetworkObject, endPoint, hitNormal, madeImpact, hitPlayer, hitPlayerRef,
                playMuzzleFlash, shooterVelocity);
        }

        [Rpc(SendTo.Server)]
        private void RequestShotFxServerRpc(NetworkObjectReference shooterRef, Vector3 endPoint, Vector3 hitNormal,
            bool madeImpact, bool hitPlayer, NetworkObjectReference hitPlayerRef, bool playMuzzleFlash,
            Vector3 shooterVelocity) {
            PlayShotFxClientRpc(shooterRef, endPoint, hitNormal, madeImpact, hitPlayer, hitPlayerRef, playMuzzleFlash,
                shooterVelocity);
        }

        [Rpc(SendTo.NotOwner)]
        private void PlayShotFxClientRpc(NetworkObjectReference shooterRef, Vector3 endPoint, Vector3 hitNormal,
            bool madeImpact, bool hitPlayer, NetworkObjectReference hitPlayerRef, bool playMuzzleFlash,
            Vector3 shooterVelocity) {
            if(!shooterRef.TryGet(out var networkObject) || networkObject == null) return;

            var weaponManager = networkObject.GetComponent<WeaponManager>();

            if(weaponManager == null) return;
            if(networkObject.IsOwner) return;
            var weapon = weaponManager.CurrentWeapon;
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
    }
}
