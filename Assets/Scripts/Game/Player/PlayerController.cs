using Game.Weapons;
using Network;
using Network.Rpc;
using Network.Singletons;
using OSI;
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

        [Header("Core Components")]
        [SerializeField] private Transform playerTransform;
        [SerializeField] private CharacterController characterController;
        [SerializeField] private PlayerInput playerInput;
        [SerializeField] private UnityEngine.InputSystem.PlayerInput unityPlayerInput;
        [SerializeField] private Animator playerAnimator;
        [SerializeField] private ClientNetworkTransform clientNetworkTransform;
        [SerializeField] private Target playerTarget;

        [Header("Cameras")]
        [SerializeField] private CinemachineCamera fpCamera;
        [SerializeField] private Camera weaponCamera;
        [SerializeField] private CinemachineCamera deathCamera;


        [Header("Player Model")]
        [SerializeField] private GameObject playerModelRoot;
        [SerializeField] private SkinnedMeshRenderer playerMesh;
        [SerializeField] private Material[] playerMaterials;
        [SerializeField] private PlayerVisualController visualController;
        [SerializeField] private PlayerShadow playerShadow;
        [SerializeField] private UpperBodyPitch upperBodyPitch;
        [SerializeField] private PlayerRagdoll playerRagdoll;
        [SerializeField] private Transform deathCameraTarget;

        [Header("Movement Controllers")]
        [SerializeField] private PlayerMovementController movementController;
        [SerializeField] private PlayerLookController lookController;
        [SerializeField] private MantleController mantleController;
        // [SerializeField] private SwingGrapple swingGrapple;
        [SerializeField] private GrappleController grappleController;


        [Header("Gameplay Controllers")]
        [SerializeField] private PlayerStatsController statsController;
        [SerializeField] private PlayerHealthController healthController;
        [SerializeField] private PlayerAnimationController animationController;
        [SerializeField] private PlayerTagController tagController;
        [SerializeField] private PlayerPodiumController podiumController;
        [SerializeField] private HopballController hopballController;
        [SerializeField] private PlayerTeamManager playerTeamManager;
        [SerializeField] private WeaponCameraController weaponCameraController;
        [SerializeField] private DeathCameraController deathCameraController;


        [Header("Weapon System")]
        [SerializeField] private WeaponManager weaponManager;
        [SerializeField] private Weapon weaponComponent;
        [SerializeField] private MeshRenderer worldWeapon;
        [SerializeField] private Transform worldWeaponSocket;
        [SerializeField] private GameObject[] worldWeaponPrefabs;


        [Header("Audio / Visual Effects")]
        [SerializeField] private AudioListener audioListener;
        [SerializeField] private NetworkDamageRelay damageRelay;
        [SerializeField] private NetworkFxRelay fxRelay;
        [SerializeField] private NetworkSfxRelay sfxRelay;
        [SerializeField] private CinemachineImpulseSource impulseSource;
        [SerializeField] private SpeedTrail speedTrail;


        [Header("Layers")]
        [SerializeField] private LayerMask worldLayer;
        [SerializeField] private LayerMask playerLayer;
        [SerializeField] private LayerMask enemyLayer;
        [SerializeField] private LayerMask weaponLayer;
        [SerializeField] private LayerMask hopballLayer;

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
        private readonly System.Collections.Generic.Dictionary<GameObject, MeshRenderer[]> _cachedWeaponRenderers =
            new();

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

        #region Unity Lifecycle

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

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
            PostMatchManager.Instance.ShowInGameHudAfterPostMatch();

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
                if(playerModelRoot != null && !playerModelRoot.activeSelf) {
                    playerModelRoot.SetActive(true);
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
                var authPos = clientNetworkTransform.transform.position;
                if(authPos.y <= 600f) {
                    // Only trigger OOB death if player is not already dead and cooldown has passed
                    // The health controller now resets netIsDead immediately when respawn starts,
                    // so this check prevents race conditions during respawn
                    if(!netIsDead.Value && Time.time - _lastDeathTime >= 4f) {
                        _lastDeathTime = Time.time;
                        // OOB death handled by health controller
                        if(healthController != null) {
                            healthController.ApplyDamageServer_Auth(1000f, playerTransform.position, Vector3.up, ulong.MaxValue);
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
                            movementController.VerticalVelocity, playerTransform.position);
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
                if(Time.frameCount % 60 != 0) return;
                if(visualController != null) {
                    visualController.VerifyAndFixVisibility();
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
                    var padNormal = hit.gameObject.transform.up;
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
                    var padNormal = hit.gameObject.transform.up;
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

        public bool ApplyDamageServer_Auth(float amount, Vector3 hitPoint, Vector3 hitDirection, ulong attackerId,
            string bodyPartTag = null, bool isHeadshot = false) {
            if(healthController != null) {
                return healthController.ApplyDamageServer_Auth(amount, hitPoint, hitDirection, attackerId, bodyPartTag,
                    isHeadshot);
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