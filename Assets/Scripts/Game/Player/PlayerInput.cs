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

        private bool IsPaused => GameMenuManager.Instance.IsPaused;
        private bool IsPausedOrDead => (GameMenuManager.Instance.IsPaused) || playerController.netIsDead.Value;

        private Weapon CurrentWeapon => weaponManager.CurrentWeapon;

        private bool _sprintBtnDown;
        private bool _crouchBtnDown;

        #region Unity Methods

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            weaponManager.InitializeWeapons();

            var playerInputComponent = GetComponent<UnityEngine.InputSystem.PlayerInput>();
            
            if(!IsOwner) {
                fpCamera.gameObject.SetActive(false);
                audioListener.enabled = false;

                if(playerInputComponent != null) {
                    playerInputComponent.enabled = false;
                }
            } else {
                // Re-enable PlayerInput for owner (it was disabled during instantiation to prevent control scheme errors)
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

        private void LateUpdate() {
            if(!IsOwner || !CurrentWeapon || !weaponManager) return;

            var fireMode = weaponManager.GetWeaponDataByIndex(weaponManager.CurrentWeaponIndex)?.fireMode;

            // Use Input System actions instead of direct input
            var playerInputComponent = GetComponent<UnityEngine.InputSystem.PlayerInput>();
            var playerMap = playerInputComponent?.actions?.FindActionMap("Player");
            var attackAction = playerMap?.FindAction("Attack");
            var jumpAction = playerMap?.FindAction("Jump");

            if(!IsPausedOrDead && fireMode == "Full" && attackAction != null && attackAction.IsPressed() && !mantleController.IsMantling) {
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
            
            if(!IsPausedOrDead && (jumpPressed || scrollPressed) && !mantleController.IsMantling) {
                if(!playerController.IsGrounded) {
                    mantleController.TryMantle();

                    // If we started mantling, don't jump
                    if(mantleController.IsMantling) {
                        return;
                    }
                }

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
                GameMenuManager.Instance.ShowScoreboard();
            } else if(GameMenuManager.Instance.IsScoreboardVisible) {
                GameMenuManager.Instance.HideScoreboard();
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

            playerController.moveInput = value.Get<Vector2>();
        }

        private void OnSprint(InputValue value) {
            if(!IsOwner) return;
            if(IsPausedOrDead) return;

            // if(!dashController.IsDashing) {
            //     dashController.OnDashInput();
            // }
            
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
            if(!IsOwner || IsPausedOrDead || mantleController.IsMantling) return;

            playerController.TryJump();

            if(grappleController.IsGrappling) {
                grappleController.CancelGrapple();
            }
        }

        private void OnAttack(InputValue value) {
            if(!IsOwner || IsPausedOrDead || mantleController.IsMantling) return;

            var fireMode = weaponManager.GetWeaponDataByIndex(weaponManager.CurrentWeaponIndex)?.fireMode;
            if(CurrentWeapon && fireMode == "Semi") {
                CurrentWeapon.Shoot();
            }
        }

        private void OnGrapple(InputValue value) {
            if(!IsOwner || IsPausedOrDead || mantleController.IsMantling || GameMenuManager.Instance.IsPostMatch) return;
            
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

        private void OnPrimary(InputValue value) {
            if(!IsOwner || IsPausedOrDead || mantleController.IsMantling) return;

            SwitchWeapon(0);
        }

        private void OnSecondary(InputValue value) {
            if(!IsOwner || IsPausedOrDead || mantleController.IsMantling) return;

            SwitchWeapon(1);
        }

        private void OnTertiary(InputValue value) {
            if(!IsOwner || IsPausedOrDead || mantleController.IsMantling) return;

            Debug.Log("Tertiary weapon input received.");
            //SwitchWeapon(2);
        }

        public void SwitchWeapon(int weaponIndex) {
            if(weaponManager.CurrentWeaponIndex == weaponIndex || weaponManager.IsSwitchingWeapon ||
               !CurrentWeapon) return;

            // Reload cancellation is handled by Weapon.SwitchToWeapon() when the weapon switch completes
            weaponManager.SwitchWeapon(weaponIndex);
        }

        private void OnReload(InputValue value) {
            if(!IsOwner || IsPausedOrDead || !CurrentWeapon || mantleController.IsMantling) return;

            CurrentWeapon.StartReload();
        }

        #endregion

        #region System

        private void OnPause(InputValue value) {
            if(!IsOwner) return;
            GameMenuManager.Instance.TogglePause();
        }

        #endregion
    }
}