using Game.Player;
using Game.Player.Hopball;
using Network.Diagnostics;
using Unity.Netcode;
using UnityEngine;

namespace Game.Hopball {
    public partial class HopballSpawnManager {
        public void RequestEquipHopballAuthority(NetworkObjectReference hopballRef) {
            if(HasHopballAuthority) {
                ProcessEquipHopballRequest(hopballRef, NetworkManager != null ? NetworkManager.LocalClientId : OwnerClientId);
                return;
            }

            RequestEquipHopballAuthorityServerRpc(hopballRef);
        }

        public void RequestDropHopballAuthority(NetworkObjectReference hopballRef, Vector3 dropPosition,
            Quaternion dropRotation, Vector3 playerVelocity, string dropReason) {
            if(HasHopballAuthority) {
                ProcessDropHopballRequest(hopballRef, dropPosition, dropRotation, playerVelocity, dropReason,
                    NetworkManager != null ? NetworkManager.LocalClientId : OwnerClientId);
                return;
            }

            RequestDropHopballAuthorityServerRpc(hopballRef, dropPosition, dropRotation, playerVelocity, dropReason);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        private void RequestEquipHopballAuthorityServerRpc(NetworkObjectReference hopballRef,
            RpcParams rpcParams = default) {
            ProcessEquipHopballRequest(hopballRef, rpcParams.Receive.SenderClientId);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        private void RequestDropHopballAuthorityServerRpc(NetworkObjectReference hopballRef, Vector3 dropPosition,
            Quaternion dropRotation, Vector3 playerVelocity, string dropReason, RpcParams rpcParams = default) {
            ProcessDropHopballRequest(hopballRef, dropPosition, dropRotation, playerVelocity, dropReason,
                rpcParams.Receive.SenderClientId);
        }

        private void ProcessEquipHopballRequest(NetworkObjectReference hopballRef, ulong requestingClientId) {
            if(!HasHopballAuthority) return;
            if(!hopballRef.TryGet(out var networkObject) || networkObject == null) return;

            var hopball = networkObject.GetComponent<HopballController>();
            if(hopball == null) return;
            if(hopball.IsEquipped) {
                FlowLog.Emit(FlowEventIds.AnomalyHopballMismatch,
                    ("serverHolder", hopball.HolderController != null ? hopball.HolderController.OwnerClientId.ToString() : "None"),
                    ("localHolder", requestingClientId),
                    ("osiHolder", "Unknown"),
                    ("reason", "PickupRejectedAlreadyEquipped"));
                return;
            }

            if(NetworkManager == null || !NetworkManager.ConnectedClients.TryGetValue(requestingClientId, out var client)) {
                return;
            }

            var requestingPlayer = client.PlayerObject;
            if(requestingPlayer == null) return;

            var requestingController = requestingPlayer.GetComponent<PlayerController>();
            if(requestingController == null) return;
            var controller = requestingController.PlayerHopballController;
            if(controller == null) return;
            FlowLog.Emit(FlowEventIds.HopballPickupCommitted,
                ("player", requestingClientId),
                ("hopballNetId", networkObject.NetworkObjectId),
                ("serverHolder", requestingClientId));

            hopball.SetEquipped(true, controller);

            OnPlayerPickedUpHopball(requestingClientId);
            controller.OnHopballEquippedClientRpc(hopballRef, requestingClientId);
        }

        private void ProcessDropHopballRequest(NetworkObjectReference hopballRef, Vector3 dropPosition,
            Quaternion dropRotation, Vector3 playerVelocity, string dropReason, ulong requestingClientId) {
            if(!HasHopballAuthority) return;
            if(!hopballRef.TryGet(out var networkObject) || networkObject == null) return;

            var hopball = networkObject.GetComponent<HopballController>();
            PlayerHopballController.DropHopballAtPositionAuthority(hopball, dropPosition, dropRotation,
                requestingClientId, playerVelocity, dropReason).Forget();
        }
    }
}
