using Game.Weapons;
using JetBrains.Annotations;
using Network.Singletons;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Player {
    public class PlayerInput : NetworkBehaviour {
        #region Serialized Fields

        [Header("Components")]
        [SerializeField] private PlayerController playerController;

        private UnityEngine.InputSystem.PlayerInput _playerInputComponent;

        private CinemachineCamera _fpCamera;
        private AudioListener _audioListener;
        private WeaponManager _weaponManager;
        private GrappleController _grappleController;
        private SwingGrapple _swingGrapple;
        private MantleController _mantleController;
        // [SerializeField] private DashController dashController;

        [Header("Input Settings")]
        [SerializeField] private bool toggleSprint = true;

        [SerializeField] private bool toggleCrouch = true;

        #endregion

        private static bool IsPaused => GameMenuManager.Instance?.IsPaused ?? false;

        private bool IsPausedOrDead =>
            (GameMenuManager.Instance?.IsPaused ?? false) || (playerController?.IsDead ?? false);

        private static bool IsPreMatch => GameMenuManager.Instance?.IsPreMatch ?? false;
        private bool IsPreMatchOrPausedOrDead => IsPreMatch || IsPausedOrDead;

        private WeaponManager WeaponManager {
            get {
                if(_weaponManager == null && playerController != null) {
                    _weaponManager = playerController.WeaponManager;
                }

                return _weaponManager;
            }
        }

        private GrappleController GrappleController {
            get {
                if(_grappleController == null && playerController != null) {
                    _grappleController = playerController.GrappleController;
                }

                return _grappleController;
            }
        }

        // private SwingGrapple SwingGrapple {
        //     get {
        //         if(_swingGrapple == null) {
        //             _swingGrapple = playerController != null
        //                 ? playerController.SwingGrapple
        //                 : GetComponent<SwingGrapple>();
        //         }
        //
        //         return _swingGrapple;
        //     }
        // }

        private MantleController MantleController {
            get {
                if(_mantleController == null && playerController != null) {
                    _mantleController = playerController.MantleController;
                }

                return _mantleController;
            }
        }

        private Weapon CurrentWeapon => WeaponManager?.CurrentWeapon;

        private bool _sprintBtnDown;
        private bool _crouchBtnDown;
        public bool IsSniperOverlayActive { get; private set; }

        [SerializeField] private float sniperZoomFov = 20f;
        private float _defaultFpFov = -1f;

        #region Unity Methods

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(playerController == null) {
                Debug.LogError("[PlayerInput] PlayerController not found!");
                enabled = false;
                return;
            }

            if(_playerInputComponent == null) _playerInputComponent = playerController.UnityPlayerInput;
            if(_fpCamera == null) _fpCamera = playerController.FpCamera;
            if(_audioListener == null) _audioListener = playerController.AudioListener;

            if(_fpCamera != null) {
                _defaultFpFov = _fpCamera.Lens.FieldOfView;
            }
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            WeaponManager?.InitializeWeapons();

            if(!IsOwner) {
                _fpCamera.gameObject.SetActive(false);
                _audioListener.enabled = false;

                if(_playerInputComponent != null) {
                    _playerInputComponent.enabled = false;
                }
            } else {
                if(_playerInputComponent != null) {
                    _playerInputComponent.enabled = true;
                }

                RefreshSniperOverlayState();
            }
        }

        private void OnDisable() {
            if(!IsOwner) return;
            IsSniperOverlayActive = false;
            SniperOverlayManager.Instance?.ToggleSniperOverlay(false);
            ApplySniperOverlayEffects(false, playZoomSound: false);
        }

        private void Start() {
            if(!IsOwner) return;

            Cursor.visible = false;
            Cursor.lockState = CursorLockMode.Locked;
        }

        // Direct Input System polling for certain actions
        private void LateUpdate() {
            if(!IsOwner || !CurrentWeapon || WeaponManager == null) return;

            var fireMode = WeaponManager?.GetWeaponDataByIndex(WeaponManager.CurrentWeaponIndex)?.fireMode;

            // Use Input System actions instead of direct input
            // Component reference should be assigned in the inspector
            if(_playerInputComponent == null) _playerInputComponent = GetComponent<UnityEngine.InputSystem.PlayerInput>();
            
            var playerMap = _playerInputComponent?.actions?.FindActionMap("Player");
            var attackAction = playerMap?.FindAction("Attack");
            var jumpAction = playerMap?.FindAction("Jump");

            if(!IsPreMatchOrPausedOrDead && fireMode == "Full" && attackAction != null && attackAction.IsPressed() &&
               !(MantleController?.IsMantling ?? false) &&
               !(playerController != null && playerController.IsHoldingHopball)) {
                CurrentWeapon.Shoot();
            }

            // Check jump action or scroll wheel for jump/mantle
            // Check if scroll is bound to jump via PlayerPrefs
            var jumpPressed = jumpAction != null && jumpAction.IsPressed();
            var scrollPressed = false;

            // Check PlayerPrefs for scroll bindings
            var jumpBinding0 = PlayerPrefs.GetString("Keybind_jump_0", "");
            var jumpBinding1 = PlayerPrefs.GetString("Keybind_jump_1", "");

            if(Mouse.current != null && Mouse.current.scroll.value.magnitude > 0f) {
                var scrollDelta = Mouse.current.scroll.value;

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

            if(!IsPreMatchOrPausedOrDead && (jumpPressed || scrollPressed) && (MantleController?.CanJump ?? false)) {
                // Check if hold-to-mantle is enabled
                var holdMantleEnabled = PlayerPrefs.GetInt("HoldMantle", 1) == 1;

                // Try mantle if enabled and not grounded
                if(holdMantleEnabled && !playerController.IsGrounded) {
                    MantleController?.TryMantle();

                    // If we started mantling, don't jump
                    if(MantleController != null && MantleController.IsMantling) {
                        return;
                    }
                }

                // Always allow hold-to-jump (for scroll wheel support)
                playerController.TryJump();
                GrappleController?.CancelGrapple();
            }

            CurrentWeapon.UpdateDamageMultiplier();

            var weaponData = WeaponManager?.GetWeaponDataByIndex(WeaponManager.CurrentWeaponIndex);
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
            // if(IsOwner && !IsPausedOrDead && !(MantleController?.IsMantling ?? false)) {
            //     if(Keyboard.current.eKey.isPressed) {
            //         if(!SwingGrapple?.IsSwinging ?? true) {
            //             SwingGrapple?.TryStartSwing();
            //         }
            //     } else {
            //         if(SwingGrapple?.IsSwinging ?? false) {
            //             SwingGrapple.CancelSwing();
            //         }
            //     }
            // }
        }

        #endregion

        #region Movement

        [UsedImplicitly]
        private void OnLook(InputValue value) {
            if(!IsOwner) return;
            if(IsPausedOrDead) {
                playerController.lookInput = Vector2.zero;
                return;
            }

            var rawDelta = value.Get<Vector2>();

            var zoomMultiplier = IsSniperOverlayActive ? _sniperSensitivityMultiplier : 1f;
            playerController.lookInput = rawDelta * zoomMultiplier;
        }

        [UsedImplicitly]
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

        [UsedImplicitly]
        private void OnSprint(InputValue value) {
            if(!IsOwner) return;
            if(IsPausedOrDead) {
                if(!toggleSprint)
                    playerController.sprintInput = false;
                return;
            }

            var pressed = value.isPressed;

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

        [UsedImplicitly]
        private void OnCrouch(InputValue value) {
            if(!IsOwner) return;
            if(IsPausedOrDead || (MantleController?.IsMantling ?? false)) {
                if(!toggleCrouch)
                    playerController.crouchInput = false;
                return;
            }

            var pressed = value.isPressed;

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

        [UsedImplicitly]
        private void OnJump(InputValue value) {
            if(!IsOwner || IsPausedOrDead || (MantleController?.IsMantling ?? false)) return;

            if(!playerController.IsGrounded) {
                MantleController?.TryMantle();

                // If we started mantling, don't jump
                if(MantleController != null && MantleController.IsMantling) {
                    return;
                }
            }

            playerController.TryJump();

            if(GrappleController != null && GrappleController.IsGrappling) {
                GrappleController.CancelGrapple();
            }
        }

        private void OnScrollWheel(InputValue _) {
            // TODO: Fix scroll wheel input so we don't have to use Mouse.current.scroll in LateUpdate
            if(!IsOwner || IsPreMatchOrPausedOrDead || (MantleController?.IsMantling ?? false)) return;

            playerController.TryJump();

            if(GrappleController != null && GrappleController.IsGrappling) {
                GrappleController.CancelGrapple();
            }
        }

        [UsedImplicitly]
        private void OnAttack(InputValue value) {
            if(!IsOwner || IsPreMatchOrPausedOrDead || (MantleController?.IsMantling ?? false)) return;
            if(playerController != null && playerController.IsHoldingHopball)
                return; // Prevent shooting while holding hopball

            var fireMode = WeaponManager?.GetWeaponDataByIndex(WeaponManager.CurrentWeaponIndex)?.fireMode;
            if(CurrentWeapon && fireMode == "Semi") {
                CurrentWeapon.Shoot();
            }
        }

        [UsedImplicitly]
        private void OnZoom(InputValue value) {
            if(!IsOwner || IsPausedOrDead) return;
            
            if(!value.isPressed) return;

            var weaponData = WeaponManager?.GetWeaponDataByIndex(WeaponManager.CurrentWeaponIndex);
            if(weaponData == null || !weaponData.useSniperOverlay) {
                if(IsSniperOverlayActive) {
                    IsSniperOverlayActive = false;
                }

                SniperOverlayManager.Instance?.ToggleSniperOverlay(false);

                return;
            }

            IsSniperOverlayActive = !IsSniperOverlayActive;
            SniperOverlayManager.Instance?.ToggleSniperOverlay(IsSniperOverlayActive);
            ApplySniperOverlayEffects(IsSniperOverlayActive, playZoomSound: true);
        }

        [UsedImplicitly]
        private void OnGrapple(InputValue value) {
            if(!IsOwner || IsPreMatchOrPausedOrDead || (MantleController?.IsMantling ?? false) ||
               GameMenuManager.Instance.IsPostMatch) return;

            if(GrappleController != null && GrappleController.IsGrappling) {
                GrappleController.CancelGrapple();
            } else {
                GrappleController?.TryGrapple();
            }
        }

        private void OnSwing() {
            // TODO: fix hold input
        }

        #endregion

        #region Weapons

        [UsedImplicitly]
        private void OnPrimary(InputValue _) {
            if(!IsOwner || IsPausedOrDead || (MantleController?.IsMantling ?? false)) return;

            SwitchWeapon(0);
        }

        [UsedImplicitly]
        private void OnSecondary(InputValue _) {
            if(!IsOwner || IsPausedOrDead || (MantleController?.IsMantling ?? false)) return;

            SwitchWeapon(1);
        }

        private void OnTertiary(InputValue _) {
            if(!IsOwner || IsPausedOrDead || (MantleController?.IsMantling ?? false)) return;

            //SwitchWeapon(2);
        }

        [UsedImplicitly]
        private void OnNextWeapon(InputValue _) {
            if(!IsOwner || IsPausedOrDead || (MantleController?.IsMantling ?? false)) return;

            if(WeaponManager == null) return;
            SwitchWeapon((WeaponManager.CurrentWeaponIndex + 1) % WeaponManager.WeaponCount);
        }
        
        [UsedImplicitly]
        private void OnPreviousWeapon(InputValue _) {
            if(!IsOwner || IsPausedOrDead || (MantleController?.IsMantling ?? false)) return;

            if(WeaponManager == null) return;
            SwitchWeapon((WeaponManager.CurrentWeaponIndex - 1 + WeaponManager.WeaponCount) %
                         WeaponManager.WeaponCount);
        }

        public void SwitchWeapon(int weaponIndex) {
            if(WeaponManager == null || !CurrentWeapon) return;
            // Allow switching even during pull out (interruptible switching)

            ForceDisableSniperOverlay(false);
            
            // If holding hopball, drop it first (WeaponManager will handle this, but we can also do it here for clarity)
            // Actually, WeaponManager.SwitchWeapon() will handle dropping, so we just proceed
            // Reload cancellation is handled by Weapon.SwitchToWeapon() when the weapon switch completes
            WeaponManager.SwitchWeapon(weaponIndex);
            RefreshSniperOverlayState();
        }

        [UsedImplicitly]
        private void OnReload(InputValue _) {
            if(!IsOwner || IsPreMatchOrPausedOrDead || !CurrentWeapon ||
               (MantleController?.IsMantling ?? false)) return;
            if(playerController != null && playerController.IsHoldingHopball)
                return; // Prevent reloading while holding hopball

            CurrentWeapon.StartReload();
        }

        #endregion

        #region System

        [UsedImplicitly]
        private void OnPause(InputValue _) {
            if(!IsOwner) return;
            GameMenuManager.Instance.TogglePause();
        }

        [UsedImplicitly]
        private void OnInteract(InputValue _) {
            if(!IsOwner || IsPausedOrDead || (MantleController?.IsMantling ?? false)) return;
            
            playerController.PickupHopball();
        }

        private void RefreshSniperOverlayState() {
            var weaponData = WeaponManager?.GetWeaponDataByIndex(WeaponManager.CurrentWeaponIndex);
            var canUseOverlay = weaponData != null && weaponData.useSniperOverlay;

            if(!canUseOverlay) {
                if(IsSniperOverlayActive) {
                    IsSniperOverlayActive = false;
                }

                SniperOverlayManager.Instance?.ToggleSniperOverlay(false);
                ApplySniperOverlayEffects(false, playZoomSound: false);
                UpdateSniperSensitivityMultiplier();
                return;
            }

            SniperOverlayManager.Instance?.ToggleSniperOverlay(IsSniperOverlayActive);
            ApplySniperOverlayEffects(IsSniperOverlayActive, playZoomSound: false);
            UpdateSniperSensitivityMultiplier();
        }

        private Vector3? _cachedFpWeaponPosition;
        private Vector3? _cachedFpWeaponRotation;
        [SerializeField] private Vector3 sniperScopedWeaponPosition = new Vector3(0f, -0.05f, 0.15f);
        [SerializeField] private Vector3 sniperScopedWeaponRotation = Vector3.zero;
        [SerializeField] private Vector3 sniperMuzzleCameraOffset = new Vector3(0f, -0.05f, 0.15f);
        private float _sniperSensitivityMultiplier = 1f;

        public Vector3 SniperMuzzleCameraOffset => sniperMuzzleCameraOffset;

        private void ApplySniperOverlayEffects(bool zoomEnabled, bool playZoomSound) {
            if(WeaponManager != null) {
                WeaponManager.SetCurrentFpWeaponVisible(!zoomEnabled);

                var fpWeapon = WeaponManager.GetCurrentFpWeapon();
                if(fpWeapon != null) {
                    if(zoomEnabled) {
                        if(_cachedFpWeaponPosition == null)
                            _cachedFpWeaponPosition = fpWeapon.transform.localPosition;
                        if(_cachedFpWeaponRotation == null)
                            _cachedFpWeaponRotation = fpWeapon.transform.localEulerAngles;

                        WeaponManager.OffsetCurrentFpWeapon(sniperScopedWeaponPosition, sniperScopedWeaponRotation);
                    } else {
                        if(_cachedFpWeaponPosition.HasValue) {
                            WeaponManager.OffsetCurrentFpWeapon(_cachedFpWeaponPosition.Value,
                                _cachedFpWeaponRotation ?? Vector3.zero);
                        }

                        _cachedFpWeaponPosition = null;
                        _cachedFpWeaponRotation = null;
                    }
                }
            }

            var lookController = playerController?.LookController;
            if(lookController != null && lookController.IsSniperZoomActive != zoomEnabled) {
                lookController.SetSniperZoomActive(zoomEnabled, sniperZoomFov);
            }
            if(playZoomSound) {
                SoundFXManager.Instance?.PlayUISound(SfxKey.SniperZoom);
            }
            UpdateSniperSensitivityMultiplier();
        }

        private void UpdateSniperSensitivityMultiplier() {
            if(_defaultFpFov <= 0f) return;
            _sniperSensitivityMultiplier = Mathf.Clamp(sniperZoomFov / _defaultFpFov, 0.01f, 1f);
        }

        public void ForceDisableSniperOverlay(bool playZoomSound) {
            if(!IsSniperOverlayActive) {
                SniperOverlayManager.Instance?.ToggleSniperOverlay(false);
                return;
            }

            IsSniperOverlayActive = false;
            SniperOverlayManager.Instance?.ToggleSniperOverlay(false);
            ApplySniperOverlayEffects(false, playZoomSound);
        }

        #endregion
    }
}