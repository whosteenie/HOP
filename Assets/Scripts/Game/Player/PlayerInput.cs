using Game.Weapons;
using Network.Singletons;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Player {
    public class PlayerInput : NetworkBehaviour {
        #region Serialized Fields

        [Header("Components")] [SerializeField]
        private PlayerController playerController;

        [SerializeField] Animator playerAnimator;
        [SerializeField] private UnityEngine.InputSystem.PlayerInput playerInputComponent;

        [SerializeField] private CinemachineCamera fpCamera;
        [SerializeField] private AudioListener audioListener;
        [SerializeField] private WeaponManager weaponManager;
        [SerializeField] private GrappleController grappleController;
        [SerializeField] private SwingGrapple swingGrapple;
        [SerializeField] private MantleController mantleController;
        [SerializeField] private DashController dashController;

        [Header("Input Settings")] [SerializeField]
        private bool toggleSprint = true;

        [SerializeField] private bool toggleCrouch = true;

        #endregion

        private bool IsPaused => GameMenuManager.Instance?.IsPaused ?? false;
        private bool IsPausedOrDead => (GameMenuManager.Instance?.IsPaused ?? false) || playerController.netIsDead.Value;
        private bool IsPreMatch => GameMenuManager.Instance?.IsPreMatch ?? false;
        private bool IsPreMatchOrPausedOrDead => IsPreMatch || IsPausedOrDead;

        private Weapon CurrentWeapon => weaponManager.CurrentWeapon;

        private bool _sprintBtnDown;
        private bool _crouchBtnDown;

        #region Unity Methods

        private void Awake() {
            // Component reference should be assigned in the inspector
            // Only use GetComponent as a last resort fallback if not assigned
            if(playerInputComponent == null) {
                playerInputComponent = GetComponent<UnityEngine.InputSystem.PlayerInput>();
            }
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            weaponManager.InitializeWeapons();

            // Component reference should be assigned in the inspector
            // Only use GetComponent as a last resort fallback if not assigned
            if(playerInputComponent == null) {
                playerInputComponent = GetComponent<UnityEngine.InputSystem.PlayerInput>();
            }

            if(!IsOwner) {
                fpCamera.gameObject.SetActive(false);
                audioListener.enabled = false;

                if(playerInputComponent != null) {
                    playerInputComponent.enabled = false;
                }
            } else {
                if(playerInputComponent != null) {
                    playerInputComponent.enabled = true;
                }
            }
        }

        private void Start() {
            if(!IsOwner) return;

            Cursor.visible = false;
            Cursor.lockState = CursorLockMode.Locked;
        }

        // Direct Input System polling for certain actions
        private void LateUpdate() {
            if(!IsOwner || !CurrentWeapon || !weaponManager) return;

            var fireMode = weaponManager.GetWeaponDataByIndex(weaponManager.CurrentWeaponIndex)?.fireMode;

            // Use Input System actions instead of direct input
            // Component reference should be assigned in the inspector
            if(playerInputComponent == null) {
                playerInputComponent = GetComponent<UnityEngine.InputSystem.PlayerInput>();
            }
            
            var playerMap = playerInputComponent?.actions?.FindActionMap("Player");
            var attackAction = playerMap?.FindAction("Attack");
            var jumpAction = playerMap?.FindAction("Jump");

            if(!IsPreMatchOrPausedOrDead && fireMode == "Full" && attackAction != null && attackAction.IsPressed() &&
               !mantleController.IsMantling && !(playerController != null && playerController.IsHoldingHopball)) {
                CurrentWeapon.Shoot();
            }

            // Check jump action or scroll wheel for jump/mantle
            // Check if scroll is bound to jump via PlayerPrefs
            bool jumpPressed = jumpAction != null && jumpAction.IsPressed();
            bool scrollPressed = false;

            // Check PlayerPrefs for scroll bindings
            string jumpBinding0 = PlayerPrefs.GetString("Keybind_jump_0", "");
            string jumpBinding1 = PlayerPrefs.GetString("Keybind_jump_1", "");

            if(Mouse.current != null && Mouse.current.scroll.value.magnitude > 0f) {
                Vector2 scrollDelta = Mouse.current.scroll.value;

                // Check if scroll down is bound to jump
                if(jumpBinding1 == "SCROLL_DOWN" && scrollDelta.y < 0) {
                    scrollPressed = true;
                } else if(jumpBinding0 == "SCROLL_DOWN" && scrollDelta.y < 0) {
                    scrollPressed = true;
                }
                // Check if scroll up is bound to jump
                else if(jumpBinding1 == "SCROLL_UP" && scrollDelta.y > 0) {
                    scrollPressed = true;
                } else if(jumpBinding0 == "SCROLL_UP" && scrollDelta.y > 0) {
                    scrollPressed = true;
                }
            }

            if(!IsPreMatchOrPausedOrDead && (jumpPressed || scrollPressed) && mantleController.CanJump) {
                // Check if hold-to-mantle is enabled
                bool holdMantleEnabled = PlayerPrefs.GetInt("HoldMantle", 1) == 1;

                // Try mantle if enabled and not grounded
                if(holdMantleEnabled && !playerController.IsGrounded) {
                    mantleController.TryMantle();

                    // If we started mantling, don't jump
                    if(mantleController.IsMantling) {
                        return;
                    }
                }

                // Always allow hold-to-jump (for scroll wheel support)
                playerController.TryJump();
                grappleController.CancelGrapple();
            }

            CurrentWeapon.UpdateDamageMultiplier();

            var weaponData = weaponManager.GetWeaponDataByIndex(weaponManager.CurrentWeaponIndex);
            if(weaponData) {
                HUDManager.Instance.UpdateMultiplier(CurrentWeapon.CurrentDamageMultiplier,
                    weaponData.maxDamageMultiplier);
            }

            if(!IsPaused && Keyboard.current.tabKey.isPressed) {
                ScoreboardManager.Instance?.ShowScoreboard();
            } else if(ScoreboardManager.Instance != null && ScoreboardManager.Instance.IsScoreboardVisible) {
                ScoreboardManager.Instance.HideScoreboard();
            }

            // OnSwing
            // if(IsOwner && !IsPausedOrDead && !mantleController.IsMantling) {
            //     if(Keyboard.current.eKey.isPressed) {
            //         if(!swingGrapple.IsSwinging) {
            //             swingGrapple.TryStartSwing();
            //         }
            //     } else {
            //         if(swingGrapple.IsSwinging) {
            //             swingGrapple.CancelSwing();
            //         }
            //     }
            // }
        }

        #endregion

        #region Movement

        private void OnLook(InputValue value) {
            if(!IsOwner) return;
            if(IsPausedOrDead) {
                playerController.lookInput = Vector2.zero;
                return;
            }

            playerController.lookInput = value.Get<Vector2>();
        }

        private void OnMove(InputValue value) {
            if(!IsOwner) return;
            if(IsPaused || GameMenuManager.Instance.IsPostMatch) {
                playerController.moveInput = Vector2.zero;
                return;
            }

            // Allow movement input to be set even during pre-match
            // It will be ignored during movement processing instead
            playerController.moveInput = value.Get<Vector2>();
        }

        private void OnSprint(InputValue value) {
            if(!IsOwner) return;
            if(IsPausedOrDead) {
                if(!toggleSprint)
                    playerController.sprintInput = false;
                return;
            }

            bool pressed = value.isPressed;

            if(toggleSprint) {
                // Toggle only on rising edge
                if(pressed && !_sprintBtnDown) {
                    playerController.sprintInput = !playerController.sprintInput;
                }

                _sprintBtnDown = pressed;
            } else {
                // Hold-to-sprint
                playerController.sprintInput = pressed;
            }
        }

        private void OnCrouch(InputValue value) {
            if(!IsOwner) return;
            if(IsPausedOrDead || mantleController.IsMantling) {
                if(!toggleCrouch)
                    playerController.crouchInput = false;
                return;
            }

            bool pressed = value.isPressed;

            if(toggleCrouch) {
                // Toggle only on rising edge
                if(pressed && !_crouchBtnDown) {
                    playerController.crouchInput = !playerController.crouchInput;
                }

                _crouchBtnDown = pressed;
            } else {
                // Hold-to-crouch
                playerController.crouchInput = pressed;
            }
        }

        private void OnJump(InputValue value) {
            if(!IsOwner || IsPausedOrDead || mantleController.IsMantling) return;

            if(!playerController.IsGrounded) {
                mantleController.TryMantle();

                // If we started mantling, don't jump
                if(mantleController.IsMantling) {
                    return;
                }
            }

            playerController.TryJump();

            if(grappleController.IsGrappling) {
                grappleController.CancelGrapple();
            }
        }

        private void OnScrollWheel(InputValue value) {
            // TODO: Fix scroll wheel input so we don't have to use Mouse.current.scroll in LateUpdate
            if(!IsOwner || IsPreMatchOrPausedOrDead || mantleController.IsMantling) return;

            playerController.TryJump();

            if(grappleController.IsGrappling) {
                grappleController.CancelGrapple();
            }
        }

        private void OnAttack(InputValue value) {
            if(!IsOwner || IsPreMatchOrPausedOrDead || mantleController.IsMantling) return;
            if(playerController != null && playerController.IsHoldingHopball) return; // Prevent shooting while holding hopball

            var fireMode = weaponManager.GetWeaponDataByIndex(weaponManager.CurrentWeaponIndex)?.fireMode;
            if(CurrentWeapon && fireMode == "Semi") {
                CurrentWeapon.Shoot();
            }
        }

        private void OnGrapple(InputValue value) {
            if(!IsOwner || IsPreMatchOrPausedOrDead || mantleController.IsMantling ||
               GameMenuManager.Instance.IsPostMatch) return;

            if(grappleController.IsGrappling) {
                grappleController.CancelGrapple();
            } else {
                grappleController.TryGrapple();
            }
        }

        private void OnSwing(InputValue value) {
            // TODO: fix hold input
        }

        #endregion

        #region Weapons

        private void OnPrimary(InputValue _) {
            if(!IsOwner || IsPausedOrDead || mantleController.IsMantling) return;

            SwitchWeapon(0);
        }

        private void OnSecondary(InputValue _) {
            if(!IsOwner || IsPausedOrDead || mantleController.IsMantling) return;

            SwitchWeapon(1);
        }

        private void OnTertiary(InputValue _) {
            if(!IsOwner || IsPausedOrDead || mantleController.IsMantling) return;

            //SwitchWeapon(2);
        }

        private void OnNextWeapon(InputValue _) {
            if(!IsOwner || IsPausedOrDead || mantleController.IsMantling) return;

            SwitchWeapon((weaponManager.CurrentWeaponIndex + 1) % weaponManager.WeaponCount);
        }
        
        private void OnPreviousWeapon(InputValue _) {
            if(!IsOwner || IsPausedOrDead || mantleController.IsMantling) return;

            SwitchWeapon((weaponManager.CurrentWeaponIndex - 1) % weaponManager.WeaponCount);
        }

        public void SwitchWeapon(int weaponIndex) {
            if(weaponManager.IsSwitchingWeapon || !CurrentWeapon) return;
            
            // If holding hopball, drop it first (WeaponManager will handle this, but we can also do it here for clarity)
            // Actually, WeaponManager.SwitchWeapon() will handle dropping, so we just proceed
            // Reload cancellation is handled by Weapon.SwitchToWeapon() when the weapon switch completes
            weaponManager.SwitchWeapon(weaponIndex);
        }

        private void OnReload(InputValue _) {
            if(!IsOwner || IsPreMatchOrPausedOrDead || !CurrentWeapon || mantleController.IsMantling) return;
            if(playerController != null && playerController.IsHoldingHopball) return; // Prevent reloading while holding hopball

            CurrentWeapon.StartReload();
        }

        #endregion

        #region System

        private void OnPause(InputValue _) {
            if(!IsOwner) return;
            GameMenuManager.Instance.TogglePause();
        }

        private void OnInteract(InputValue _) {
            Debug.LogWarning("[Player Input] Interact pressed - trying to pick up hopball.");
            if(!IsOwner || IsPausedOrDead || mantleController.IsMantling) return;
            
            playerController.PickupHopball();
        }

        #endregion
    }
}