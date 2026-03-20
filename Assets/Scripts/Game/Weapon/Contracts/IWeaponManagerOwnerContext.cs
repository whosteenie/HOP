using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Weapon.Contracts {
    public interface IWeaponManagerOwnerContext {
        bool IsOwner { get; }
        bool IsHoldingHopball { get; }
        bool IsRagdoll { get; }
        ulong OwnerClientId { get; }
        ulong NetworkObjectId { get; }
        NetworkObject NetworkObject { get; }
        Transform Transform { get; }
        Transform FpCameraTransform { get; }
        CinemachineCamera FpCamera { get; }
        Camera WeaponCamera { get; }
        Transform WorldWeaponSocket { get; }
        Animator PlayerAnimator { get; }
        IWeaponFacade WeaponComponent { get; }
        NetworkVariable<int> PrimaryWeaponIndexState { get; }
        NetworkVariable<int> SecondaryWeaponIndexState { get; }
        NetworkVariable<bool> NetIsDeadState { get; }
    }
}
