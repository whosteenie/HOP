using System.Collections.Generic;
using System.Linq;
using Network.AntiCheat;
using UnityEngine;

namespace Game.Player.Core {
    internal sealed class PlayerMovementValidationCoordinator {
        private sealed class MovementViolation {
            public float Time;
            public bool WasSpeedViolation;
        }

        private readonly PlayerController _player;
        private readonly List<MovementViolation> _movementViolations = new();
        private Vector3 _lastServerMovementPosition;
        private float _lastServerMovementTime;
        private bool _hasServerMovementSample;

        public PlayerMovementValidationCoordinator(PlayerController player) {
            _player = player;
        }

        public float ObservedServerMovementSpeed { get; private set; }

        public void ValidateServerMovement(Vector3 position) {
            var config = AntiCheatConfig.Instance;
            if(config == null || _player.ClientNetworkTransform == null) return;

            var now = Time.time;
            if(_player.NetIsDead is { Value: true }) {
                _movementViolations.Clear();
                _lastServerMovementPosition = position;
                _lastServerMovementTime = now;
                _hasServerMovementSample = true;
                ObservedServerMovementSpeed = 0f;
                return;
            }

            if(!_hasServerMovementSample) {
                _lastServerMovementPosition = position;
                _lastServerMovementTime = now;
                _hasServerMovementSample = true;
                ObservedServerMovementSpeed = 0f;
                return;
            }

            _movementViolations.RemoveAll(v => now - v.Time > config.movementViolationWindowSeconds);

            var delta = position - _lastServerMovementPosition;
            var distance = delta.magnitude;
            var dt = Mathf.Max(0.0001f, now - _lastServerMovementTime);
            var adjustedPosition = position;

            if(distance > config.maxTeleportDistance) {
                _movementViolations.Add(new MovementViolation { Time = now, WasSpeedViolation = false });

                var teleportViolations = _movementViolations.Count(v => !v.WasSpeedViolation);
                if(teleportViolations >= config.teleportViolationThreshold) {
                    AntiCheatLogger.LogMovementEnforcement(_player.OwnerClientId,
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
            }

            var speed = distance / dt;
            ObservedServerMovementSpeed = speed;
            if(speed > config.maxSpeedMetersPerSecond && delta.sqrMagnitude > 0.0001f) {
                _movementViolations.Add(new MovementViolation { Time = now, WasSpeedViolation = true });

                var speedViolations = _movementViolations.Count(v => v.WasSpeedViolation);
                if(speedViolations >= config.speedViolationThreshold) {
                    AntiCheatLogger.LogMovementEnforcement(_player.OwnerClientId,
                        $"speed {speed:F1} m/s (limit {config.maxSpeedMetersPerSecond:F1}) - {speedViolations} violations in window");

                    var allowedDistance = config.maxSpeedMetersPerSecond * dt;
                    var clamped = _lastServerMovementPosition + delta.normalized * allowedDistance;
                    _player.ApplyServerMovementCorrectionOwnerRpc(clamped, _player.PlayerTransform.rotation);
                    adjustedPosition = clamped;
                }
            } else if(_movementViolations.Count == 0 ||
                      now - _movementViolations[^1].Time > config.movementViolationWindowSeconds * 0.5f) {
                _movementViolations.Clear();
            }

            _lastServerMovementPosition = adjustedPosition;
            _lastServerMovementTime = now;
            _hasServerMovementSample = true;
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
    }
}
