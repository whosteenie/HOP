using UnityEngine;

namespace Game.Player.Contracts {
    public interface IPlayerTagContext {
        ulong OwnerClientId { get; }
        bool IsGunTagMode { get; }

        void PlayHitEffects(Vector3 hitPoint, float amount);
        void UpdateTeamOutlineColour();
        void UpdateFpArmTagGlow(bool isTagged);
        void DrainCurrentWeaponAmmoForTag();
    }
}
