using System;
using System.Collections;
using System.Collections.Generic;
using Game.Weapons;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    public class SpeedTrail : NetworkBehaviour {
        [Header("References")]
        [SerializeField] private PlayerController playerController; // assign in inspector or auto-find

        private SkinnedMeshRenderer _playerMesh; // WORLD/3P mesh (not FP arms)
        private DeathCameraController _deathCameraController; // Used to check if death cam is active

        [Header("Afterimage Settings")]
        private const float MinMultiplierForTrail = 1.5f;

        private const float SpawnInterval = 0.05f;
        private const float GhostLifetime = 0.30f;
        private const float SpawnOffset = 0.5f;
        [SerializeField] private Material ghostMaterial;
        [SerializeField] private Color trailColor = new(0.3f, 0.7f, 1f, 0.5f);

        // Color mapping: white, red, orange, yellow, green, blue, purple (index 0-6)
        private static readonly Color[] PlayerColors = {
            new Color(1f, 1f, 1f, 1f), // white (0)
            new Color(1f, 0f, 0f, 1f), // red (1)
            new Color(1f, 0.5f, 0f, 1f), // orange (2)
            new Color(1f, 1f, 0f, 1f), // yellow (3)
            new Color(0f, 1f, 0f, 1f), // green (4)
            new Color(0f, 0.5f, 1f, 1f), // blue (5)
            new Color(0.5f, 0f, 1f, 1f) // purple (6)
        };

        private static readonly int emissionColor = Shader.PropertyToID("_EmissionColor");
        private static readonly int emission = Shader.PropertyToID("_Emission");
        private static readonly int tintColor = Shader.PropertyToID("_TintColor");
        private static readonly int color = Shader.PropertyToID("_Color");

        private float _lastSpawnTime;
        private readonly Queue<GameObject> _ghostPool = new();
        private const int PoolSize = 20;

        // Cache WeaponManager reference to prevent GetComponent allocations
        private WeaponManager _cachedWeaponManager;

        // Material pool to avoid creating new materials every spawn
        private readonly Queue<Material> _materialPool = new();
        private const int MaterialPoolSize = 20;

        // Track active fade coroutines so we can stop them when clearing trails
        private readonly List<Coroutine> _activeFadeCoroutines = new();

        private void Awake() {
            playerController ??= GetComponent<PlayerController>();
            
            _deathCameraController ??= playerController.DeathCameraController;
            _playerMesh ??= playerController.PlayerMesh;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            // Auto-find controller if not assigned
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
                if(playerController == null) {
                    playerController = GetComponentInParent<PlayerController>();
                }
            }

            // Auto-find player mesh if not assigned
            if(_playerMesh == null) {
                _playerMesh = GetComponentInChildren<SkinnedMeshRenderer>();
            }

            // Auto-find death camera if not assigned
            if(_deathCameraController == null) {
                _deathCameraController = GetComponent<DeathCameraController>();
                if(_deathCameraController == null && playerController != null) {
                    _deathCameraController = playerController.GetComponent<DeathCameraController>();
                }
            }

            // Cache WeaponManager reference to prevent GetComponent allocations in Update
            if(playerController != null) {
                _cachedWeaponManager = playerController.WeaponManager;
            }

            if(ghostMaterial == null) CreateGhostMaterial();
            for(var i = 0; i < PoolSize; i++) CreateGhost();

            // Pre-populate material pool
            for(var i = 0; i < MaterialPoolSize; i++) {
                var mat = CreatePooledMaterial();
                _materialPool.Enqueue(mat);
            }
        }

        private void Update() {
            // For owners: only spawn trails when dead (netIsDead is true)
            // For non-owners: always spawn trails (if enabled)
            if(IsOwner) {
                // Owner: only spawn trails when dead
                if(playerController == null || !playerController.IsDead) {
                    return;
                }
            }

            // Check if player trails are enabled in settings
            if(PlayerPrefs.GetInt("PlayerTrails", 1) == 0) return;

            if(!_playerMesh || !playerController) return;

            // Get the actual damage multiplier from the Weapon component
            // This matches the same calculation used in Weapon.UpdateDamageMultiplier()
            // Use cached WeaponManager reference to prevent GetComponent allocations
            if(_cachedWeaponManager == null && playerController != null) {
                _cachedWeaponManager = playerController.WeaponManager;
            }

            if(_cachedWeaponManager == null || _cachedWeaponManager.CurrentWeapon == null) return;

            var weapon = _cachedWeaponManager.CurrentWeapon;
            // Use the network-synced multiplier value
            var currentMultiplier = weapon.netCurrentDamageMultiplier.Value;

            // Only spawn trails when multiplier is at least the minimum threshold
            if(currentMultiplier < MinMultiplierForTrail) {
                return;
            }

            // Faster -> more frequent (based on multiplier, not speed)
            var speedFactor = Mathf.InverseLerp(MinMultiplierForTrail, 3f, currentMultiplier);
            var adjustedInterval = Mathf.Lerp(SpawnInterval * 2f, SpawnInterval * 0.5f, speedFactor);

            var timeSinceLastSpawn = Time.time - _lastSpawnTime;
            if(timeSinceLastSpawn < adjustedInterval) {
                return;
            }

            // Spawn the trail (it will be visible to this client)
            SpawnAfterimage();
            _lastSpawnTime = Time.time;
        }

        private GameObject CreateGhost() {
            var ghost = new GameObject("AfterimageGhost") {
                // Layer will be set when spawning based on viewer
                // Default to "Default" layer for now (will be set correctly in SpawnAfterimage)
                layer = LayerMask.NameToLayer("Default")
            };

            ghost.SetActive(false);
            ghost.AddComponent<MeshFilter>();
            var mr = ghost.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

            _ghostPool.Enqueue(ghost);
            return ghost;
        }

        private void SpawnAfterimage() {
            // Find an available ghost from pool (check if inactive)
            GameObject ghost = null;
            var attempts = 0;
            while(attempts < _ghostPool.Count && _ghostPool.Count > 0) {
                var candidate = _ghostPool.Dequeue();
                _ghostPool.Enqueue(candidate); // Put it back at the end

                if(candidate != null && !candidate.activeInHierarchy) {
                    ghost = candidate;
                    break;
                }

                attempts++;
            }

            // If no available ghost found, create a new one
            if(ghost == null) {
                ghost = CreateGhost();
            }

            // Set layer based on whether this is owner or non-owner
            // For owner: use "Masked" layer while alive (hidden from FP camera), "Default" when dead (visible during death cam)
            // For non-owner: always use "Default" layer (always visible)
            if(IsOwner) {
                // Owner: use Default layer when dead (visible during death cam), Masked layer while alive
                bool isDead = playerController != null && playerController.IsDead;
                ghost.layer = isDead
                    ? LayerMask.NameToLayer("Default")
                    : LayerMask.NameToLayer("Masked");
            } else {
                // Non-owner: always visible on Default layer
                ghost.layer = LayerMask.NameToLayer("Default");
            }

            // Movement direction - calculate from velocity if available, otherwise use transform forward
            Vector3 moveDir;
            if(playerController != null) {
                // Try to get velocity direction from controller
                var velocity = playerController.GetFullVelocity;
                if(velocity.sqrMagnitude > 0.01f) {
                    moveDir = -velocity.normalized; // Negative because we want to spawn behind
                } else {
                    moveDir = -transform.forward;
                }
            } else {
                // Fallback: use transform forward
                moveDir = -transform.forward;
            }

            // Spawn behind the player (negative direction)
            var spawnPos = _playerMesh.transform.position + moveDir * SpawnOffset;

            ghost.transform.SetPositionAndRotation(spawnPos, _playerMesh.transform.rotation);
            ghost.transform.localScale = _playerMesh.transform.lossyScale;

            var mf = ghost.GetComponent<MeshFilter>();
            if(mf == null) {
                return;
            }

            // Reuse existing mesh or create new one
            Mesh baked = mf.sharedMesh;
            if(baked == null) {
                baked = new Mesh();
            }

            // Bake the current player mesh state into the mesh
            _playerMesh.BakeMesh(baked);
            mf.sharedMesh = baked;

            var mr = ghost.GetComponent<MeshRenderer>();
            if(mr == null) {
                return;
            }

            // Get material from pool (reuse instead of creating new)
            Material material = null;
            if(_materialPool.Count > 0) {
                material = _materialPool.Dequeue();
            } else {
                // Pool empty, create new one
                material = CreatePooledMaterial();
            }

            // Apply player's selected color to the afterimage
            if(playerController != null) {
                var materialIndex = playerController.playerMaterialIndex.Value;
                if(materialIndex >= 0 && materialIndex < PlayerColors.Length) {
                    var playerColor = PlayerColors[materialIndex];

                    // Preserve emission intensity from the original material
                    // For Particles/Standard Unlit shader, emission is typically _EmissionColor
                    if(material.HasProperty(emissionColor)) {
                        var currentEmission = material.GetColor(emissionColor);
                        // Calculate intensity from the original emission (HDR values can be > 1)
                        var emissionIntensity = currentEmission.maxColorComponent;
                        if(emissionIntensity < 0.1f) {
                            // If no emission set, use a default intensity (matching the red look you liked)
                            emissionIntensity = 2.4f; // Approximate intensity from inspector
                        }

                        // Apply player color with preserved intensity (HDR)
                        material.SetColor(emissionColor, playerColor * emissionIntensity);
                        material.EnableKeyword("_EMISSION");
                    } else if(material.HasProperty(emission)) {
                        var currentEmission = material.GetColor(emission);
                        var emissionIntensity = currentEmission.maxColorComponent;
                        if(emissionIntensity < 0.1f) {
                            emissionIntensity = 2.4f;
                        }

                        material.SetColor(emission, playerColor * emissionIntensity);
                        material.EnableKeyword("_EMISSION");
                    }

                    // Also set the main color (albedo) if the shader supports it
                    // For Particles shader, this might be _TintColor or _Color
                    if(material.HasProperty(tintColor)) {
                        var currentTint = material.GetColor(tintColor);
                        material.SetColor(tintColor,
                            new Color(playerColor.r, playerColor.g, playerColor.b, currentTint.a));
                    } else if(material.HasProperty(color)) {
                        var currentColor = material.GetColor(color);
                        material.SetColor(color,
                            new Color(playerColor.r, playerColor.g, playerColor.b, currentColor.a));
                    }
                }
            }

            mr.material = material;

            ghost.SetActive(true);
            var fadeCoroutine = StartCoroutine(FadeAndReturnGhost(ghost, mr));
            _activeFadeCoroutines.Add(fadeCoroutine);
        }

        private IEnumerator FadeAndReturnGhost(GameObject ghost, MeshRenderer mr) {
            var t = 0f;
            var mat = mr.material;
            var c0 = mat.color;
            while(t < GhostLifetime) {
                t += Time.deltaTime;
                var a = Mathf.Lerp(c0.a, 0f, t / GhostLifetime);
                mat.color = new Color(c0.r, c0.g, c0.b, a);
                yield return null;
            }

            // Remove this coroutine from tracking when it completes naturally
            _activeFadeCoroutines.RemoveAll(c => c == null);

            ghost.SetActive(false);

            // Return material to pool instead of destroying it
            if(mat != null && _materialPool.Count < MaterialPoolSize * 2) {
                // Reset material color to original
                mat.color = trailColor;
                _materialPool.Enqueue(mat);
            } else if(mat != null) {
                // Pool is full, destroy excess materials
                Destroy(mat);
            }

            // Destroy the mesh to prevent memory leak
            var mf = ghost.GetComponent<MeshFilter>();
            if(mf != null && mf.sharedMesh != null) {
                Destroy(mf.sharedMesh);
                mf.sharedMesh = null;
            }

            _ghostPool.Enqueue(ghost);
        }

        private void CreateGhostMaterial() {
            ghostMaterial = NewGhostMaterial();
        }

        private Material NewGhostMaterial() {
            var m = new Material(Shader.Find("Standard"));
            m.SetFloat(Shader.PropertyToID("_Mode"), 3);
            m.SetInt(Shader.PropertyToID("_SrcBlend"), (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt(Shader.PropertyToID("_DstBlend"), (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetInt(Shader.PropertyToID("_ZWrite"), 0);
            m.DisableKeyword("_ALPHATEST_ON");
            m.EnableKeyword("_ALPHABLEND_ON");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            // Use a lower render queue so trails render behind the player model
            // Player models typically render at 2000-2500, so 2500 ensures trails are behind
            m.renderQueue = 2500;
            m.color = trailColor;
            return m;
        }

        /// <summary>
        /// Creates a material for the pool (based on ghostMaterial if available, otherwise creates new)
        /// </summary>
        private Material CreatePooledMaterial() {
            return ghostMaterial ? new Material(ghostMaterial) : NewGhostMaterial();
        }

        /// <summary>
        /// Clears all active speed trail afterimages immediately.
        /// Called before respawn to ensure a clean slate.
        /// </summary>
        public void ClearAllTrails() {
            // Stop all fade coroutines
            foreach(var coroutine in _activeFadeCoroutines) {
                if(coroutine != null) {
                    StopCoroutine(coroutine);
                }
            }

            _activeFadeCoroutines.Clear();

            // Deactivate all active ghosts and return materials to pool
            var activeGhosts = new List<GameObject>();
            foreach(var ghost in _ghostPool) {
                if(ghost != null && ghost.activeInHierarchy) {
                    activeGhosts.Add(ghost);
                }
            }

            foreach(var ghost in activeGhosts) {
                var mr = ghost.GetComponent<MeshRenderer>();
                if(mr != null && mr.material != null) {
                    var mat = mr.material;
                    // Reset material color
                    mat.color = trailColor;
                    // Return material to pool if not full
                    if(_materialPool.Count < MaterialPoolSize * 2) {
                        _materialPool.Enqueue(mat);
                    } else {
                        Destroy(mat);
                    }
                }

                // Destroy the mesh to prevent memory leak
                var mf = ghost.GetComponent<MeshFilter>();
                if(mf != null && mf.sharedMesh != null) {
                    Destroy(mf.sharedMesh);
                    mf.sharedMesh = null;
                }

                ghost.SetActive(false);
            }
        }
    }
}