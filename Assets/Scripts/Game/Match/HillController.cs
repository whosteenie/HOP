using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using Game.Player;
using Game.Spawning;

namespace Game.Match {
    /// <summary>
    /// Controls the Hill behavior: wandering and detecting players.
    /// Uses a simple bounds reflection system for movement (bounces off walls like a Roomba).
    /// </summary>
    public class HillController : NetworkBehaviour {
        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");

        private enum HillState {
            Uncontested,
            Contested,
            ControlledTeamA,
            ControlledTeamB
        }

        [Header("Movement Settings")]
        [SerializeField] private float wanderRadius = 5.0f; // Raycast distance for wall detection
        [SerializeField] private float moveSpeed = 5.0f;

        [Header("Components")]
        [SerializeField] private Collider zoneCollider;
        [SerializeField] private MeshRenderer visualRenderer;

        [Header("Visuals")]
        [SerializeField] private Color colorUncontested = Color.white;
        [SerializeField] private Color colorContested = Color.yellow;
        [SerializeField] private Color colorTeamA = Color.cyan; // Blue-ish
        [SerializeField] private Color colorTeamB = new(1f, 0.5f, 0f); // Orange

        // Runtime State
        private readonly NetworkVariable<HillState> _currentState = new();
        
        private readonly HashSet<PlayerController> _playersInZone = new();
        private float _timer;
        private bool _isMoving;
        private Vector3 _targetPosition;

        public SpawnPoint.Team? ControllingTeam {
            get {
                return _currentState.Value switch {
                    HillState.ControlledTeamA => SpawnPoint.Team.TeamA,
                    HillState.ControlledTeamB => SpawnPoint.Team.TeamB,
                    _ => null
                };
            }
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            
            if (IsServer) {
                // Set initial random direction
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

        private PlayerController _localPlayerInZone;

        private void Update() {
             // Client-side Personal KOTH time tracking
            if (_localPlayerInZone != null && 
                _localPlayerInZone.netIsDead != null && 
                !_localPlayerInZone.netIsDead.Value && 
                Progression.ProgressionManager.Instance != null) {
                 Progression.ProgressionManager.Instance.AddTimeAsKing(Time.deltaTime);
            }

            if (!IsServer) return;

            // Roomba Movement Logic
            // Move forward in current direction (_targetPosition is used as direction vector here)
            if (_isMoving) {
                var currentPos = transform.position;
                var moveDir = _targetPosition;
                
                // Raycast ahead to detect walls (Enable Trigger Detection)
                var ray = new Ray(currentPos, moveDir);
                // Note: User specified "Bounds" layer. We use QueryTriggerInteraction.Collide to hit Triggers.
                if (Physics.Raycast(ray, out var hit, wanderRadius, LayerMask.GetMask("Bounds"), 
                        QueryTriggerInteraction.Collide)) {
                    // If we are close to a wall, reflect direction
                    if (hit.distance < 2.0f) {
                        var reflectDir = Vector3.Reflect(moveDir, hit.normal);
                        reflectDir.y = 0; // Flatten direction
                        _targetPosition = reflectDir.normalized;
                    }
                }
                
                // Move
                transform.position += _targetPosition * (moveSpeed * Time.deltaTime);

                // Force Height (Safety net against physics drift or low spawn)
                if (transform.position.y < 753f) {
                     var pos = transform.position;
                     pos.y = 753f;
                     transform.position = pos;
                }
            }

            // Control Logic
            UpdateControlState();
        }

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
            if (visualRenderer.material.HasProperty(BaseColor))
                visualRenderer.material.SetColor(BaseColor, targetColor);
            if (visualRenderer.material.HasProperty(EmissionColor))
                visualRenderer.material.SetColor(EmissionColor, targetColor * 1.5f);
        }

        private void OnTriggerEnter(Collider other) {
            var player = other.GetComponent<PlayerController>();
            if (player == null) player = other.GetComponentInParent<PlayerController>(); // Check parent if collider is on child part

            if(player == null) return;
            // Client-side check for local player
            if (player.IsOwner) {
                _localPlayerInZone = player;
            }

            // Server-side logic
            if(!IsServer) return;
            Debug.Log($"[HillController] Player {player.name} entered zone.");
            _playersInZone.Add(player);
        }

        private void OnTriggerExit(Collider other) {
            var player = other.GetComponent<PlayerController>();
            if (player == null) player = other.GetComponentInParent<PlayerController>();

            if(player == null) return;
            // Client-side check for local player
            if (player.IsOwner) {
                _localPlayerInZone = null;
            }

            // Server-side logic
            if(!IsServer) return;
            Debug.Log($"[HillController] Player {player.name} exited zone.");
            _playersInZone.Remove(player);
        }

        private void OnDrawGizmos() {
            Gizmos.color = new Color(0, 1, 0, 0.3f);
            if(zoneCollider == null) return;
            switch(zoneCollider) {
                case BoxCollider box:
                    Gizmos.matrix = transform.localToWorldMatrix;
                    Gizmos.DrawCube(box.center, box.size);
                    break;
                case SphereCollider sphere:
                    Gizmos.matrix = transform.localToWorldMatrix;
                    Gizmos.DrawSphere(sphere.center, sphere.radius);
                    break;
                default:
                    Gizmos.DrawWireSphere(transform.position, wanderRadius);
                    break;
            }
        }
    }
}
