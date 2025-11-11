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

        [SerializeField] private CinemachineCamera fpCamera;
        [SerializeField] private AudioListener audioListener;
        [SerializeField] private WeaponManager weaponManager;
        [SerializeField] private GrappleController grappleController;

        [Header("Input Settings")] [SerializeField]
        private bool toggleSprint = true;

        [SerializeField] private bool toggleCrouch;

        #endregion

        private bool IsPaused => GameMenuManager.Instance.IsPaused;
        private bool IsPausedOrDead => (GameMenuManager.Instance.IsPaused) || playerController.netIsDead.Value;

        private GameObject CurrentWeaponModel =>
            fpCamera.transform.GetChild(weaponManager.currentWeaponIndex).gameObject;

        private Weapon CurrentWeapon => weaponManager.CurrentWeapon;

        #region Unity Methods

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            
            weaponManager.InitializeWeapons(fpCamera, playerController);

            if(!IsOwner) {
                fpCamera.gameObject.SetActive(false);
                audioListener.enabled = false;

                var playerInputComponent = GetComponent<UnityEngine.InputSystem.PlayerInput>();
                if(playerInputComponent != null) {
                    playerInputComponent.enabled = false;
                }
            }
        }

        private void Start() {
            if(!IsOwner) return;

            Cursor.visible = false;
            Cursor.lockState = CursorLockMode.Locked;
        }

        private void LateUpdate() {
            if(!IsOwner) return;
            if(CurrentWeapon == null) return;

            if(!IsPausedOrDead && CurrentWeapon.fireMode == "Full" &&
               (Mouse.current.leftButton.isPressed || Mouse.current.rightButton.isPressed)) {
                CurrentWeapon.Shoot();
            }

            if(!IsPausedOrDead && Mouse.current.scroll.value.magnitude > 0f) {
                playerController.TryJump();
                grappleController.CancelGrapple();
            }

            if(CurrentWeapon)
                CurrentWeapon.UpdateDamageMultiplier();

            if(CurrentWeapon)
                HUDManager.Instance.UpdateMultiplier(CurrentWeapon.CurrentDamageMultiplier,
                    CurrentWeapon.maxDamageMultiplier);

            if(!IsPaused && Keyboard.current.tabKey.isPressed) {
                GameMenuManager.Instance.ShowScoreboard();
            } else if(GameMenuManager.Instance.IsScoreboardVisible) {
                GameMenuManager.Instance.HideScoreboard();
            }
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
            if(IsPaused) {
                playerController.moveInput = Vector2.zero;
                return;
            }
            
            playerController.moveInput = value.Get<Vector2>();
        }

        private void OnSprint(InputValue value) {
            if(!IsOwner) return;
            if(IsPausedOrDead) {
                if(!toggleSprint)
                    playerController.sprintInput = false;
                return;
            }

            if(toggleSprint) {
                // Toggle mode
                playerController.sprintInput = !playerController.sprintInput;
            } else {
                // Hold mode
                playerController.sprintInput = value.isPressed;
            }
        }

        private void OnCrouch(InputValue value) {
            if(!IsOwner) return;
            if(IsPausedOrDead) {
                if(!toggleCrouch)
                    playerController.crouchInput = false;
                return;
            }

            if(toggleCrouch) {
                // Toggle mode
                playerController.crouchInput = !playerController.crouchInput;
            } else {
                // Hold mode
                playerController.crouchInput = value.isPressed;
            }
        }

        private void OnJump(InputValue value) {
            if(!IsOwner) return;
            if(IsPausedOrDead) return;

            playerController.TryJump();

            if(grappleController.IsGrappling) {
                grappleController.CancelGrapple();
            }
        }

        private void OnScrollWheel(InputValue value) {
            // TODO: Fix scroll wheel input so we don't have to use Mouse.current.scroll in LateUpdate
            if(!IsOwner) return;
            if(IsPausedOrDead) return;

            playerController.TryJump();

            if(grappleController.IsGrappling) {
                grappleController.CancelGrapple();
            }
        }

        private void OnAttack(InputValue value) {
            if(!IsOwner) return;
            if(IsPausedOrDead) return;

            if(CurrentWeapon && CurrentWeapon.fireMode == "Semi") {
                CurrentWeapon.Shoot();
            }
        }

        private void OnGrapple(InputValue value) {
            if(!IsOwner) return;
            if(IsPausedOrDead) return;

            if(grappleController.IsGrappling) {
                grappleController.CancelGrapple();
            } else {
                grappleController.TryGrapple();
            }
        }

        #endregion

        #region Weapons

        private void OnPrimary(InputValue value) {
            if(!IsOwner) return;

            SwitchWeapon(0);
            Debug.Log("Equipped Primary");
        }

        private void OnSecondary(InputValue value) {
            if(!IsOwner) return;

            SwitchWeapon(1);
            Debug.Log("Equipped Secondary");
        }

        private void OnTertiary(InputValue value) {
            if(!IsOwner) return;

            SwitchWeapon(2);
            Debug.Log("Equipped Tertiary");
        }

        public void SwitchWeapon(int weaponIndex) {
            if(IsPausedOrDead || weaponManager.currentWeaponIndex == weaponIndex) return;

            if(CurrentWeapon.IsReloading) {
                CurrentWeapon.CancelReload();
            }

            CurrentWeaponModel.SetActive(false);
            weaponManager.currentWeaponIndex = weaponIndex;
            weaponManager.CurrentWeapon.BindAndResolve(fpCamera, playerController);
            CurrentWeaponModel.transform.localPosition = CurrentWeapon.spawnPosition;
            CurrentWeaponModel.transform.localEulerAngles = CurrentWeapon.spawnRotation;
            CurrentWeaponModel.SetActive(true);
            HUDManager.Instance.UpdateAmmo(CurrentWeapon.currentAmmo, CurrentWeapon.magSize);
        }

        private void OnReload(InputValue value) {
            if(!IsOwner) return;
            if(IsPausedOrDead) return;

            if(weaponManager.CurrentWeapon) {
                weaponManager.CurrentWeapon.StartReload();
            }
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