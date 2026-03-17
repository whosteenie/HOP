using System;
using System.Collections.Generic;
using Events;
using Network.Core;
using Unity.Netcode;
using UnityEngine;
using Game.Player.Core;
using Game.Spawning;
using Random = UnityEngine.Random;

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
        [SerializeField] private float moveSpeed = 5.0f; // Fallback when match settings are unavailable.

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
        private readonly Dictionary<ulong, PlayerController> _trackedPlayers = new();
        private readonly List<ulong> _staleTrackedPlayers = new();
        
        private float _timer;
        private bool _isMoving;
        private Vector3 _targetPosition;
        private bool _networkCallbacksRegistered;
        private bool _pendingTrackedPlayersRefresh;
        private float _nextTrackedPlayersRefreshTime;
        private const float TrackedPlayersRefreshIntervalSeconds = 1f;
        private const float KingTimeProgressionChunkSeconds = 1f;
        private bool _sessionOwnerCallbacksRegistered;
        private float _localKingProgressionSeconds;
        private float EffectiveMoveSpeed {
            get {
                var baseMoveSpeed = Mathf.Max(0.1f, moveSpeed);
                if(MatchSettingsManager.Instance == null) {
                    return baseMoveSpeed;
                }

                // Treat the match setting as a human-scale multiplier so a value of 1 preserves the
                // tuned default hill movement instead of collapsing it to 1 unit/sec.
                var configuredSpeedScale = MatchSettingsManager.Instance.GetKothHillSpeedMultiplier();
                return baseMoveSpeed * configuredSpeedScale;
            }
        }
        private bool HasHillAuthority => NetworkAuthority.HasGlobalAuthority(this);

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
            NetworkAuthority.TryConfigureSessionOwnerObject(this);
            RegisterSessionOwnerCallbacks();
            
            if (HasHillAuthority) {
                // Set initial random direction
                _targetPosition = Random.onUnitSphere;
                _targetPosition.y = 0; // Flatten direction
                _targetPosition.Normalize();
                
                _isMoving = true;
                RegisterNetworkCallbacks();
                RefreshTrackedPlayers();
            }

            _currentState.OnValueChanged += OnStateChanged;
            UpdateVisuals(_currentState.Value);
        }

        public override void OnNetworkDespawn() {
            FlushLocalKingProgression();
            base.OnNetworkDespawn();
            _currentState.OnValueChanged -= OnStateChanged;

            UnregisterNetworkCallbacks();
            UnregisterSessionOwnerCallbacks();

            _trackedPlayers.Clear();
            _staleTrackedPlayers.Clear();
        }

        private void RegisterSessionOwnerCallbacks() {
            if(_sessionOwnerCallbacksRegistered || NetworkManager == null) return;
            NetworkManager.OnSessionOwnerPromoted += OnSessionOwnerPromoted;
            _sessionOwnerCallbacksRegistered = true;
        }

        private void UnregisterSessionOwnerCallbacks() {
            if(!_sessionOwnerCallbacksRegistered || NetworkManager == null) return;
            NetworkManager.OnSessionOwnerPromoted -= OnSessionOwnerPromoted;
            _sessionOwnerCallbacksRegistered = false;
        }

        private void OnSessionOwnerPromoted(ulong _) {
            if(!HasHillAuthority) {
                UnregisterNetworkCallbacks();
                _isMoving = false;
                return;
            }

            NetworkAuthority.TryConfigureSessionOwnerObject(this);
            _targetPosition = Random.onUnitSphere;
            _targetPosition.y = 0f;
            _targetPosition.Normalize();
            _isMoving = true;
            RegisterNetworkCallbacks();
            RefreshTrackedPlayers();
        }

        private void OnStateChanged(HillState previous, HillState current) {
            UpdateVisuals(current);
        }

        private PlayerController _localPlayerInZone;

        private void Awake() {
            if(zoneCollider == null) {
                zoneCollider = GetComponent<Collider>();
            }
        }

        private void OnValidate() {
            if(zoneCollider == null) {
                zoneCollider = GetComponent<Collider>();
            }
        }

        private void Update() {
             // Client-side Personal KOTH time tracking
            if(_localPlayerInZone == null && PlayerController.LocalPlayer != null) {
                _localPlayerInZone = PlayerController.LocalPlayer;
            }

            TrackLocalKingProgression();

            if (!HasHillAuthority) return;
            if(_pendingTrackedPlayersRefresh || Time.unscaledTime >= _nextTrackedPlayersRefreshTime) {
                RefreshTrackedPlayers();
            }

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
                transform.position += _targetPosition * (EffectiveMoveSpeed * Time.deltaTime);

                // Force Height (Safety net against physics drift or low spawn)
                if (transform.position.y < 753f) {
                    var transformHill = transform;
                    var pos = transformHill.position;
                     pos.y = 753f;
                     transformHill.position = pos;
                }
            }

            // Control Logic
            UpdateControlState();
        }

        private void TrackLocalKingProgression() {
            var isTrackingLocalKingTime = _localPlayerInZone != null &&
                                          _localPlayerInZone.NetIsDead is { Value: false } &&
                                          IsPointInsideZone(_localPlayerInZone.transform.position);

            if(!isTrackingLocalKingTime) {
                FlushLocalKingProgression();
                return;
            }

            _localKingProgressionSeconds += Time.deltaTime;
            if(_localKingProgressionSeconds < KingTimeProgressionChunkSeconds) return;

            var wholeChunks = Mathf.Floor(_localKingProgressionSeconds / KingTimeProgressionChunkSeconds);
            var awardedSeconds = wholeChunks * KingTimeProgressionChunkSeconds;
            _localKingProgressionSeconds -= awardedSeconds;
            EventBus.Publish(new MatchKingTimeAwardedEvent(NetworkManager.Singleton.LocalClientId, awardedSeconds));
        }

        private void FlushLocalKingProgression() {
            if(_localKingProgressionSeconds <= 0f || NetworkManager.Singleton == null) {
                _localKingProgressionSeconds = 0f;
                return;
            }

            EventBus.Publish(new MatchKingTimeAwardedEvent(NetworkManager.Singleton.LocalClientId,
                _localKingProgressionSeconds));
            _localKingProgressionSeconds = 0f;
        }

        private void UpdateControlState() {
            var teamACount = 0;
            var teamBCount = 0;

            _staleTrackedPlayers.Clear();
            foreach(var (clientId, player) in _trackedPlayers) {
                if(player == null || !player.IsSpawned) {
                    _staleTrackedPlayers.Add(clientId);
                    continue;
                }

                if(player.NetIsDead.Value) continue;
                if(!IsPointInsideZone(player.transform.position)) continue;

                var teamMgr = player.TeamManager;
                if (teamMgr == null) continue;
                
                switch(teamMgr.netTeam.Value) {
                    case SpawnPoint.Team.TeamA:
                        teamACount++;
                        break;
                    case SpawnPoint.Team.TeamB:
                        teamBCount++;
                        break;
                    case SpawnPoint.Team.None:
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            if(_staleTrackedPlayers.Count > 0) {
                foreach(var clientId in _staleTrackedPlayers) {
                    _trackedPlayers.Remove(clientId);
                }
            }

            var newState = HillState.Uncontested;
            switch(teamACount) {
                case > 0 when teamBCount > 0:
                    newState = HillState.Contested;
                    break;
                case > 0:
                    newState = HillState.ControlledTeamA;
                    break;
                default: {
                    if (teamBCount > 0) {
                        newState = HillState.ControlledTeamB;
                    }

                    break;
                }
            }

            if (_currentState.Value != newState) {
                _currentState.Value = newState;
            }
        }

        private void RegisterNetworkCallbacks() {
            if(_networkCallbacksRegistered) return;

            var networkManager = NetworkManager.Singleton;
            if(networkManager == null) return;

            networkManager.OnClientConnectedCallback += OnClientConnected;
            networkManager.OnClientDisconnectCallback += OnClientDisconnected;
            _networkCallbacksRegistered = true;
        }

        private void UnregisterNetworkCallbacks() {
            if(!_networkCallbacksRegistered) return;

            var networkManager = NetworkManager.Singleton;
            if(networkManager != null) {
                networkManager.OnClientConnectedCallback -= OnClientConnected;
                networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
            }

            _networkCallbacksRegistered = false;
        }

        private void OnClientConnected(ulong clientId) {
            if(!HasHillAuthority) return;
            TrackConnectedClient(clientId);
        }

        private void OnClientDisconnected(ulong clientId) {
            if(!HasHillAuthority) return;
            _trackedPlayers.Remove(clientId);
        }

        /// <summary>Refreshes the set of tracked players from the network.</summary>
        private void RefreshTrackedPlayers() {
            var networkManager = NetworkManager.Singleton;
            if(networkManager == null) {
                _trackedPlayers.Clear();
                _pendingTrackedPlayersRefresh = false;
                _nextTrackedPlayersRefreshTime = Time.unscaledTime + TrackedPlayersRefreshIntervalSeconds;
                return;
            }

            _staleTrackedPlayers.Clear();
            foreach(var clientId in _trackedPlayers.Keys) {
                if(!networkManager.ConnectedClients.ContainsKey(clientId)) {
                    _staleTrackedPlayers.Add(clientId);
                }
            }

            if(_staleTrackedPlayers.Count > 0) {
                foreach(var clientId in _staleTrackedPlayers) {
                    _trackedPlayers.Remove(clientId);
                }

                _staleTrackedPlayers.Clear();
            }

            foreach(var clientId in networkManager.ConnectedClientsIds) {
                TrackConnectedClient(clientId);
            }

            _pendingTrackedPlayersRefresh = false;
            _nextTrackedPlayersRefreshTime = Time.unscaledTime + TrackedPlayersRefreshIntervalSeconds;
        }

        private void TrackConnectedClient(ulong clientId) {
            var networkManager = NetworkManager.Singleton;
            if(networkManager == null) return;

            if(!networkManager.ConnectedClients.TryGetValue(clientId, out var client)) {
                _trackedPlayers.Remove(clientId);
                return;
            }

            var playerObject = client.PlayerObject;
            if(playerObject == null) {
                _pendingTrackedPlayersRefresh = true;
                return;
            }

            var player = playerObject.GetComponent<PlayerController>();
            if(player == null) {
                _trackedPlayers.Remove(clientId);
                return;
            }

            _trackedPlayers[clientId] = player;
        }

        private bool IsPointInsideZone(Vector3 worldPoint) {
            if(zoneCollider == null) return false;

            var sphere = zoneCollider as SphereCollider;
            if(sphere != null) {
                Transform transformZone;
                var center = (transformZone = sphere.transform).TransformPoint(sphere.center);
                var lossy = transformZone.lossyScale;
                var maxScale = Mathf.Max(Mathf.Abs(lossy.x), Mathf.Abs(lossy.y), Mathf.Abs(lossy.z));
                var radius = sphere.radius * maxScale;
                return (worldPoint - center).sqrMagnitude <= radius * radius;
            }

            var box = zoneCollider as BoxCollider;
            if(box == null) return zoneCollider.bounds.Contains(worldPoint);
            var local = box.transform.InverseTransformPoint(worldPoint) - box.center;
            var half = box.size * 0.5f;
            return Mathf.Abs(local.x) <= half.x &&
                   Mathf.Abs(local.y) <= half.y &&
                   Mathf.Abs(local.z) <= half.z;

        }

        private void UpdateVisuals(HillState state) {
            if (visualRenderer == null) return;
            
            var targetColor = state switch {
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
            if(!HasHillAuthority) return;
            Debug.Log($"[HillController] Player {player.name} entered zone.");
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
            if(!HasHillAuthority) return;
            Debug.Log($"[HillController] Player {player.name} exited zone.");
        }

        private void OnDrawGizmos() {
            Gizmos.color = new Color(0, 1, 0, 0.3f);
            if(zoneCollider == null) return;
            var box = zoneCollider as BoxCollider;
            if(box != null) {
                Gizmos.matrix = transform.localToWorldMatrix;
                Gizmos.DrawCube(box.center, box.size);
                return;
            }

            var sphere = zoneCollider as SphereCollider;
            if(sphere != null) {
                Gizmos.matrix = transform.localToWorldMatrix;
                Gizmos.DrawSphere(sphere.center, sphere.radius);
                return;
            }

            Gizmos.DrawWireSphere(transform.position, wanderRadius);
        }
    }
}
