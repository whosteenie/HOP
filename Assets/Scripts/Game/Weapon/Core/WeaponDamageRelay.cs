using System;
using Unity.Netcode;

namespace Game.Weapon.Core {
    public class WeaponDamageRelay : NetworkBehaviour {
        /// <summary>
        /// Shooter-side callback (client) to play hit/kill UI, etc.
        /// Only invoked on the LOCAL shooter after the server confirms.
        /// </summary>
        public event Action<bool> OnHitConfirm;

        /// <summary>
        /// Server -> Clients: notify a specific shooter they hit/fragged (self-filter on client).
        /// </summary>
        public void SendHitConfirmToOwner(bool wasKill) {
            HitConfirmOwnerRpc(wasKill);
        }

        [Rpc(SendTo.Owner)]
        private void HitConfirmOwnerRpc(bool wasKill) {
            if(OnHitConfirm != null) {
                OnHitConfirm.Invoke(wasKill);
            }
        }
    }
}
