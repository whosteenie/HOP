using System.Collections.Generic;
using System;
using System.Linq;
using Network.AntiCheat;
using Network.Diagnostics;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    public class PlayerRagdoll : NetworkBehaviour {
        [Header("References")]
        [SerializeField] private PlayerController playerController;

        private CharacterController _characterController;
        private Animator _playerAnimator;

        [Header("Ragdoll Settings")]
        private const float RagdollForce = 60f;

        [Header("Ragdoll Force Target")]
        [Tooltip("Rigidbody to apply ragdoll force to (typically the chest/spine/torso).")]
        [SerializeField] private Rigidbody chestRigidbody;

        [Header("Body Part Tags")]
        [Tooltip("Tag used for head body part (for headshot detection).")]
        #pragma warning disable CS0414 // Field is assigned but never used (reserved for future headshot feature)
        [SerializeField] private string headTag = "Head";
        #pragma warning restore CS0414
        
        [Header("Combat Rewind")]
        [SerializeField] private float rewindHistoryDurationSeconds = 0.25f;
        [SerializeField] private float rewindCaptureIntervalSeconds = 1f / 60f;
        private const float DefaultRewindHistoryDurationSeconds = 0.25f;
        private const float DefaultRewindCaptureIntervalSeconds = 1f / 60f;

        private Rigidbody[] _ragdollRigidbodies;
        private CharacterJoint[] _ragdollJoints;
        private Collider[] _ragdollColliders;
        private Vector3 _hitPoint;
        private Vector3 _hitDir;
        private Transform[] _rewindHitboxTransforms;
        private readonly List<HitboxPoseSnapshot> _hitboxHistory = new();
        private float _lastHitboxSnapshotTime = float.NegativeInfinity;

        public sealed class HitboxPoseSnapshot {
            public float ServerTime;
            public Vector3[] Positions;
            public Quaternion[] Rotations;
        }

        /// <summary>
        /// Returns whether the player is currently in ragdoll state.
        /// </summary>
        public bool IsRagdoll { get; private set; }

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(playerController == null) {
                Debug.LogError("[PlayerRagdoll] PlayerController not found!");
                enabled = false;
                return;
            }

            if(_characterController == null) _characterController = playerController.CharacterController;
            if(_playerAnimator == null) _playerAnimator = playerController.PlayerAnimator;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            _ragdollRigidbodies = GetComponentsInChildren<Rigidbody>(true);
            _ragdollJoints = GetComponentsInChildren<CharacterJoint>(true);
            _ragdollColliders = GetComponentsInChildren<Collider>(true);

            // Set ragdoll components to Enemy layer (excluding base GameObject)
            SetRagdollLayersToEnemy();

            DisableRagdoll();
            
            // Enable colliders for hit detection (even when ragdoll is disabled)
            EnableCollidersForHitDetection();
            RefreshRewindHitboxTransforms();
        }

        /// <summary>
        /// Enables the ragdoll effect on the player.
        /// </summary>
        public void EnableRagdoll(Vector3? hitPoint = null, Vector3? hitDirection = null, string bodyPartTag = null) {
            if(IsRagdoll) return;
            IsRagdoll = true;
            FlowLog.Emit(FlowEventIds.PlayerRagdollState,
                ("player", OwnerClientId),
                ("state", "Enabled"),
                ("reason", string.IsNullOrEmpty(bodyPartTag) ? "Damage" : bodyPartTag));

            _characterController.enabled = false;
            _playerAnimator.enabled = false;
            EnableRagdollPhysics();

            if(!hitPoint.HasValue || !hitDirection.HasValue) return;
            _hitPoint = hitPoint.Value;
            _hitDir = hitDirection.Value;
            ApplyHitForce(bodyPartTag);
        }

        /// <summary>
        /// Applies a hit force to the ragdoll.
        /// </summary>
        private void ApplyHitForce(string bodyPartTag = null) {
            // Always apply force to chest/torso for consistent, predictable ragdoll behavior
            // bodyPartTag is still used for headshot damage calculation, just not for force direction
            if(chestRigidbody != null) {
                chestRigidbody.AddForce(_hitDir * RagdollForce, ForceMode.Impulse);
            } else {
                // Fallback if not assigned (shouldn't happen if inspector is set up correctly)
                var fallback = GetClosestRigidbody(_hitPoint);
                if(fallback != null) {
                    fallback.AddForce(_hitDir * RagdollForce, ForceMode.Impulse);
                }
            }
        }

        /// <summary>
        /// Gets a rigidbody by its GameObject tag (e.g., "Head" for headshots).
        /// </summary>
        private Rigidbody GetRigidbodyByTag(string rbTag) {
            return _ragdollRigidbodies.FirstOrDefault(rb => rb != null && rb.CompareTag(rbTag));
        }

        private Rigidbody GetClosestRigidbody(Vector3 point) {
            Rigidbody closest = null;
            var bestDist = float.MaxValue;
            foreach(var rb in _ragdollRigidbodies) {
                if(rb == null) continue;
                var d = Vector3.Distance(rb.worldCenterOfMass, point);
                if(!(d < bestDist)) continue;
                bestDist = d;
                closest = rb;
            }

            return closest;
        }

        /// <summary>
        /// Disables the ragdoll effect and returns the player to the normal state.
        /// </summary>
        public void DisableRagdoll() {
            IsRagdoll = false;
            FlowLog.Emit(FlowEventIds.PlayerRagdollState,
                ("player", OwnerClientId),
                ("state", "Disabled"),
                ("reason", "RespawnOrReset"));

            DisableRagdollPhysics();

            if(_characterController != null) {
                _characterController.enabled = true;
            }
            if(_playerAnimator != null) {
                _playerAnimator.enabled = true;
            }
            
            // Ensure colliders are enabled for hit detection after disabling ragdoll
            EnableCollidersForHitDetection();
        }

        private void LateUpdate() {
            CaptureHitboxHistoryServer();
        }

        private void EnableRagdollPhysics() {
            foreach(var rb in _ragdollRigidbodies) {
                if(rb == null) continue;
                rb.isKinematic = false; // Make non-kinematic for physics interactions
                rb.linearVelocity = Vector3.zero;
                rb.detectCollisions = true; // Keep true for raycast hit detection
                rb.useGravity = true;
            }

            foreach(var col in _ragdollColliders) {
                if(col == null) continue;
                col.enabled = true;
            }

            foreach(var joint in _ragdollJoints) {
                if(joint == null) continue;
                joint.enableCollision = true;
            }
        }

        /// <summary>
        /// Disables physics on the ragdoll components.
        /// </summary>
        private void DisableRagdollPhysics() {
            foreach(var rb in _ragdollRigidbodies) {
                if(rb == null) continue;
                // Unity warns if velocity is written while already kinematic.
                if(!rb.isKinematic) {
                    rb.linearVelocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
                rb.isKinematic = true; // Make kinematic to prevent physics interactions
                rb.detectCollisions = true; // Keep true for raycast hit detection
                rb.useGravity = false;
            }

            // Keep colliders enabled for hit detection, but disable physics collisions via kinematic
            // Colliders are enabled in EnableCollidersForHitDetection()
            // Rigidbodies are kinematic so they don't interact with physics, but raycasts still work

            foreach(var joint in _ragdollJoints) {
                if(joint == null) continue;
                joint.enableCollision = false;
            }
        }

        /// <summary>
        /// Enables ragdoll colliders for hit detection (raycasts).
        /// Colliders remain enabled even when ragdoll is disabled so bullets can hit them.
        /// </summary>
        private void EnableCollidersForHitDetection() {
            if(_ragdollColliders == null) return;
            
            foreach(var col in _ragdollColliders) {
                if(col == null) continue;
                col.enabled = true; // Enable for raycast hit detection
                col.isTrigger = false; // Ensure it's not a trigger (triggers don't block raycasts)
            }
        }

        /// <summary>
        /// Sets all ragdoll components (rigidbodies and colliders) to Enemy layer for OTHER players.
        /// Excludes the base GameObject (the one with CharacterController).
        /// Only sets layers for non-owner players (other players are enemies).
        /// </summary>
        private void SetRagdollLayersToEnemy() {
            // Only set Enemy layer for other players, not the local player
            if(IsOwner) {
                return;
            }

            var enemyLayer = LayerMask.NameToLayer("Enemy");
            if(enemyLayer == -1) {
                Debug.LogWarning("[PlayerRagdoll] Enemy layer not found. Make sure 'Enemy' layer exists in project settings.");
                return;
            }

            // Get base GameObject (the one with CharacterController)
            var baseGameObject = _characterController != null ? _characterController.gameObject : gameObject;

            // Set rigidbody GameObjects to Enemy layer (excluding base GameObject)
            foreach(var rb in _ragdollRigidbodies) {
                if(rb != null && rb.gameObject != baseGameObject) {
                    rb.gameObject.layer = enemyLayer;
                }
            }

            // Set collider GameObjects to Enemy layer (excluding base GameObject)
            foreach(var col in _ragdollColliders) {
                if(col != null && col.gameObject != baseGameObject) {
                    col.gameObject.layer = enemyLayer;
                }
            }
        }

        public HitboxPoseSnapshot CaptureCurrentHitboxPose() {
            if(_rewindHitboxTransforms == null || _rewindHitboxTransforms.Length == 0) {
                RefreshRewindHitboxTransforms();
            }

            if(_rewindHitboxTransforms == null || _rewindHitboxTransforms.Length == 0) {
                return null;
            }

            var snapshot = new HitboxPoseSnapshot {
                Positions = new Vector3[_rewindHitboxTransforms.Length],
                Rotations = new Quaternion[_rewindHitboxTransforms.Length]
            };

            for(var i = 0; i < _rewindHitboxTransforms.Length; i++) {
                var hitboxTransform = _rewindHitboxTransforms[i];
                if(hitboxTransform == null) {
                    snapshot.Positions[i] = Vector3.zero;
                    snapshot.Rotations[i] = Quaternion.identity;
                    continue;
                }

                snapshot.Positions[i] = hitboxTransform.position;
                snapshot.Rotations[i] = hitboxTransform.rotation;
            }

            return snapshot;
        }

        public bool TryGetHistoricalHitboxPose(float serverTime, out HitboxPoseSnapshot snapshot) {
            snapshot = null;
            if(_hitboxHistory.Count == 0) {
                return false;
            }

            if(serverTime <= _hitboxHistory[0].ServerTime) {
                snapshot = CloneSnapshot(_hitboxHistory[0]);
                return snapshot != null;
            }

            var lastIndex = _hitboxHistory.Count - 1;
            if(serverTime >= _hitboxHistory[lastIndex].ServerTime) {
                snapshot = CloneSnapshot(_hitboxHistory[lastIndex]);
                return snapshot != null;
            }

            for(var i = 1; i < _hitboxHistory.Count; i++) {
                var newer = _hitboxHistory[i];
                if(serverTime > newer.ServerTime) {
                    continue;
                }

                var older = _hitboxHistory[i - 1];
                var delta = newer.ServerTime - older.ServerTime;
                if(delta <= 0.0001f) {
                    snapshot = CloneSnapshot(newer);
                    return snapshot != null;
                }

                var t = Mathf.Clamp01((serverTime - older.ServerTime) / delta);
                snapshot = InterpolateSnapshot(older, newer, t, serverTime);
                return snapshot != null;
            }

            return false;
        }

        public void ApplyHitboxPose(HitboxPoseSnapshot snapshot) {
            if(snapshot == null || snapshot.Positions == null || snapshot.Rotations == null) return;
            if(_rewindHitboxTransforms == null || _rewindHitboxTransforms.Length == 0) {
                RefreshRewindHitboxTransforms();
            }

            if(_rewindHitboxTransforms == null || snapshot.Positions.Length != _rewindHitboxTransforms.Length ||
               snapshot.Rotations.Length != _rewindHitboxTransforms.Length) {
                return;
            }

            for(var i = 0; i < _rewindHitboxTransforms.Length; i++) {
                var hitboxTransform = _rewindHitboxTransforms[i];
                if(hitboxTransform == null) {
                    continue;
                }

                hitboxTransform.SetPositionAndRotation(snapshot.Positions[i], snapshot.Rotations[i]);
            }
        }

        private void CaptureHitboxHistoryServer() {
            if(!IsServer || !IsSpawned || IsRagdoll) return;
            if(playerController != null && playerController.IsDead) return;

            var serverTime = NetworkManager != null ? NetworkManager.ServerTime.TimeAsFloat : Time.time;
            var captureInterval = GetRewindCaptureIntervalSeconds();
            if(serverTime - _lastHitboxSnapshotTime < captureInterval) {
                return;
            }

            var snapshot = CaptureCurrentHitboxPose();
            if(snapshot == null) {
                return;
            }

            snapshot.ServerTime = serverTime;
            _hitboxHistory.Add(snapshot);
            _lastHitboxSnapshotTime = serverTime;
            TrimHitboxHistory(snapshot.ServerTime);
        }

        private void RefreshRewindHitboxTransforms() {
            if(_ragdollRigidbodies == null || _ragdollRigidbodies.Length == 0) {
                _rewindHitboxTransforms = null;
                _hitboxHistory.Clear();
                return;
            }

            var baseGameObject = _characterController != null ? _characterController.gameObject : gameObject;
            var hitboxTransforms = new List<Transform>(_ragdollRigidbodies.Length);
            foreach(var rb in _ragdollRigidbodies) {
                if(rb == null || rb.gameObject == baseGameObject) {
                    continue;
                }

                hitboxTransforms.Add(rb.transform);
            }

            if(_rewindHitboxTransforms != null && _rewindHitboxTransforms.Length != hitboxTransforms.Count) {
                _hitboxHistory.Clear();
            }

            _rewindHitboxTransforms = hitboxTransforms.ToArray();
        }

        private void TrimHitboxHistory(float now) {
            var maxAge = Mathf.Max(DefaultRewindHistoryDurationSeconds, GetRewindHistoryDurationSeconds());
            while(_hitboxHistory.Count > 0 && now - _hitboxHistory[0].ServerTime > maxAge) {
                _hitboxHistory.RemoveAt(0);
            }
        }

        private float GetRewindHistoryDurationSeconds() {
            var config = AntiCheatConfig.Instance;
            if(config != null && config.combatRewindHistorySeconds > 0f) {
                return config.combatRewindHistorySeconds;
            }

            return rewindHistoryDurationSeconds > 0f
                ? rewindHistoryDurationSeconds
                : DefaultRewindHistoryDurationSeconds;
        }

        private float GetRewindCaptureIntervalSeconds() {
            var config = AntiCheatConfig.Instance;
            if(config != null && config.combatRewindCaptureIntervalSeconds > 0f) {
                return config.combatRewindCaptureIntervalSeconds;
            }

            return rewindCaptureIntervalSeconds > 0f
                ? rewindCaptureIntervalSeconds
                : DefaultRewindCaptureIntervalSeconds;
        }

        private static HitboxPoseSnapshot CloneSnapshot(HitboxPoseSnapshot source) {
            if(source == null || source.Positions == null || source.Rotations == null) {
                return null;
            }

            var positions = new Vector3[source.Positions.Length];
            var rotations = new Quaternion[source.Rotations.Length];
            Array.Copy(source.Positions, positions, source.Positions.Length);
            Array.Copy(source.Rotations, rotations, source.Rotations.Length);
            return new HitboxPoseSnapshot {
                ServerTime = source.ServerTime,
                Positions = positions,
                Rotations = rotations
            };
        }

        private static HitboxPoseSnapshot InterpolateSnapshot(HitboxPoseSnapshot older, HitboxPoseSnapshot newer,
            float t, float serverTime) {
            if(older == null || newer == null || older.Positions == null || newer.Positions == null ||
               older.Rotations == null || newer.Rotations == null) {
                return null;
            }

            if(older.Positions.Length != newer.Positions.Length || older.Rotations.Length != newer.Rotations.Length ||
               older.Positions.Length != older.Rotations.Length) {
                return null;
            }

            var positions = new Vector3[older.Positions.Length];
            var rotations = new Quaternion[older.Rotations.Length];
            for(var i = 0; i < positions.Length; i++) {
                positions[i] = Vector3.Lerp(older.Positions[i], newer.Positions[i], t);
                rotations[i] = Quaternion.Slerp(older.Rotations[i], newer.Rotations[i], t);
            }

            return new HitboxPoseSnapshot {
                ServerTime = serverTime,
                Positions = positions,
                Rotations = rotations
            };
        }
    }
}
