using Game.Weapon.Manager;
using Unity.Cinemachine;
using UnityEngine;
using UnityPlayerInputComponent = UnityEngine.InputSystem.PlayerInput;

namespace Game.Player.Contracts {
    public interface IPlayerInputContext {
        UnityPlayerInputComponent UnityPlayerInput { get; }
        AudioListener AudioListener { get; }
        CinemachineCamera FpCamera { get; }
        WeaponManager WeaponManager { get; }
        bool IsDead { get; }
        bool IsGrounded { get; }
        bool IsHoldingHopball { get; }
        bool IsWallRunning { get; }
        bool IsMantling { get; }
        bool CanMantleJump { get; }
        bool IsGrappling { get; }
        bool LockLook { get; set; }
        bool SprintInputState { get; set; }
        bool CrouchInputState { get; set; }
        bool IsSniperZoomActive { get; }

        void SetMoveInput(Vector2 move);
        void SetLookInput(Vector2 look);
        void TryJump(float height = 2f);
        void PickupHopball();
        void TryMantle();
        void TryGrapple();
        void CancelGrapple();
        void SetSniperZoomActive(bool active, float zoomFov = 0f);
    }
}
