using System.Collections.Generic;
using System.Linq;
using Network.AntiCheat;
using UnityEngine;

namespace Game.Player.Core {
    internal sealed class PlayerMovementValidation {
        private sealed class MovementViolation {
            internal float Time;
            internal bool WasSpeedViolation;
        }

        private readonly PlayerController _player;
        private readonly List<MovementViolation> _movementViolations = new();
        private Vector3 _lastServerMovementPosition;
        private float _lastServerMovementTime;
        private bool _hasServerMovementSample;

        public PlayerMovementValidation(PlayerController player) {
            _player = player;
        }

        public float ObservedServerMovementSpeed { get; private set; }

        public void ValidateServerMovement(Vector3 position) {
            var config = AntiCheatConfig.Instance;
            if(config == null || _player.ClientNetworkTransform == null) return;

            var now = Time.time;
            if(_player.NetIsDead is { Value: true }) {
                ResetValidationSample(position, now);
                return;
            }

            if(!_hasServerMovementSample) {
                ResetValidationSample(position, now);
                return;
            }

            PruneExpiredViolations(now, config);

            var delta = position - _lastServerMovementPosition;
            var distance = delta.magnitude;
            var dt = Mathf.Max(0.0001f, now - _lastServerMovementTime);
            var adjustedPosition = position;

            TryHandleTeleportViolation(ref adjustedPosition, ref delta, ref distance, now, config);
            TryHandleSpeedViolation(ref adjustedPosition, delta, distance, dt, now, config);
            FinalizeValidationSample(adjustedPosition, now);
        }

        public void ApplyServerMovementCorrection(Vector3 correctedPosition, Quaternion correctedRotation) {
            if(_player.NetIsDead is { Value: true }) return;

            var characterController = _player.CharacterController;
            var shouldReEnableCharacterController = characterController != null && characterController.enabled;
            if(characterController != null) {
                characterController.enabled = false;
            }

            var clientNetworkTransform = _player.ClientNetworkTransform;
            if(clientNetworkTransform != null) {
                clientNetworkTransform.Teleport(correctedPosition, correctedRotation, Vector3.one);
            } else if(_player.PlayerTransform != null) {
                _player.PlayerTransform.SetPositionAndRotation(correctedPosition, correctedRotation);
            }

            var movementController = _player.MovementController;
            if(movementController != null) {
                movementController.ResetVelocity();
            }

            if(characterController != null && shouldReEnableCharacterController) {
                characterController.enabled = true;
            }
        }

        private void ResetValidationSample(Vector3 position, float now) {
            _movementViolations.Clear();
            _lastServerMovementPosition = position;
            _lastServerMovementTime = now;
            _hasServerMovementSample = true;
            ObservedServerMovementSpeed = 0f;
        }

        private void PruneExpiredViolations(float now, AntiCheatConfig config) {
            _movementViolations.RemoveAll(v => now - v.Time > config.movementViolationWindowSeconds);
        }

        private void TryHandleTeleportViolation(ref Vector3 adjustedPosition, ref Vector3 delta, ref float distance,
            float now, AntiCheatConfig config) {
            if(distance <= config.maxTeleportDistance) return;

            _movementViolations.Add(new MovementViolation { Time = now, WasSpeedViolation = false });

            var teleportViolations = _movementViolations.Count(v => !v.WasSpeedViolation);
            if(teleportViolations < config.teleportViolationThreshold) return;

            AntiCheatLogger.LogMovementEnforce(_player.OwnerClientId,
                $"teleport {distance:F1}m (limit {config.maxTeleportDistance:F1}) - {teleportViolations} violations in window");

            if(delta.sqrMagnitude > 0.0001f) {
                var clamped = _lastServerMovementPosition + delta.normalized * config.maxTeleportDistance;
                _player.ApplyServerMovementCorrectionOwnerRpc(clamped, _player.PlayerTransform.rotation);
                adjustedPosition = clamped;
                delta = clamped - _lastServerMovementPosition;
                distance = delta.magnitude;
            } else {
                adjustedPosition = _lastServerMovementPosition;
            }
        }

        private void TryHandleSpeedViolation(ref Vector3 adjustedPosition, Vector3 delta, float distance, float dt,
            float now, AntiCheatConfig config) {
            var speed = distance / dt;
            ObservedServerMovementSpeed = speed;

            if(speed > config.maxSpeedMetersPerSecond && delta.sqrMagnitude > 0.0001f) {
                _movementViolations.Add(new MovementViolation { Time = now, WasSpeedViolation = true });

                var speedViolations = _movementViolations.Count(v => v.WasSpeedViolation);
                if(speedViolations < config.speedViolationThreshold) return;

                AntiCheatLogger.LogMovementEnforce(_player.OwnerClientId,
                    $"speed {speed:F1} m/s (limit {config.maxSpeedMetersPerSecond:F1}) - {speedViolations} violations in window");

                var allowedDistance = config.maxSpeedMetersPerSecond * dt;
                var clamped = _lastServerMovementPosition + delta.normalized * allowedDistance;
                _player.ApplyServerMovementCorrectionOwnerRpc(clamped, _player.PlayerTransform.rotation);
                adjustedPosition = clamped;
                return;
            }

            if(_movementViolations.Count == 0 ||
               now - _movementViolations[^1].Time > config.movementViolationWindowSeconds * 0.5f) {
                _movementViolations.Clear();
            }
        }

        private void FinalizeValidationSample(Vector3 adjustedPosition, float now) {
            _lastServerMovementPosition = adjustedPosition;
            _lastServerMovementTime = now;
            _hasServerMovementSample = true;
        }
    }
}
