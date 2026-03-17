using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player.Contracts {
    public interface IPlayerTagContext {
        ulong OwnerClientId { get; }
        bool IsOwner { get; }
        NetworkVariable<FixedString64Bytes> PlayerName { get; }

        void PlayHitEffects(Vector3 hitPoint, float amount);
        void UpdateTeamOutlineColour();
        void UpdateFpArmTagGlow(bool isTagged);
        void DrainCurrentWeaponAmmoForTag();
    }
}
