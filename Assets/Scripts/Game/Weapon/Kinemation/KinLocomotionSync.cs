using System.Reflection;
using KINEMATION.FPSAnimationPack.Scripts.Player;
using UnityEngine;

namespace Game.Weapons.Kinemation {
    /// <summary>Syncs locomotion (move/look/sprint/air) from game state into the KIN FPSPlayer viewmodel. Uses reflection for FPSPlayer internals.</summary>
    internal sealed class KinLocomotionSync {
        private static readonly FieldInfo FpsPlayerMoveInputField =
            typeof(FPSPlayer).GetField("_moveInput", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FpsPlayerLookInputField =
            typeof(FPSPlayer).GetField("_lookInput", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FpsPlayerSprintingField =
            typeof(FPSPlayer).GetField("_bSprinting", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FpsPlayerTacSprintingField =
            typeof(FPSPlayer).GetField("_bTacSprinting", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly int IsInAirHash = Animator.StringToHash("IsInAir");

        private readonly IKinDriverResolverContext _context;
        private readonly bool _freezeLocomotionInAir;
        private readonly bool _forceWalkAnimationWhileSprinting;
        private readonly float _sprintWalkGaitValue;
        private readonly bool _syncLookPitchWithPlayer;
        private readonly bool _syncAirborneState;

        public KinLocomotionSync(IKinDriverResolverContext context,
            bool freezeLocomotionInAir, bool forceWalkAnimationWhileSprinting, float sprintWalkGaitValue,
            bool syncLookPitchWithPlayer, bool syncAirborneState) {
            _context = context;
            _freezeLocomotionInAir = freezeLocomotionInAir;
            _forceWalkAnimationWhileSprinting = forceWalkAnimationWhileSprinting;
            _sprintWalkGaitValue = Mathf.Clamp(sprintWalkGaitValue, 0f, 1.99f);
            _syncLookPitchWithPlayer = syncLookPitchWithPlayer;
            _syncAirborneState = syncAirborneState;
        }

        public void SyncLocomotion(Vector2 moveInput, bool sprinting, bool tacticalSprinting, bool isGrounded,
            float lookPitchDegrees) {
            var fpsPlayer = _context.FpsPlayer;
            var fpsAnimator = _context.FpsAnimator;
            if(fpsPlayer == null) return;

            if(_freezeLocomotionInAir && !isGrounded) {
                moveInput = Vector2.zero;
                sprinting = false;
                tacticalSprinting = false;
            }

            if(_forceWalkAnimationWhileSprinting && (sprinting || tacticalSprinting)) {
                if(moveInput.sqrMagnitude > 0.0001f && _sprintWalkGaitValue > 0f) {
                    var moveDir = moveInput.normalized;
                    var moveMag = Mathf.Max(moveInput.magnitude, _sprintWalkGaitValue);
                    moveInput = moveDir * Mathf.Min(moveMag, 1.99f);
                }
                sprinting = false;
                tacticalSprinting = false;
            }

            FpsPlayerMoveInputField?.SetValue(fpsPlayer, moveInput);
            FpsPlayerLookInputField?.SetValue(fpsPlayer,
                _syncLookPitchWithPlayer ? new Vector2(0f, -lookPitchDegrees) : Vector2.zero);
            FpsPlayerSprintingField?.SetValue(fpsPlayer, sprinting);
            FpsPlayerTacSprintingField?.SetValue(fpsPlayer, tacticalSprinting);

            if(fpsAnimator != null)
                fpsAnimator.SetBool(IsInAirHash, _syncAirborneState && !isGrounded);
        }
    }
}

