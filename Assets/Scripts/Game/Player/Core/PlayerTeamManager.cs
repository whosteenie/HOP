using System.Collections;
using Game.Match;
using Game.Player.Combat;
using Game.Spawning;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player.Core {
    [RequireComponent(typeof(PlayerController))]
    public class PlayerTeamManager : NetworkBehaviour {
        public static int OutlineColorID { get; } = Shader.PropertyToID("_OutlineColor");

        private static readonly int Size = Shader.PropertyToID("_Size");
        [SerializeField] private PlayerController playerController;

        // --------------------------------------------------------------------
        // 1. Networked team (synced once at spawn)
        // --------------------------------------------------------------------
        public NetworkVariable<SpawnPoint.Team> netTeam = new();

        // --------------------------------------------------------------------
        // 2. Outline colours (tweak in inspector)
        // --------------------------------------------------------------------
        [Header("Outline Colours")]
        [ColorUsage(true, true)] // HDR enabled: showAlpha=true, hdr=true
        [SerializeField]
        private Color teammateOutline = new(0f, 1.5f, 2.5f, 1f); // Bright cyan-blue with HDR glow

        [ColorUsage(true, true)] // HDR enabled
        [SerializeField]
        private Color enemyOutline = new(2.5f, 0.5f, 0.5f, 1f); // Bright red with HDR glow

        [ColorUsage(true, true)] // HDR enabled
        [SerializeField]
        private Color taggedGlow = new(8f, 6f, 1f, 1f); // Very bright yellow-orange with HDR glow for tagged players
        public Color TaggedGlow => taggedGlow;

        [Header("Outline Distance Scaling")]
        [SerializeField] private float minOutlineSize = 0.008f; // Minimum size (close distance)

        [SerializeField]
        private float maxOutlineSize = 0.025f; // Maximum size (far distance) - much thicker at distance

        [SerializeField] private float distanceScaleStart = 10f; // Start scaling at this distance
        [SerializeField] private float distanceScaleEnd = 100f; // Max scaling at this distance

        // --------------------------------------------------------------------
        // 3. Cached components
        // --------------------------------------------------------------------
        private SkinnedMeshRenderer _skinned;
        private MaterialPropertyBlock _propertyBlock;
        private MaterialPropertyBlock _tagPropertyBlock; // Reusable property block for tagged players
        private PlayerTagController _tagController;
        private Camera _mainCamera; // Cached main camera reference

        // Cache MatchSettingsManager and game mode to avoid repeated lookups
        private MatchSettingsManager _cachedMatchSettings;
        private string _cachedGameModeId;
        private bool _cachedIsTeamBased;
        private bool _cachedIsTagMode;
        private bool _gameModeCacheValid;

        // Cache last outline size to avoid GetPropertyBlock every frame
        private float _lastOutlineSize = -1f;
        private Camera _camera;

        // --------------------------------------------------------------------
        // Unity / Netcode lifecycle
        // --------------------------------------------------------------------
        private void Start() {
            _camera = Camera.main;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            // Get the correct SkinnedMeshRenderer from PlayerController (the body mesh, not FP arm)
            if(playerController == null)
                playerController = GetComponent<PlayerController>();
            
            if(playerController == null) {
                Debug.LogError($"[PlayerTeamManager] PlayerController not found! GameObject: {gameObject.name}");
                enabled = false;
                return;
            }

            _skinned = playerController.PlayerMesh;
            if(_skinned == null) {
                Debug.LogError($"[PlayerTeamManager] PlayerController.PlayerMesh is null! GameObject: {gameObject.name}");
                enabled = false;
                return;
            }

            // Find and cache main camera once (for dynamically spawned prefabs)
            _mainCamera = Camera.main;

            _tagController = GetComponent<PlayerTagController>();

            // Cache MatchSettingsManager
            _cachedMatchSettings = MatchSettingsManager.Instance;
            _gameModeCacheValid = false;

            // Initialize MaterialPropertyBlock for per-instance properties
            if(_skinned != null) {
                _propertyBlock = new MaterialPropertyBlock();
                _skinned.GetPropertyBlock(_propertyBlock, 0);
                _tagPropertyBlock = new MaterialPropertyBlock();
            }

            netTeam.OnValueChanged -= OnTeamChanged;
            netTeam.OnValueChanged += OnTeamChanged;

            // Delay outline update to ensure all teams are synced
            StartCoroutine(DelayedOutlineUpdate());
        }

        private IEnumerator DelayedOutlineUpdate() {
            // Wait a frame to ensure network variables are synced
            yield return null;
            UpdateOutlineColour();

            // Also update when local player's team is set (if we're the local player)
            if(!IsOwner) yield break;
            // Update all other players' outlines when our team is set
            UpdateAllPlayerOutlines();
        }

        private void UpdateAllPlayerOutlines() {
            // Update all other spawned players' outline controllers.
            foreach(var controller in PlayerController.SpawnedPlayers) {
                if(controller == null) continue;
                var player = controller.TeamManager;
                if(player == null || player == this) continue;
                player.UpdateOutlineColour();
            }
        }

        public override void OnNetworkDespawn() {
            netTeam.OnValueChanged -= OnTeamChanged;
            base.OnNetworkDespawn();
        }

        // --------------------------------------------------------------------
        // Called whenever NetTeam changes (including on spawn)
        // --------------------------------------------------------------------
        private void OnTeamChanged(SpawnPoint.Team previous, SpawnPoint.Team current) {
            UpdateOutlineColour();

            // If this is the local player's team changing, update all other players' outlines
            if(IsOwner) {
                UpdateAllPlayerOutlines();
            }
        }

        // --------------------------------------------------------------------
        // Public method to update outline - can be called by PlayerTagController
        // --------------------------------------------------------------------
        /// <summary>
        /// Updates the outline color based on the current team and game mode.
        /// </summary>
        public void UpdateOutlineColour() {
            if(_skinned == null || _propertyBlock == null) {
                Debug.LogWarning($"[PlayerTeamManager] Cannot update outline - skinned: {_skinned != null}, propertyBlock: {_propertyBlock != null}, GameObject: {gameObject.name}");
                return;
            }

            // Refresh MatchSettingsManager cache if needed
            if(_cachedMatchSettings == null) {
                _cachedMatchSettings = MatchSettingsManager.Instance;
                _gameModeCacheValid = false;
            }

            if(_cachedMatchSettings == null) {
                Debug.LogWarning($"[PlayerTeamManager] MatchSettingsManager is null! GameObject: {gameObject.name}");
                return;
            }

            // Always check current game mode and invalidate cache if it changed
            var currentGameModeId = _cachedMatchSettings.selectedGameModeId;
            if(_gameModeCacheValid && _cachedGameModeId != currentGameModeId) {
                // Game mode changed - invalidate cache
                _gameModeCacheValid = false;
                Debug.Log($"[PlayerTeamManager] Game mode changed from '{_cachedGameModeId}' to '{currentGameModeId}', invalidating cache. GameObject: {gameObject.name}");
            }

            // Cache game mode checks
            if(!_gameModeCacheValid) {
                _cachedGameModeId = currentGameModeId;
                _cachedIsTeamBased = MatchSettingsManager.IsTeamBasedMode(_cachedGameModeId);
                _cachedIsTagMode = _cachedGameModeId == "Gun Tag";
                _gameModeCacheValid = true;
            }

            if(_cachedIsTagMode && _tagController != null) {
                if(_tagController.IsTagged.Value) {
                    _skinned.GetPropertyBlock(_tagPropertyBlock, 0);
                    var outlineSize = CalculateOutlineSize();
                    _tagPropertyBlock.SetColor(OutlineColorID, taggedGlow);
                    _tagPropertyBlock.SetFloat(Size, outlineSize);
                    _skinned.SetPropertyBlock(_tagPropertyBlock, 0);
                    _lastOutlineSize = outlineSize;
                } else {
                    _skinned.SetPropertyBlock(null, 0);
                    _lastOutlineSize = -1f;
                }

                return;
            }

            // Team-based mode: update colors
            if(_cachedIsTeamBased) {
                Color target;

                if(IsOwner) {
                    return;
                }

                GameObject localPlayer = null;
                var networkManager = NetworkManager.Singleton;
                if(networkManager != null && networkManager.LocalClient != null) {
                    var playerObject = networkManager.LocalClient.PlayerObject;
                    if(playerObject != null) {
                        localPlayer = playerObject.gameObject;
                    }
                }
                if(localPlayer != null) {
                    var localController = PlayerController.LocalPlayer;
                    PlayerTeamManager localTeamMgr = null;
                    if(localController != null) {
                        localTeamMgr = localController.TeamManager;
                    }
                    if(localTeamMgr != null && netTeam.Value == localTeamMgr.netTeam.Value) {
                        target = teammateOutline;
                    } else {
                        target = enemyOutline;
                    }
                } else {
                    target = enemyOutline;
                }

                var outlineSize = CalculateOutlineSize();

                var sharedMaterial = _skinned.sharedMaterial;
                if(sharedMaterial == null) {
                    return;
                }
                
                var hasOutlineColor = sharedMaterial.HasProperty(OutlineColorID);
                
                if(!hasOutlineColor) {
                    var materialInstance = _skinned.material;
                    if(materialInstance == null || !materialInstance.HasProperty(OutlineColorID)) return;
                    materialInstance.SetColor(OutlineColorID, target);
                    materialInstance.SetFloat(Size, outlineSize);
                    _lastOutlineSize = outlineSize;
                    return;
                }
                
                _skinned.GetPropertyBlock(_propertyBlock, 0);
                
                _propertyBlock.SetColor(OutlineColorID, target);
                _propertyBlock.SetFloat(Size, outlineSize);
                _skinned.SetPropertyBlock(_propertyBlock, 0);
                _lastOutlineSize = outlineSize;
                return;
            }

            _skinned.SetPropertyBlock(null, 0);
            _lastOutlineSize = -1f;
        }

        // --------------------------------------------------------------------
        // Calculate outline size based on distance (larger at distance for visibility)
        // --------------------------------------------------------------------
        /// <summary>
        /// Calculates the outline size based on the distance from the main camera.
        /// </summary>
        private float CalculateOutlineSize() {
            if(_mainCamera == null) {
                _mainCamera = _camera;
                if(_mainCamera == null) {
                    return minOutlineSize;
                }
            }

            var distance = Vector3.Distance(_mainCamera.transform.position, transform.position);

            var normalizedDistance = Mathf.InverseLerp(distanceScaleStart, distanceScaleEnd, distance);
            normalizedDistance = Mathf.Clamp01(normalizedDistance);

            var outlineSize = Mathf.Lerp(minOutlineSize, maxOutlineSize, normalizedDistance);

            return outlineSize;
        }

        // --------------------------------------------------------------------
        // Update outline size every frame for distance-based scaling
        // --------------------------------------------------------------------
        private void Update() {
            if(_skinned == null || _propertyBlock == null) return;
            if(IsOwner) return; // Don't update for self

            // Refresh MatchSettingsManager cache if needed
            if(_cachedMatchSettings == null) {
                _cachedMatchSettings = MatchSettingsManager.Instance;
                _gameModeCacheValid = false;
            }

            if(_cachedMatchSettings == null) return;

            // Always check current game mode and invalidate cache if it changed
            var currentGameModeId = _cachedMatchSettings.selectedGameModeId;
            switch(_gameModeCacheValid) {
                case true when _cachedGameModeId != currentGameModeId:
                    // Game mode changed - invalidate cache and update outline
                    _gameModeCacheValid = false;
                    UpdateOutlineColour(); // Force update when game mode changes
                    return;
                // Cache game mode checks
                case false:
                    _cachedGameModeId = currentGameModeId;
                    _cachedIsTeamBased = MatchSettingsManager.IsTeamBasedMode(_cachedGameModeId);
                    _cachedIsTagMode = _cachedGameModeId == "Gun Tag";
                    _gameModeCacheValid = true;
                    break;
            }

            // Only update size for team-based or tag modes (where we're using custom colors)
            if(!_cachedIsTeamBased && !_cachedIsTagMode) return;

            // For tag mode, only update if tagged
            if(_cachedIsTagMode && (_tagController == null || !_tagController.IsTagged.Value)) {
                return;
            }

            // Update outline size based on distance
            var outlineSize = CalculateOutlineSize();

            // Only update if size actually changed (avoid unnecessary SetPropertyBlock calls)
            // Use cached last size instead of GetPropertyBlock every frame
            if(!(Mathf.Abs(_lastOutlineSize - outlineSize) > 0.001f)) return;
            // For tag mode, update the tag property block
            if(_cachedIsTagMode && _tagController != null && _tagController.IsTagged.Value) {
                _tagPropertyBlock.SetFloat(Size, outlineSize);
                _skinned.SetPropertyBlock(_tagPropertyBlock, 0);
            } else {
                // For team-based mode, update the regular property block
                _propertyBlock.SetFloat(Size, outlineSize);
                _skinned.SetPropertyBlock(_propertyBlock, 0);
            }

            _lastOutlineSize = outlineSize;
        }
    }
}
