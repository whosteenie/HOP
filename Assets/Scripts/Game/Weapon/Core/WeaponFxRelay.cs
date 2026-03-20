using System.Collections.Generic;
using Game.Weapon.Contracts;
using Network.AntiCheat;
using Network.Core;
using Unity.Netcode;
using UnityEngine;

namespace Game.Weapon.Core {
    [DefaultExecutionOrder(7005)] // Run after UpperBodyPitch + SpineProxy LateUpdate passes.
    public class WeaponFxRelay : NetworkBehaviour, IWeaponFxRelay {
        private const string RequestShotFxServerRpcContext = "WeaponFxRelay.RequestShotFxServerRpc";

        [HideInInspector, SerializeField] private MonoBehaviour ownerContextSource;
        private IWeaponOwnerContext _ownerContext;
        private NetworkObject _playerNetworkObject;
        private readonly List<PendingRemoteShotFx> _pendingRemoteShotFx = new();

        private struct PendingRemoteShotFx {
            internal IWeaponFacade Weapon;
            internal ShotFxRequest Request;
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

                var request = pending.Request;
                if(request.PlayMuzzleFlash) {
                    weapon.PlayNetworkedMuzzleFlash(request.EndPoint);
                }

                var hasStartPoint = weapon.TryGetRemoteWorldMuzzlePosition(out var startPoint);
                if(hasStartPoint) {
                    weapon.SpawnTracerLocal(new TracerSpawnRequest {
                        Start = startPoint,
                        End = request.EndPoint,
                        HitNormal = request.HitNormal,
                        MadeImpact = request.MadeImpact,
                        HitPlayer = request.HitPlayer,
                        HitPlayerRef = request.HitPlayerRef,
                        ShooterVelocity = request.ShooterVelocity
                    });
                }
            }

            _pendingRemoteShotFx.Clear();
        }

        public void RequestShotFx(in ShotFxRequest request) {
            if(_ownerContext is not { IsOwner: true } || !_playerNetworkObject.IsSpawned) return;

            RequestShotFxServerRpc(request.EndPoint, request.HitNormal, request.MadeImpact, request.HitPlayer,
                request.HitPlayerRef, request.PlayMuzzleFlash, request.ShooterVelocity);
        }

        private void QueueRemoteShotFx(in ShotFxRequest request) {
            ValidateComponents();
            if(_playerNetworkObject == null) return;
            if(_playerNetworkObject.IsOwner) return;
            var weapon = _ownerContext != null ? _ownerContext.CurrentWeapon : null;
            if(weapon == null) return;

            _pendingRemoteShotFx.Add(new PendingRemoteShotFx {
                Weapon = weapon,
                Request = request
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
                AntiCheatLogger.LogAuthorityViolate(RequestShotFxServerRpcContext, senderClientId);
                return;
            }

            BroadcastShotFxClientRpc(endPoint, hitNormal, madeImpact, hitPlayer, hitPlayerRef, playMuzzleFlash,
                shooterVelocity);
        }

        [Rpc(SendTo.Everyone)]
        private void BroadcastShotFxClientRpc(Vector3 endPoint, Vector3 hitNormal, bool madeImpact,
            bool hitPlayer, NetworkObjectReference hitPlayerRef, bool playMuzzleFlash, Vector3 shooterVelocity) {
            QueueRemoteShotFx(new ShotFxRequest {
                EndPoint = endPoint,
                HitNormal = hitNormal,
                MadeImpact = madeImpact,
                HitPlayer = hitPlayer,
                HitPlayerRef = hitPlayerRef,
                PlayMuzzleFlash = playMuzzleFlash,
                ShooterVelocity = shooterVelocity
            });
        }
    }
}
