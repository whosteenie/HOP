using System.Collections.Generic;
using Game.Weapon.Contracts;
using Network.AntiCheat;
using Network.Core;
using Unity.Netcode;
using UnityEngine;

namespace Game.Weapon.Core {
    [DefaultExecutionOrder(7005)] // Run after UpperBodyPitch + SpineProxy LateUpdate passes.
    public class WeaponFxRelay : NetworkBehaviour, IWeaponFxRelay {
        [HideInInspector, SerializeField] private MonoBehaviour ownerContextSource;
        private IWeaponOwnerContext _ownerContext;
        private NetworkObject _playerNetworkObject;
        private readonly List<PendingRemoteShotFx> _pendingRemoteShotFx = new();

        private struct PendingRemoteShotFx {
            internal IWeaponFacade Weapon;
            internal Vector3 EndPoint;
            internal Vector3 HitNormal;
            internal Vector3 ShooterVelocity;
            internal bool MadeImpact;
            internal bool HitPlayer;
            internal NetworkObjectReference HitPlayerRef;
            internal bool PlayMuzzleFlash;
        }

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(_ownerContext == null) {
                if(ownerContextSource != null) {
                    // ReSharper disable once UsePatternMatching
                    var ownerContext = ownerContextSource as IWeaponOwnerContext;
                    if(ownerContext != null) {
                        _ownerContext = ownerContext;
                    }
                } else {
                    foreach(var candidate in GetComponentsInParent<MonoBehaviour>(true)) {
                        if(candidate == null) continue;
                        // ReSharper disable once UseNegatedPatternMatching
                        var resolvedContext = candidate as IWeaponOwnerContext;
                        if(resolvedContext == null) continue;
                        ownerContextSource = candidate;
                        _ownerContext = resolvedContext;
                        break;
                    }
                }
            }

            if(_ownerContext == null) {
                enabled = false;
                return;
            }

            if(_playerNetworkObject == null) {
                _playerNetworkObject = _ownerContext.NetworkObject;
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
            if(_ownerContext is not { IsOwner: true } || !_playerNetworkObject.IsSpawned) return;

            RequestShotFxServerRpc(endPoint, hitNormal, madeImpact, hitPlayer, hitPlayerRef, playMuzzleFlash,
                shooterVelocity);
        }

        private void QueueRemoteShotFx(Vector3 endPoint, Vector3 hitNormal, bool madeImpact, bool hitPlayer,
            NetworkObjectReference hitPlayerRef, bool playMuzzleFlash, Vector3 shooterVelocity) {
            ValidateComponents();
            if(_playerNetworkObject == null) return;
            if(_playerNetworkObject.IsOwner) return;
            var weapon = _ownerContext != null ? _ownerContext.CurrentWeapon : null;
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
            if(_ownerContext == null || _playerNetworkObject == null) {
                return;
            }

            var senderClientId = rpcParams.Receive.SenderClientId;
            if(_ownerContext.OwnerClientId != senderClientId) {
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
