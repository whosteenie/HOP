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
            public Weapon weapon;
            public Vector3 endPoint;
            public Vector3 hitNormal;
            public bool madeImpact;
            public bool hitPlayer;
            public NetworkObjectReference hitPlayerRef;
            public bool playMuzzleFlash;
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

            for(var i = 0; i < _pendingRemoteShotFx.Count; i++) {
                var pending = _pendingRemoteShotFx[i];
                var weapon = pending.weapon;
                if(weapon == null) continue;

                if(pending.playMuzzleFlash) {
                    weapon.PlayNetworkedMuzzleFlash(pending.endPoint);
                }

                var hasStartPoint = weapon.TryGetRemoteWorldMuzzlePosition(out var startPoint);
                weapon.LogRemoteTracerDebug(pending.endPoint, hasStartPoint, startPoint);

                if(hasStartPoint) {
                    weapon.SpawnTracerLocal(startPoint, pending.endPoint, pending.hitNormal, pending.madeImpact,
                        pending.hitPlayer, pending.hitPlayerRef);
                }
            }

            _pendingRemoteShotFx.Clear();
        }

        public void RequestShotFx(Vector3 endPoint, Vector3 hitNormal, bool madeImpact,
            bool hitPlayer, NetworkObjectReference hitPlayerRef, bool playMuzzleFlash = true) {
            if(!playerController.IsOwner || !_playerNetworkObject.IsSpawned) return;

            RequestShotFxServerRpc(_playerNetworkObject, endPoint, hitNormal, madeImpact, hitPlayer, hitPlayerRef, playMuzzleFlash);
        }

        [Rpc(SendTo.Server)]
        private void RequestShotFxServerRpc(NetworkObjectReference shooterRef, Vector3 endPoint, Vector3 hitNormal,
            bool madeImpact, bool hitPlayer, NetworkObjectReference hitPlayerRef, bool playMuzzleFlash) {
            PlayShotFxClientRpc(shooterRef, endPoint, hitNormal, madeImpact, hitPlayer, hitPlayerRef, playMuzzleFlash);
        }

        [Rpc(SendTo.NotOwner)]
        private void PlayShotFxClientRpc(NetworkObjectReference shooterRef, Vector3 endPoint, Vector3 hitNormal,
            bool madeImpact, bool hitPlayer, NetworkObjectReference hitPlayerRef, bool playMuzzleFlash) {
            if(!shooterRef.TryGet(out var networkObject) || networkObject == null) return;

            var weaponManager = networkObject.GetComponent<WeaponManager>();

            if(weaponManager == null) return;
            if(networkObject.IsOwner) return;
            var weapon = weaponManager.CurrentWeapon;
            if(weapon == null) return;

            _pendingRemoteShotFx.Add(new PendingRemoteShotFx {
                weapon = weapon,
                endPoint = endPoint,
                hitNormal = hitNormal,
                madeImpact = madeImpact,
                hitPlayer = hitPlayer,
                hitPlayerRef = hitPlayerRef,
                playMuzzleFlash = playMuzzleFlash
            });
        }
    }
}
