using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Weapons;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    public class SpeedTrail : NetworkBehaviour
    {
        [Header("Refs")]
        [SerializeField] private PlayerController controller;          // assign in inspector or auto-find
        [SerializeField] private SkinnedMeshRenderer playerMesh;       // WORLD/3P mesh (not FP arms)

        [Header("Afterimage Settings")]
        [SerializeField] private float minMultiplierForTrail = 1.5f;
        [SerializeField] private float spawnInterval = 0.05f;
        [SerializeField] private float ghostLifetime = 0.30f;
        [SerializeField] private float spawnOffset = 0.5f; 
        [SerializeField] private Material ghostMaterial;
        [SerializeField] private Color trailColor = new(0.3f, 0.7f, 1f, 0.5f);
        
        // Color mapping: white, red, orange, yellow, green, blue, purple (index 0-6)
        private static readonly Color[] PlayerColors = {
            new Color(1f, 1f, 1f, 1f),      // white (0)
            new Color(1f, 0f, 0f, 1f),      // red (1)
            new Color(1f, 0.5f, 0f, 1f),    // orange (2)
            new Color(1f, 1f, 0f, 1f),      // yellow (3)
            new Color(0f, 1f, 0f, 1f),      // green (4)
            new Color(0f, 0.5f, 1f, 1f),    // blue (5)
            new Color(0.5f, 0f, 1f, 1f)     // purple (6)
        };

        private float _lastSpawnTime;
        private readonly Queue<GameObject> _ghostPool = new();
        private const int PoolSize = 20;

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
        
            // Auto-find controller if not assigned
            if(controller == null) {
                controller = GetComponent<PlayerController>();
                if(controller == null) {
                    controller = GetComponentInParent<PlayerController>();
                }
            }
            
            // Auto-find player mesh if not assigned
            if(playerMesh == null) {
                playerMesh = GetComponentInChildren<SkinnedMeshRenderer>();
            }
            
            if(ghostMaterial == null) CreateGhostMaterial();
            for(var i = 0; i < PoolSize; i++) CreateGhost();
        }

        private void Update() {
            if(IsOwner) return;
            
            if(!playerMesh || !controller) return;

            // Get the actual damage multiplier from the Weapon component
            // This matches the same calculation used in Weapon.UpdateDamageMultiplier()
            var weaponManager = controller.GetComponent<WeaponManager>();
            if(weaponManager == null || weaponManager.CurrentWeapon == null) return;
            
            var weapon = weaponManager.CurrentWeapon;
            // Use the network-synced multiplier value
            var currentMultiplier = weapon.netCurrentDamageMultiplier.Value;

            // Only spawn trails when multiplier is at least the minimum threshold
            if(currentMultiplier < minMultiplierForTrail) {
                return;
            }

            // Faster -> more frequent (based on multiplier, not speed)
            var speedFactor = Mathf.InverseLerp(minMultiplierForTrail, 3f, currentMultiplier);
            var adjustedInterval = Mathf.Lerp(spawnInterval * 2f, spawnInterval * 0.5f, speedFactor);

            var timeSinceLastSpawn = Time.time - _lastSpawnTime;
            if(timeSinceLastSpawn < adjustedInterval) {
                return;
            }
            
            // Spawn the trail (it will be visible to this client)
            SpawnAfterimage();
            _lastSpawnTime = Time.time;
        }

        private GameObject CreateGhost() {
            var ghost = new GameObject("AfterimageGhost");
            // Layer will be set when spawning based on viewer
            // Default to "Default" layer for now (will be set correctly in SpawnAfterimage)
            ghost.layer = LayerMask.NameToLayer("Default");

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

            // Set layer to Default - since Update() only runs on non-owners (IsOwner check),
            // we're viewing someone else's player, so the trail should be visible
            ghost.layer = LayerMask.NameToLayer("Default");

            // Movement direction - calculate from velocity if available, otherwise use transform forward
            Vector3 moveDir;
            if(controller != null) {
                // Try to get velocity direction from controller
                var velocity = controller.CurrentFullVelocity;
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
            var spawnPos = playerMesh.transform.position + moveDir * spawnOffset;

            ghost.transform.SetPositionAndRotation(spawnPos, playerMesh.transform.rotation);
            ghost.transform.localScale = playerMesh.transform.lossyScale;

            var baked = new Mesh();
            playerMesh.BakeMesh(baked);

            var mf = ghost.GetComponent<MeshFilter>();
            if(mf == null) {
                Debug.LogError("[SpeedTrail] Ghost missing MeshFilter!");
                return;
            }
            mf.sharedMesh = baked;

            var mr = ghost.GetComponent<MeshRenderer>();
            if(mr == null) {
                Debug.LogError("[SpeedTrail] Ghost missing MeshRenderer!");
                return;
            }
            // material per instance so alpha fades independently
            var material = ghostMaterial ? new Material(ghostMaterial) : NewGhostMaterial();
            
            // Apply player's selected color to the afterimage
            if(controller != null) {
                var materialIndex = controller.playerMaterialIndex.Value;
                if(materialIndex >= 0 && materialIndex < PlayerColors.Length) {
                    var playerColor = PlayerColors[materialIndex];
                    
                    // Preserve emission intensity from the original material
                    // For Particles/Standard Unlit shader, emission is typically _EmissionColor
                    if(material.HasProperty("_EmissionColor")) {
                        var currentEmission = material.GetColor("_EmissionColor");
                        // Calculate intensity from the original emission (HDR values can be > 1)
                        var emissionIntensity = currentEmission.maxColorComponent;
                        if(emissionIntensity < 0.1f) {
                            // If no emission set, use a default intensity (matching the red look you liked)
                            emissionIntensity = 2.4f; // Approximate intensity from inspector
                        }
                        // Apply player color with preserved intensity (HDR)
                        material.SetColor("_EmissionColor", playerColor * emissionIntensity);
                        material.EnableKeyword("_EMISSION");
                    } else if(material.HasProperty("_Emission")) {
                        var currentEmission = material.GetColor("_Emission");
                        var emissionIntensity = currentEmission.maxColorComponent;
                        if(emissionIntensity < 0.1f) {
                            emissionIntensity = 2.4f;
                        }
                        material.SetColor("_Emission", playerColor * emissionIntensity);
                        material.EnableKeyword("_EMISSION");
                    }
                    
                    // Also set the main color (albedo) if the shader supports it
                    // For Particles shader, this might be _TintColor or _Color
                    if(material.HasProperty("_TintColor")) {
                        var currentTint = material.GetColor("_TintColor");
                        material.SetColor("_TintColor", new Color(playerColor.r, playerColor.g, playerColor.b, currentTint.a));
                    } else if(material.HasProperty("_Color")) {
                        var currentColor = material.GetColor("_Color");
                        material.SetColor("_Color", new Color(playerColor.r, playerColor.g, playerColor.b, currentColor.a));
                    }
                }
            }
            
            mr.material = material;

            ghost.SetActive(true);
            StartCoroutine(FadeAndReturnGhost(ghost, mr));
        }

        private IEnumerator FadeAndReturnGhost(GameObject ghost, MeshRenderer mr) {
            var t = 0f;
            var mat = mr.material;
            var c0 = mat.color;
            while(t < ghostLifetime) {
                t += Time.deltaTime;
                var a = Mathf.Lerp(c0.a, 0f, t / ghostLifetime);
                mat.color = new Color(c0.r, c0.g, c0.b, a);
                yield return null;
            }
            ghost.SetActive(false);
            Destroy(mat); // destroy the instance
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
    }
}