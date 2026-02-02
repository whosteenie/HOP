using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using Game.Player;
using Game.Spawning;

namespace Game.Match {
    /// <summary>
    /// Controls the Hill behavior: wandering and detecting players.
    /// Uses NavMeshAgent for movement, with a fallback to direct movement if NavMesh is invalid.
    /// </summary>
    public class HillController : NetworkBehaviour {
        public enum HillState {
            Uncontested,
            Contested,
            ControlledTeamA,
            ControlledTeamB
        }

        [Header("Movement Settings")]
        [SerializeField] private float wanderRadius = 5.0f; // Raycast distance
        [SerializeField] private float moveSpeed = 5.0f;

        [Header("Components")]
        [SerializeField] private NavMeshAgent navAgent;
        [SerializeField] private Collider zoneCollider;
        [SerializeField] private MeshRenderer visualRenderer;

        [Header("Visuals")]
        [SerializeField] private Color colorUncontested = Color.white;
        [SerializeField] private Color colorContested = Color.yellow;
        [SerializeField] private Color colorTeamA = Color.cyan; // Blue-ish
        [SerializeField] private Color colorTeamB = new Color(1f, 0.5f, 0f); // Orange

        // Runtime State
        private readonly NetworkVariable<HillState> _currentState = new(HillState.Uncontested);
        
        private readonly HashSet<PlayerController> _playersInZone = new();
        private float _timer;
        private bool _isMoving;
        private Vector3 _targetPosition;

        // Fallback movement
        private bool _usingFallbackMovement = false;

        public SpawnPoint.Team? ControllingTeam {
            get {
                switch (_currentState.Value) {
                    case HillState.ControlledTeamA: return SpawnPoint.Team.TeamA;
                    case HillState.ControlledTeamB: return SpawnPoint.Team.TeamB;
                    default: return null;
                }
            }
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            
            if (IsServer) {
                // Initialize Movement (Disable NavMesh if present)
                if (navAgent != null) navAgent.enabled = false;
                
                // Set initial direction
                _targetPosition = Random.onUnitSphere;
                _targetPosition.y = 0; // Flatten direction
                _targetPosition.Normalize();
                
                _isMoving = true;
            }

            _currentState.OnValueChanged += OnStateChanged;
            UpdateVisuals(_currentState.Value);
        }

        public override void OnNetworkDespawn() {
            base.OnNetworkDespawn();
            _currentState.OnValueChanged -= OnStateChanged;
        }

        private void OnStateChanged(HillState previous, HillState current) {
            UpdateVisuals(current);
        }

        private void Update() {
            if (!IsServer) return;

            // Roomba Movement Logic
            // Move forward in current direction (_targetPosition is used as direction vector here)
            if (_isMoving) {
                Vector3 currentPos = transform.position;
                Vector3 moveDir = _targetPosition;
                
                // Raycast ahead to detect walls (Enable Trigger Detection)
                Ray ray = new Ray(currentPos, moveDir);
                // Note: User specified "Bounds" layer. We use QueryTriggerInteraction.Collide to hit Triggers.
                if (Physics.Raycast(ray, out RaycastHit hit, wanderRadius, LayerMask.GetMask("Default", "Ground", "Wall", "Bounds"), QueryTriggerInteraction.Collide)) {
                    // If we are close to a wall, reflect direction
                    if (hit.distance < 2.0f) {
                        Vector3 reflectDir = Vector3.Reflect(moveDir, hit.normal);
                        reflectDir.y = 0; // Keep flat
                        _targetPosition = reflectDir.normalized;
                    }
                }
                
                // Move
                transform.position += _targetPosition * (moveSpeed * Time.deltaTime);

                // Force Height (Safety net against physics drift or low spawn)
                if (transform.position.y < 753f) {
                     Vector3 pos = transform.position;
                     pos.y = 753f;
                     transform.position = pos;
                }
            }

            // Control Logic
            UpdateControlState();
        }

        // Unused but kept for interface compatibility if needed later
        private void SetNewDestination() { }

        private void UpdateControlState() {
            // Clean up nulls
            _playersInZone.RemoveWhere(p => p == null || !p.IsSpawned || p.netIsDead.Value);

            int teamACount = 0;
            int teamBCount = 0;

            foreach (var player in _playersInZone) {
                var teamMgr = player.TeamManager;
                if (teamMgr == null) continue;
                
                if (teamMgr.netTeam.Value == SpawnPoint.Team.TeamA) teamACount++;
                else if (teamMgr.netTeam.Value == SpawnPoint.Team.TeamB) teamBCount++;
            }

            HillState newState = HillState.Uncontested;
            if (teamACount > 0 && teamBCount > 0) {
                newState = HillState.Contested;
            } else if (teamACount > 0) {
                newState = HillState.ControlledTeamA;
            } else if (teamBCount > 0) {
                newState = HillState.ControlledTeamB;
            }

            if (_currentState.Value != newState) {
                _currentState.Value = newState;
            }
        }

        private void UpdateVisuals(HillState state) {
            if (visualRenderer == null) return;
            
            Color targetColor = state switch {
                HillState.Contested => colorContested,
                HillState.ControlledTeamA => colorTeamA,
                HillState.ControlledTeamB => colorTeamB,
                _ => colorUncontested
            };

            // Assuming material has color property. If using custom shader, might need property block.
            // Using material.color for standard shader support, or PropertyBlock for optimization
            visualRenderer.material.color = targetColor; 
            
            // If custom shader uses _BaseColor or event emission
            if (visualRenderer.material.HasProperty("_BaseColor"))
                visualRenderer.material.SetColor("_BaseColor", targetColor);
             if (visualRenderer.material.HasProperty("_EmissionColor"))
                visualRenderer.material.SetColor("_EmissionColor", targetColor * 1.5f);
        }

        private void OnTriggerEnter(Collider other) {
            if (!IsServer) return;
            
            var player = other.GetComponent<PlayerController>();
            if (player == null) player = other.GetComponentInParent<PlayerController>(); // Check parent if collider is on child part
            
            if (player != null) {
                Debug.Log($"[HillController] Player {player.name} entered zone.");
                _playersInZone.Add(player);
            }
        }

        private void OnTriggerExit(Collider other) {
            if (!IsServer) return;
            
            var player = other.GetComponent<PlayerController>();
            if (player == null) player = other.GetComponentInParent<PlayerController>();

            if (player != null) {
                Debug.Log($"[HillController] Player {player.name} exited zone.");
                _playersInZone.Remove(player);
            }
        }

        private void OnDrawGizmos() {
            Gizmos.color = new Color(0, 1, 0, 0.3f);
            if (zoneCollider != null) {
                if (zoneCollider is BoxCollider box) {
                    Gizmos.matrix = transform.localToWorldMatrix;
                    Gizmos.DrawCube(box.center, box.size);
                } else if (zoneCollider is SphereCollider sphere) {
                    Gizmos.matrix = transform.localToWorldMatrix;
                    Gizmos.DrawSphere(sphere.center, sphere.radius);
                } else {
                     Gizmos.DrawWireSphere(transform.position, wanderRadius);
                }
            }
        }
    }
}
