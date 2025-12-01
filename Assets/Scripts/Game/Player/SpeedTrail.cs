using Game.Weapons;
using Unity.Netcode;
using UnityEngine;
using System.Collections;

namespace Game.Player {
    public class SpeedTrail : NetworkBehaviour {
        [Header("References")]
        [SerializeField] private PlayerController playerController; // assign in inspector or auto-find
        [SerializeField] private GameObject speedTrailEffect; // The particle system effect GameObject

        [Header("Trail Material Objects")]
        [Tooltip("The 'trail' GameObject that has two materials with Color properties")]
        [SerializeField] private GameObject trailObject;
        
        [Tooltip("The 'electric' GameObject that has one material with a Color property")]
        [SerializeField] private GameObject electricObject;
        
        [Tooltip("The 'electric (1)' GameObject that has one material with a Color property")]
        [SerializeField] private GameObject electricObject1;

        // Cache WeaponManager reference to prevent GetComponent allocations
        private WeaponManager _weaponManager;

        [Header("Trail Settings")]
        [SerializeField] private float minMultiplierForTrail = 1.5f;
        
        [Header("Fade Settings")]
        [SerializeField] private float fadeInDuration = 0.5f; // Time to fade in
        [SerializeField] private float fadeOutDuration = 0.5f; // Time to fade out
        
        // Cache particle systems for the trail and electric objects
        private ParticleSystem _trailParticleSystem;
        private ParticleSystem _electricParticleSystem;
        private ParticleSystem _electricParticleSystem1;
        
        // Cache original emission rates
        private float _trailOriginalEmissionRate;
        private float _electricOriginalEmissionRate;
        private float _electric1OriginalEmissionRate;
        
        private bool _isTrailActive;
        private Color _currentPlayerColor = Color.white;
        private Coroutine _fadeCoroutine;

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(playerController == null) {
                Debug.LogError("[SpeedTrail] PlayerController not found!");
                enabled = false;
                return;
            }

            if(_weaponManager == null) {
                _weaponManager = playerController.WeaponManager;
            }

            // Cache renderers for trail and electric objects
            CacheTrailRenderers();

            CachePlayerColor();
        }

        /// <summary>
        /// Caches the ParticleSystem components from the trail and electric objects.
        /// Also caches original emission rates for fade in/out functionality.
        /// </summary>
        private void CacheTrailRenderers() {
            // Cache trail particle system
            if(trailObject != null) {
                _trailParticleSystem = trailObject.GetComponent<ParticleSystem>();
                if(_trailParticleSystem == null) {
                    Debug.LogWarning($"[SpeedTrail] Trail object '{trailObject.name}' has no ParticleSystem component!");
                } else {
                    // Cache original emission rate
                    var emission = _trailParticleSystem.emission;
                    _trailOriginalEmissionRate = emission.rateOverTime.constant;
                }
            } else {
                _trailParticleSystem = null;
            }

            // Cache electric particle system
            if(electricObject != null) {
                _electricParticleSystem = electricObject.GetComponent<ParticleSystem>();
                if(_electricParticleSystem == null) {
                    Debug.LogWarning($"[SpeedTrail] Electric object '{electricObject.name}' has no ParticleSystem component!");
                } else {
                    // Cache original emission rate
                    var emission = _electricParticleSystem.emission;
                    _electricOriginalEmissionRate = emission.rateOverTime.constant;
                }
            } else {
                _electricParticleSystem = null;
            }

            // Cache electric (1) particle system
            if(electricObject1 != null) {
                _electricParticleSystem1 = electricObject1.GetComponent<ParticleSystem>();
                if(_electricParticleSystem1 == null) {
                    Debug.LogWarning($"[SpeedTrail] Electric (1) object '{electricObject1.name}' has no ParticleSystem component!");
                } else {
                    // Cache original emission rate
                    var emission = _electricParticleSystem1.emission;
                    _electric1OriginalEmissionRate = emission.rateOverTime.constant;
                }
            } else {
                _electricParticleSystem1 = null;
            }
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            
            // Subscribe to color changes after network spawn to ensure NetworkVariable is initialized
            SubscribeToColorChanges();
            
            // Apply initial color after network spawn (ensures NetworkVariable is synced)
            CachePlayerColor();
            ApplyPlayerColorToMaterials();
        }

        public override void OnNetworkDespawn() {
            base.OnNetworkDespawn();
            UnsubscribeFromColorChanges();
        }

        private void OnEnable() {
            // Ensure trail starts disabled
            if(speedTrailEffect == null) return;
            speedTrailEffect.SetActive(false);
            _isTrailActive = false;
        }

        private void OnDisable() {
            // Disable trail when component is disabled
            if(speedTrailEffect == null) return;
            speedTrailEffect.SetActive(false);
            _isTrailActive = false;
        }

        private void Update() {
            // Check if player trails are enabled in settings first
            if(PlayerPrefs.GetInt("PlayerTrails", 1) == 0) {
                SetTrailActive(false);
                return;
            }

            if(!playerController || speedTrailEffect == null) {
                SetTrailActive(false);
                return;
            }

            // For owners: only show trails when dead (deathcam)
            if(IsOwner) {
                if(!playerController.IsDead) {
                    SetTrailActive(false);
                    return;
                }
            }

            // Get the actual damage multiplier from the Weapon component
            if(_weaponManager == null && playerController != null) {
                _weaponManager = playerController.WeaponManager;
            }

            if(_weaponManager == null || _weaponManager.CurrentWeapon == null) {
                SetTrailActive(false);
                return;
            }

            var weapon = _weaponManager.CurrentWeapon;
            // Use the network-synced multiplier value
            var currentMultiplier = weapon.netCurrentDamageMultiplier.Value;

            // Enable/disable trail based on multiplier threshold
            // For owners: only when dead (already checked above)
            // For non-owners: when multiplier is high enough
            var shouldBeActive = currentMultiplier >= minMultiplierForTrail;
            SetTrailActive(shouldBeActive);
        }

        private void SetTrailActive(bool active) {
            if(speedTrailEffect == null || _isTrailActive == active) return;

            _isTrailActive = active;
            
            // Stop any existing fade coroutine
            if(_fadeCoroutine != null) {
                StopCoroutine(_fadeCoroutine);
                _fadeCoroutine = null;
            }
            
            if(active) {
                // Apply player color before activating (ensures new particles have correct color)
                ApplyPlayerColorToMaterials();
                
                // Fade in: enable GameObject and fade emission rate from 0 to full
                speedTrailEffect.SetActive(true);
                _fadeCoroutine = StartCoroutine(FadeEmissionRate(0f, 1f, fadeInDuration));
            } else {
                // Fade out: fade emission rate from current to 0, then disable GameObject
                _fadeCoroutine = StartCoroutine(FadeEmissionRate(1f, 0f, fadeOutDuration));
            }
        }

        private void CachePlayerColor() {
            if(playerController == null) return;
            _currentPlayerColor = playerController.CurrentBaseColor;
        }

        private void SubscribeToColorChanges() {
            if(playerController == null) return;
            playerController.playerBaseColor.OnValueChanged -= OnPlayerBaseColorChanged;
            playerController.playerBaseColor.OnValueChanged += OnPlayerBaseColorChanged;
            CachePlayerColor();
        }

        private void UnsubscribeFromColorChanges() {
            if(playerController == null) return;
            playerController.playerBaseColor.OnValueChanged -= OnPlayerBaseColorChanged;
        }

        private void OnPlayerBaseColorChanged(Vector4 _, Vector4 newValue) {
            _currentPlayerColor = new Color(newValue.x, newValue.y, newValue.z, 1f);
            ApplyPlayerColorToMaterials();
            
            // Broadcast color change to all clients via RPC
            if(IsOwner) {
                BroadcastColorChangeClientRpc(newValue);
            }
        }

        /// <summary>
        /// Broadcasts color change to all clients to ensure trail colors stay in sync.
        /// </summary>
        [Rpc(SendTo.Everyone)]
        private void BroadcastColorChangeClientRpc(Vector4 color) {
            // Only apply if we're not the owner (owner already applied it via OnValueChanged)
            if(IsOwner) return;
            _currentPlayerColor = new Color(color.x, color.y, color.z, 1f);
            ApplyPlayerColorToMaterials();
        }

        /// <summary>
        /// Applies the player's base color to all trail and electric material Color properties.
        /// Accesses materials through ParticleSystemRenderer module.
        /// Note: Only the trail object has both Material and Trail Material fields.
        /// Electric objects only have Material field.
        /// </summary>
        private void ApplyPlayerColorToMaterials() {
            // Common color property names to try
            string[] colorPropertyNames = { "_Color", "_TintColor", "_BaseColor", "_MainColor", "Color" };

            // Apply to trail particle system (has both Material and Trail Material)
            if(_trailParticleSystem != null) {
                var psr = _trailParticleSystem.GetComponent<ParticleSystemRenderer>();
                if(psr != null) {
                    // Apply to main Material
                    if(psr.material != null) {
                        ApplyColorToMaterial(psr.material, colorPropertyNames);
                    }
                    
                    // Apply to Trail Material (only trail object has this)
                    if(psr.trailMaterial != null) {
                        ApplyColorToMaterial(psr.trailMaterial, colorPropertyNames);
                    }
                }
                
                // Clear existing particles so new ones use updated material colors
                _trailParticleSystem.Clear();
            }

            // Apply to electric particle system (only has Material, no Trail Material)
            if(_electricParticleSystem != null) {
                var psr = _electricParticleSystem.GetComponent<ParticleSystemRenderer>();
                if(psr != null && psr.material != null) {
                    ApplyColorToMaterial(psr.material, colorPropertyNames);
                }
                
                // Clear existing particles so new ones use updated material colors
                _electricParticleSystem.Clear();
            }

            // Apply to electric (1) particle system (only has Material, no Trail Material)
            if(_electricParticleSystem1 == null) return;
            {
                var psr = _electricParticleSystem1.GetComponent<ParticleSystemRenderer>();
                if(psr != null && psr.material != null) {
                    ApplyColorToMaterial(psr.material, colorPropertyNames);
                }
                
                // Clear existing particles so new ones use updated material colors
                _electricParticleSystem1.Clear();
            }
        }

        /// <summary>
        /// Helper method to apply color to a material by trying common color property names.
        /// </summary>
        private void ApplyColorToMaterial(Material material, string[] colorPropertyNames) {
            if(material == null) return;

            foreach(var propName in colorPropertyNames) {
                if(!material.HasProperty(propName)) continue;
                // Preserve alpha if the property supports it
                var currentColor = material.GetColor(propName);
                var newColor = new Color(
                    _currentPlayerColor.r,
                    _currentPlayerColor.g,
                    _currentPlayerColor.b,
                    currentColor.a // Preserve original alpha
                );
                material.SetColor(propName, newColor);
                break; // Found and set the property
            }
        }

        /// <summary>
        /// Clears/disables the speed trail immediately.
        /// Called before respawn to ensure a clean slate.
        /// </summary>
        public void ClearTrail() {
            // Stop fade coroutine if running
            if(_fadeCoroutine != null) {
                StopCoroutine(_fadeCoroutine);
                _fadeCoroutine = null;
            }
            
            // Immediately disable without fade
            if(speedTrailEffect != null) {
                speedTrailEffect.SetActive(false);
            }
            
            // Reset emission rates to 0
            SetEmissionRateMultiplier(0f);
            _isTrailActive = false;
        }

        /// <summary>
        /// Coroutine that fades the emission rate of all particle systems over time.
        /// </summary>
        private IEnumerator FadeEmissionRate(float startValue, float endValue, float duration) {
            var elapsed = 0f;
            
            while(elapsed < duration) {
                elapsed += Time.deltaTime;
                var t = Mathf.Clamp01(elapsed / duration);
                var currentValue = Mathf.Lerp(startValue, endValue, t);
                
                // Apply to all particle systems
                SetEmissionRateMultiplier(currentValue);
                
                yield return null;
            }
            
            // Ensure final value is set
            SetEmissionRateMultiplier(endValue);
            
            // If fading out, disable GameObject after fade completes
            if(endValue == 0f && speedTrailEffect != null) {
                speedTrailEffect.SetActive(false);
            }
            
            _fadeCoroutine = null;
        }

        /// <summary>
        /// Sets the emission rate multiplier for all particle systems.
        /// Multiplies the original emission rate by the given multiplier.
        /// </summary>
        private void SetEmissionRateMultiplier(float multiplier) {
            // Trail particle system
            if(_trailParticleSystem != null) {
                var emission = _trailParticleSystem.emission;
                emission.rateOverTime = new ParticleSystem.MinMaxCurve(_trailOriginalEmissionRate * multiplier);
            }
            
            // Electric particle system
            if(_electricParticleSystem != null) {
                var emission = _electricParticleSystem.emission;
                emission.rateOverTime = new ParticleSystem.MinMaxCurve(_electricOriginalEmissionRate * multiplier);
            }
            
            // Electric (1) particle system
            if(_electricParticleSystem1 == null) return;
            {
                var emission = _electricParticleSystem1.emission;
                emission.rateOverTime = new ParticleSystem.MinMaxCurve(_electric1OriginalEmissionRate * multiplier);
            }
        }
    }
}
