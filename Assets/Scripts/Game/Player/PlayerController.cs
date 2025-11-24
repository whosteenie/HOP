using Game.Weapons;
using Network;
using Network.Rpc;
using Network.Singletons;
using Unity.Cinemachine;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace Game.Player {
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterController))]
    [DefaultExecutionOrder(-100)] // Initialize before sub-controllers
    public partial class PlayerController : NetworkBehaviour {

        #region Serialized Fields

        [Header("Components")]
        [SerializeField] private CinemachineCamera fpCamera;
        [SerializeField] private SwingGrapple swingGrapple;
        [SerializeField] private CharacterController characterController;
        [SerializeField] private PlayerInput playerInput;
        [SerializeField] private Animator characterAnimator;
        [SerializeField] private WeaponManager weaponManager;
        [SerializeField] private GrappleController grappleController;
        [SerializeField] private PlayerRagdoll playerRagdoll;
        [SerializeField] private DeathCamera deathCamera;
        [SerializeField] private NetworkSfxRelay sfxRelay;
        [SerializeField] private CinemachineImpulseSource impulseSource;
        [SerializeField] private MeshRenderer worldWeapon;
        [SerializeField] private LayerMask worldLayer;
        [SerializeField] private LayerMask enemyLayer;
        [SerializeField] private Transform tr;
        [SerializeField] private GameObject[] worldWeaponPrefabs;
        [SerializeField] private ClientNetworkTransform networkTransform;

        #endregion

        #region Public Input Fields

        public Vector2 moveInput;
        public Vector2 lookInput;
        public bool sprintInput;
        public bool crouchInput;

        #endregion

        #region Private Fields

        private float _lastDeathTime; // Used for OOB check in Update()

        // Cache MeshRenderers per weapon instance to avoid repeated GetComponentsInChildren calls
        private readonly System.Collections.Generic.Dictionary<GameObject, MeshRenderer[]> _cachedWeaponRenderers = new();

        #endregion

        #region Private Properties

        public Vector3 CurrentFullVelocity =>
            movementController != null ? movementController.CurrentFullVelocity : Vector3.zero;

        public bool IsGrounded => movementController != null && movementController.IsGrounded;

        private float CurrentPitch {
            get => lookController != null ? lookController.CurrentPitch : 0f;
            set {
                if(lookController != null) {
                    lookController.CurrentPitch = value;
                }
            }
        }

        #endregion

        #region Public Properties

        public CinemachineCamera FpCamera => fpCamera;

        #endregion

        #region Network Variables

        public NetworkVariable<float> netHealth = new(100f);
        public NetworkVariable<bool> netIsDead = new();
        public NetworkVariable<int> kills = new();
        public NetworkVariable<int> deaths = new();
        public NetworkVariable<int> assists = new();

        public NetworkVariable<float> damageDealt = new();

        public NetworkVariable<int> playerMaterialIndex = new(0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public NetworkVariable<FixedString64Bytes> playerName = new("Player",
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public NetworkVariable<bool> netIsCrouching = new(false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        #endregion

        #region Additional Serialized Fields

        [SerializeField] private Material[] playerMaterials;
        [SerializeField] private CinemachineCamera worldCamera;
        [SerializeField] private GameObject worldModelRoot;
        [SerializeField] private MantleController mantleController;
        [SerializeField] private UpperBodyPitch upperBodyPitch;

        [Header("Sub-Controllers")]
        [SerializeField] private PlayerStatsController statsController;
        [SerializeField] private PlayerTagController tagController;
        [SerializeField] private PlayerPodiumController podiumController;
        [SerializeField] private PlayerVisualController visualController;
        [SerializeField] private PlayerAnimationController animationController;
        [SerializeField] private PlayerShadow playerShadow;
        [SerializeField] private PlayerMovementController movementController;

        [SerializeField] private PlayerLookController lookController;
        [SerializeField] private PlayerHealthController healthController;
        [SerializeField] private HopballController hopballController;

        [SerializeField] private PlayerTeamManager playerTeamManager;

        #endregion

        #region Additional Private Fields

        private int _playerLayer;
        private int _enemyLayer;

        #endregion

        #region Unity Lifecycle

        private void Awake() {
            // Initialize transform reference
            if(tr == null) {
                tr = transform;
            }

            // Cache layer masks
            _playerLayer = LayerMask.NameToLayer("Player");
            _enemyLayer = LayerMask.NameToLayer("Enemy");

            // All component references should be assigned in the inspector
            // Only use GetComponent as a last resort fallback if not assigned
            if(characterController == null) {
                characterController = GetComponent<CharacterController>();
            }

            if(playerTeamManager == null) {
                playerTeamManager = GetComponent<PlayerTeamManager>();
            }

            if(hopballController == null) {
                hopballController = GetComponent<HopballController>();
            }
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            // All component references should be assigned in the inspector
            // Only use GetComponent as a last resort fallback if not assigned
            // (This should rarely happen if prefab is set up correctly)

            // Network-dependent initialization
            playerMaterialIndex.OnValueChanged -= OnMatChanged;
            playerMaterialIndex.OnValueChanged += OnMatChanged;
            netHealth.OnValueChanged -= OnHealthChanged;
            netHealth.OnValueChanged += OnHealthChanged;
            netIsCrouching.OnValueChanged -= OnCrouchStateChanged;
            netIsCrouching.OnValueChanged += OnCrouchStateChanged;

            if(visualController != null) {
                visualController.ApplyPlayerMaterial(playerMaterialIndex.Value);
            }

            if(characterController.enabled == false)
                characterController.enabled = true;

            // Note: Main player object should be set to Default layer in inspector
            // Ragdoll components are set to Enemy layer in PlayerRagdoll.OnNetworkSpawn()

            var gameMenu = GameMenuManager.Instance;
            if(gameMenu && gameMenu.TryGetComponent(out UIDocument doc)) {
                var root = doc.rootVisualElement;
                var rootContainer = root?.Q<VisualElement>("root-container");
                if(rootContainer != null)
                    rootContainer.style.display = DisplayStyle.Flex;
            }

            HUDManager.Instance.ShowHUD();
            if(IsOwner && fpCamera && lookController != null) {
                fpCamera.Lens.FieldOfView = lookController.BaseFov;
            }

            GameMenuManager.Instance.IsPostMatch = false;
            GameMenuManager.Instance.ShowInGameHudAfterPostMatch();
            
            // Track spawn time to prevent landing sounds on initial spawn
            if(animationController != null) {
                animationController.ResetSpawnTime();
            }

            if(GameMenuManager.Instance.IsPaused) {
                GameMenuManager.Instance.TogglePause();
            }

            if(IsOwner) {
                playerName.Value = PlayerPrefs.GetString("PlayerName", "Unknown Player");
                var savedColorIndex = PlayerPrefs.GetInt("PlayerColorIndex", 0);
                playerMaterialIndex.Value = savedColorIndex;
                GrappleUIManager.Instance.RegisterLocalPlayer(this);
                
                // Initialize tag status for HUD in Tag mode
                var matchSettings = MatchSettingsManager.Instance;
                if(matchSettings != null && matchSettings.selectedGameModeId == "Gun Tag" && tagController != null) {
                    HUDManager.Instance.UpdateTagStatus(tagController.isTagged.Value);
                }
                
                if(playerShadow != null) {
                    playerShadow.SetWorldWeaponPrefabsShadowMode(ShadowCastingMode.ShadowsOnly);
                }
            } else {
                // Ensure world model root and weapon are active for non-owner players
                if(worldModelRoot != null && !worldModelRoot.activeSelf) {
                    worldModelRoot.SetActive(true);
                }

                if(worldWeapon != null && !worldWeapon.gameObject.activeSelf) {
                        worldWeapon.gameObject.SetActive(true);
                    }

                // Invalidate renderer cache to force refresh
                if(visualController != null) {
                    visualController.InvalidateRendererCache();
                }

                // Enable all renderers and set proper shadow modes
                if(visualController != null) {
                    visualController.SetRenderersEnabled(true);
                }

                if(playerShadow != null) {
                    playerShadow.SetSkinnedMeshRenderersShadowMode(ShadowCastingMode.On, null, true);
                    playerShadow.SetWorldWeaponRenderersShadowMode(ShadowCastingMode.On);
                }

                // Force bounds update immediately
                if(visualController != null) {
                    visualController.ForceRendererBoundsUpdate();
                }
            }
        }

        public override void OnNetworkDespawn() {
            base.OnNetworkDespawn();

            playerMaterialIndex.OnValueChanged -= OnMatChanged;
            netHealth.OnValueChanged -= OnHealthChanged;
            netIsCrouching.OnValueChanged -= OnCrouchStateChanged;
        }
            
        private void OnMatChanged(int _, int newIdx) {
            if(visualController != null) {
                visualController.ApplyPlayerMaterial(newIdx);
            }
        }

        private void OnHealthChanged(float oldV, float newV) {
            if(IsOwner) HUDManager.Instance.UpdateHealth(newV, 100f);
        }

        private void OnCrouchStateChanged(bool oldValue, bool newValue) {
            if(movementController != null) {
                movementController.UpdateCrouch(fpCamera);
                }
        }

        private void Update() {
            if(IsServer) {
                Vector3 authPos = networkTransform.transform.position;
                if(authPos.y <= 600f) {
                    // Only trigger OOB death if player is not already dead and cooldown has passed
                    // The health controller now resets netIsDead immediately when respawn starts,
                    // so this check prevents race conditions during respawn
                    if(!netIsDead.Value && Time.time - _lastDeathTime >= 4f) {
                        _lastDeathTime = Time.time;
                        // OOB death handled by health controller
                        if(healthController != null) {
                            healthController.ApplyDamageServer_Auth(1000f, tr.position, Vector3.up, ulong.MaxValue);
                        }
                    }
                }

                if(healthController != null) {
                    healthController.UpdateHealthRegeneration();
                }
            }

            if(netIsDead.Value || characterController.enabled == false) return;

            if(IsOwner) {
                if(movementController != null) {
                    movementController.UpdateMovement(fpCamera);
                    movementController.UpdateCrouch(fpCamera);

                    if(animationController != null) {
                        animationController.UpdateFallingState(movementController.IsGrounded,
                            movementController.VerticalVelocity, tr.position);
                        animationController.UpdateAnimator(movementController.HorizontalVelocity,
                            movementController.MaxSpeed, movementController.CachedHorizontalSpeedSqr);
                    }
            }

                if(lookController != null) {
                    lookController.UpdateSpeedFov();
            }

                if(statsController != null) {
                    statsController.TrackVelocity();
                }
            } else {
                if(movementController != null) {
                    movementController.UpdateCrouch(fpCamera);
                }

                if(animationController != null) {
                    animationController.SetCrouching(netIsCrouching.Value);
            }

                // Periodic visibility check for non-owner players
                // This helps catch and fix cases where renderers become invisible due to bounds issues
                if(Time.frameCount % 60 == 0) {
                    if(visualController != null) {
                        visualController.VerifyAndFixVisibility();
                    }
                }
            }
        }

        private void LateUpdate() {
            if(!IsOwner || netIsDead.Value) return;

            if(lookController != null) {
                lookController.UpdateLook();
            }
        }

        #endregion

        #region Collision Handling

        private void OnControllerColliderHit(ControllerColliderHit hit) {
            if(hit.gameObject.CompareTag("JumpPad")) {
                // Cancel any active grapple before launching
            grappleController.CancelGrapple();
                
                // Use the jump pad's own transform.up (surface normal) instead of collision normal
                // This preserves horizontal velocity (e.g., from grappling) while adding vertical boost
                if(movementController != null) {
                    Vector3 padNormal = hit.gameObject.transform.up;
                    movementController.LaunchFromJumpPad(padNormal);
            } else {
                    // Fallback to old behavior if movement controller not available
                    TryJump(15f);
                }
            } else if(hit.gameObject.CompareTag("MegaPad")) {
                // Cancel any active grapple before launching
                grappleController.CancelGrapple();
                
                // Mega jump pad: provides greater boost
                // Use the jump pad's own transform.up (surface normal) instead of collision normal
                if(movementController != null) {
                    Vector3 padNormal = hit.gameObject.transform.up;
                    movementController.LaunchFromJumpPad(padNormal, force: 30f);
            } else {
                    // Fallback to old behavior if movement controller not available
                    TryJump(30f);
                }
            } else {
                // Cancel grapple for other collisions (non-jump pad)
                grappleController.CancelGrapple();
            }
        }

        #endregion

        #region Damage & Death Methods

        public bool ApplyDamageServer_Auth(float amount, Vector3 hitPoint, Vector3 hitDirection, ulong attackerId, string bodyPartTag = null, bool isHeadshot = false) {
            if(healthController != null) {
                return healthController.ApplyDamageServer_Auth(amount, hitPoint, hitDirection, attackerId, bodyPartTag, isHeadshot);
                }

                return false;
        }

        #endregion

        #region Health & Animation

        public void ResetHealthAndRegenerationState() {
            if(healthController != null) {
                healthController.ResetHealthAndRegenerationState();
            }
        }

        public void ResetWeaponState(bool resetAllAmmo = false, bool switchToWeapon0 = false, bool updateHUD = false) {
            if(!IsOwner || weaponManager == null) return;

            if(resetAllAmmo) {
                weaponManager.ResetAllWeaponAmmo();
            }

            var currentWeapon = weaponManager.CurrentWeapon;
            if(currentWeapon != null) {
                currentWeapon.ResetWeapon();
                currentWeapon.CurrentDamageMultiplier = 1f;

                var weaponInstance = currentWeapon.GetWeaponPrefab();
                if(weaponInstance != null) {
                    // Ensure the weapon GameObject and all its parents are active
                    var parent = weaponInstance.transform.parent;
                    while(parent != null) {
                        if(!parent.gameObject.activeSelf) {
                            parent.gameObject.SetActive(true);
                        }
                        parent = parent.parent;
                    }

                    weaponInstance.SetActive(true);

                    // Set position and rotation
                    weaponInstance.transform.localPosition = currentWeapon.GetSpawnPosition();
                    weaponInstance.transform.localEulerAngles = currentWeapon.GetSpawnRotation();

                    // Get cached MeshRenderers or cache them if not found
                    if(!_cachedWeaponRenderers.TryGetValue(weaponInstance, out var meshRenderers)) {
                        meshRenderers = weaponInstance.GetComponentsInChildren<MeshRenderer>(true);
                        _cachedWeaponRenderers[weaponInstance] = meshRenderers;
                    }

                    // Explicitly enable all MeshRenderers in the weapon hierarchy
                    foreach(var meshRenderer in meshRenderers) {
                        if(meshRenderer != null) {
                            meshRenderer.enabled = true;
                        }
                    }
                }

                if(worldWeapon != null) {
                    worldWeapon.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                }

                if(updateHUD) {
                    HUDManager.Instance.UpdateAmmo(currentWeapon.currentAmmo, currentWeapon.GetMagSize());
                    HUDManager.Instance.UpdateHealth(netHealth.Value, 100f);
                }
            }

            if(switchToWeapon0) {
                playerInput.SwitchWeapon(0);
            }
        }

        #endregion
    }
}