using Game.Audio;
using Game.Menu;
using Game.UI;
using Game.Weapons;
using JetBrains.Annotations;
using Network.Events;
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

        [Header("Input Settings")]
        [SerializeField] private bool toggleSprint = true;

        [SerializeField] private bool toggleCrouch = true;
        
        [Header("Bot Control")]
        /// <summary>
        /// When true, disables Unity Input System reading and allows external control (for AI bots).
        /// </summary>
        public bool IsBot { get; set; }

        #endregion

        private static bool IsPaused => GameMenuManager.Instance != null && GameMenuManager.Instance.IsPaused;

        private bool IsPausedOrDead {
            get {
                if(GameMenuManager.Instance != null && playerController != null) {
                    return GameMenuManager.Instance.IsPaused || playerController.IsDead;
                }

                return false;
            }
        }

        private static bool IsPreMatch => GameMenuManager.Instance != null && GameMenuManager.IsPreMatch;
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


        private MantleController MantleController {
            get {
                if(_mantleController == null && playerController != null) {
                    _mantleController = playerController.MantleController;
                }

                return _mantleController;
            }
        }

        private Weapon CurrentWeapon {
            get {
                if(WeaponManager == null) return null;
                return WeaponManager.CurrentWeapon;
            }
        }

        private bool _sprintBtnDown;
        private bool _crouchBtnDown;
        public bool IsSniperOverlayActive { get; private set; }

        [SerializeField] private float sniperZoomFov = 20f;
        private float _defaultFpFov = -1f;
        
        /// <summary>
        /// Gets whether the jump action is currently being held (for bot recording).
        /// Respects custom keybinds including scroll wheel.
        /// </summary>
        public bool IsJumpHeld {
            get {
                if(_playerInputComponent == null) return false;
                var playerMap = _playerInputComponent.actions.FindActionMap("Player");
                var jumpAction = playerMap?.FindAction("Jump");
                return jumpAction != null && jumpAction.IsPressed();
            }
        }

        /// <summary>
        /// Gets whether the grapple action is currently being held (for bot recording).
        /// </summary>
        public bool IsGrappleHeld {
            get {
                if(_playerInputComponent == null) return false;
                var playerMap = _playerInputComponent.actions.FindActionMap("Player");
                var grappleAction = playerMap?.FindAction("Grapple");
                return grappleAction != null && grappleAction.IsPressed();
            }
        }

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

            if(WeaponManager != null)
                WeaponManager.InitializeWeapons();

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
            if(SniperOverlayManager.Instance == null) return;
            SniperOverlayManager.Instance.ToggleSniperOverlay(false);
            ApplySniperOverlayEffects(false, playZoomSound: false);
        }

        private void Start() {
            if(!IsOwner) return;

            Cursor.visible = false;
            Cursor.lockState = CursorLockMode.Locked;
        }

        // Direct Input System polling for certain actions
        private void LateUpdate() {
            if(IsBot) return;
            if(!IsOwner || !CurrentWeapon || WeaponManager == null) return;

            var weaponData = WeaponManager.GetWeaponDataByIndex(WeaponManager.CurrentWeaponIndex);
            var fireMode = weaponData.fireMode;

            // Component reference should be assigned in the inspector
            if(_playerInputComponent == null) _playerInputComponent = GetComponent<UnityEngine.InputSystem.PlayerInput>();
            if(_playerInputComponent == null) return;
            
            var playerMap = _playerInputComponent.actions.FindActionMap("Player");
            var attackAction = playerMap != null ? playerMap.FindAction("Attack") : null;
            var jumpAction = playerMap != null ? playerMap.FindAction("Jump") : null;

            if(!IsPreMatchOrPausedOrDead && fireMode == "Full" && attackAction != null && attackAction.IsPressed() &&
               !(MantleController != null && MantleController.IsMantling) &&
               !(playerController != null && playerController.IsHoldingHopball)) {
                CurrentWeapon.Shoot();
            }

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

            if(!IsPreMatchOrPausedOrDead && (jumpPressed || scrollPressed) && (MantleController != null && MantleController.CanJump)) {
                // Check if hold-to-mantle is enabled
                var holdMantleEnabled = PlayerPrefs.GetInt("HoldMantle", 1) == 1;

                // Prioritize Wall Jump over Mantle
                var isWallRunning = playerController.WallRunController != null && playerController.WallRunController.IsWallRunning;

                switch(isWallRunning) {
                    // Prevent "Auto-Hop" (holding jump) from unintentionally triggering wall jumps.
                    // If wall running, require a fresh jump press (triggered) or scroll wheel input.
                    case true when !scrollPressed && !jumpAction.triggered && !jumpAction.triggered:
                        // Jump is held, but not fresh press - ignore for wall jumping
                        return;
                    // Try mantle if enabled and not grounded (and not wall running)
                    case false when MantleController != null && holdMantleEnabled && !playerController.IsGrounded: {
                        MantleController.TryMantle();

                        // If we started mantling, don't jump
                        if(MantleController != null && MantleController.IsMantling) {
                            return;
                        }

                        break;
                    }
                }

                // Always allow hold-to-jump (for scroll wheel support)
                playerController.TryJump();
                
                if(GrappleController != null)
                    GrappleController.CancelGrapple();
            }

            CurrentWeapon.UpdateDamageMultiplier();

            if(weaponData) {
                EventBus.Publish(new UpdateMultiplierEvent(CurrentWeapon.CurrentDamageMultiplier,
                    weaponData.maxDamageMultiplier));
            }

            if(!IsPaused && Keyboard.current.tabKey.isPressed) {
                if(ScoreboardManager.Instance != null) {
                    EventBus.Publish(new ShowScoreboardEvent());
                }
            } else if(ScoreboardManager.Instance != null && ScoreboardManager.Instance.IsScoreboardVisible) {
                EventBus.Publish(new HideScoreboardEvent());
            }

        }

        #endregion

        #region Bot Control Interface

        /// <summary>
        /// Sets movement input externally (for bots). Bypasses Unity Input System.
        /// </summary>
        public void SetMovementInput(Vector2 move) {
            if(!IsOwner) return; // Only owner can set input (prevents network interference)
            if(playerController != null) {
                playerController.moveInput = move;
            }
        }

        /// <summary>
        /// Sets look input externally (for bots). Bypasses Unity Input System.
        /// </summary>
        public void SetLookInput(Vector2 look) {
            if(!IsOwner) return; // Only owner can set input (prevents network interference)
            if(playerController != null) {
                playerController.lookInput = look;
            }
        }

        /// <summary>
        /// Triggers jump action externally (for bots).
        /// </summary>
        public void TriggerJump() {
            if(!IsOwner || IsPreMatchOrPausedOrDead) return;
            var isMantling = MantleController != null && MantleController.IsMantling;
            if(isMantling) return;

            if(!playerController.IsGrounded && MantleController != null) {
                MantleController.TryMantle();
                if(MantleController.IsMantling) return;
            }

            playerController.TryJump();
            
            if(GrappleController != null && GrappleController.IsGrappling) {
                GrappleController.CancelGrapple();
            }
        }

        /// <summary>
        /// Triggers grapple action externally (for bots).
        /// </summary>
        public void TriggerGrapple() {
            if(!IsOwner || IsPreMatchOrPausedOrDead) return;
            var isMantling = MantleController != null && MantleController.IsMantling;
            if(isMantling) return;

            if(GrappleController != null) {
                if(GrappleController.IsGrappling) {
                    GrappleController.CancelGrapple();
                } else {
                    GrappleController.TryGrapple();
                }
            }
        }

        /// <summary>
        /// Triggers shoot action externally (for bots).
        /// </summary>
        public void TriggerShoot() {
            if(!IsOwner || IsPreMatchOrPausedOrDead) return;
            var isMantling = MantleController != null && MantleController.IsMantling;
            if(isMantling) return;
            if(playerController != null && playerController.IsHoldingHopball) return;

            if(CurrentWeapon != null) {
                CurrentWeapon.Shoot();
            }
        }

        /// <summary>
        /// Sets sprint state externally (for bots).
        /// </summary>
        public void SetSprintInput(bool sprint) {
            if(!IsOwner) return; // Only owner can set input (prevents network interference)
            if(playerController != null) {
                playerController.sprintInput = sprint;
            }
        }

        /// <summary>
        /// Sets crouch state externally (for bots).
        /// </summary>
        public void SetCrouchInput(bool crouch) {
            if(!IsOwner) return; // Only owner can set input (prevents network interference)
            if(playerController != null) {
                playerController.crouchInput = crouch;
            }
        }

        #endregion

        #region Movement

        [UsedImplicitly]
        private void OnLook(InputValue value) {
            if(IsBot) return;
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
            if(IsBot) return;
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
            if(IsBot) return;
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
            if(IsBot) return;
            if(!IsOwner) return;
            var isMantling = MantleController != null && MantleController.IsMantling;
            if(IsPausedOrDead || isMantling) {
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
            if(IsBot) return;
            if(!IsOwner || IsPausedOrDead) return;
            var isMantling = MantleController != null && MantleController.IsMantling;
            if(isMantling) return;

            if(!playerController.IsGrounded) {
                // Prioritize Wall Jump over Mantle
                if (playerController.WallRunController != null && playerController.WallRunController.IsWallRunning) {
                    playerController.TryJump();
                    return;
                }

                if(MantleController != null) {
                    MantleController.TryMantle();
                }

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
            if(!IsOwner || IsPreMatchOrPausedOrDead) return;
            var isMantling = MantleController != null && MantleController.IsMantling;
            if(isMantling) return;

            playerController.TryJump();

            if(GrappleController != null && GrappleController.IsGrappling) {
                GrappleController.CancelGrapple();
            }
        }

        [UsedImplicitly]
        private void OnAttack(InputValue value) {
            if(IsBot) return;
            if(!IsOwner || IsPreMatchOrPausedOrDead) return;
            var isMantling = MantleController != null && MantleController.IsMantling;
            if(isMantling) return;
            if(playerController != null && playerController.IsHoldingHopball)
                return; // Prevent shooting while holding hopball

            if(WeaponManager == null) return;
            var weaponData = WeaponManager.GetWeaponDataByIndex(WeaponManager.CurrentWeaponIndex);
            var fireMode = weaponData != null ? weaponData.fireMode : null;
            if(CurrentWeapon != null && fireMode == "Semi") {
                CurrentWeapon.Shoot();
            }
        }

        [UsedImplicitly]
        private void OnZoom(InputValue value) {
            if(IsBot) return;
            if(!IsOwner || IsPausedOrDead) return;
            
            if(!value.isPressed) return;

            if(WeaponManager == null) return;
            var weaponData = WeaponManager.GetWeaponDataByIndex(WeaponManager.CurrentWeaponIndex);
            if(weaponData == null || !weaponData.useSniperOverlay) {
                if(IsSniperOverlayActive) {
                    IsSniperOverlayActive = false;
                }

                if(SniperOverlayManager.Instance != null) {
                    SniperOverlayManager.Instance.ToggleSniperOverlay(false);
                }

                return;
            }

            IsSniperOverlayActive = !IsSniperOverlayActive;
            if(SniperOverlayManager.Instance != null) {
                SniperOverlayManager.Instance.ToggleSniperOverlay(IsSniperOverlayActive);
            }
            ApplySniperOverlayEffects(IsSniperOverlayActive, playZoomSound: true);
        }

        [UsedImplicitly]
        private void OnGrapple(InputValue value) {
            if(IsBot) return;
            if(!IsOwner || IsPreMatchOrPausedOrDead || GameMenuManager.Instance.IsPostMatch) return;
            var isMantling = MantleController != null && MantleController.IsMantling;
            if(isMantling) return;

            if(GrappleController != null && GrappleController.IsGrappling) {
                GrappleController.CancelGrapple();
            } else {
                if(GrappleController != null) {
                    GrappleController.TryGrapple();
                }
            }
        }


        #endregion

        #region Weapons

        [UsedImplicitly]
        private void OnPrimary(InputValue _) {
            if(IsBot) return;
            if(!IsOwner || IsPausedOrDead) return;
            var isMantling = MantleController != null && MantleController.IsMantling;
            if(isMantling) return;

            SwitchWeapon(0);
        }

        [UsedImplicitly]
        private void OnSecondary(InputValue _) {
            if(IsBot) return;
            if(!IsOwner || IsPausedOrDead) return;
            var isMantling = MantleController != null && MantleController.IsMantling;
            if(isMantling) return;

            SwitchWeapon(1);
        }

        private void OnTertiary(InputValue _) {
            if(IsBot) return;
            if(!IsOwner || IsPausedOrDead) return;
            var isMantling = MantleController != null && MantleController.IsMantling;
            if(isMantling) return;

            //SwitchWeapon(2);
        }

        [UsedImplicitly]
        private void OnNextWeapon(InputValue _) {
            if(IsBot) return;
            if(!IsOwner || IsPausedOrDead) return;
            var isMantling = MantleController != null && MantleController.IsMantling;
            if(isMantling) return;

            if(WeaponManager == null) return;
            SwitchWeapon((WeaponManager.CurrentWeaponIndex + 1) % WeaponManager.WeaponCount);
        }
        
        [UsedImplicitly]
        private void OnPreviousWeapon(InputValue _) {
            if(IsBot) return;
            if(!IsOwner || IsPausedOrDead) return;
            var isMantling = MantleController != null && MantleController.IsMantling;
            if(isMantling) return;

            if(WeaponManager == null) return;
            SwitchWeapon((WeaponManager.CurrentWeaponIndex - 1 + WeaponManager.WeaponCount) %
                         WeaponManager.WeaponCount);
        }

        /// <summary>
        /// Switches the current weapon to the specified index.
        /// </summary>
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
            if(IsBot) return;
            if(!IsOwner || IsPreMatchOrPausedOrDead || !CurrentWeapon) return;
            var isMantling = MantleController != null && MantleController.IsMantling;
            if(isMantling) return;
            if(playerController != null && playerController.IsHoldingHopball)
                return; // Prevent reloading while holding hopball

            CurrentWeapon.StartReload();
        }

        #endregion

        #region System

        [UsedImplicitly]
        private void OnPause(InputValue _) {
            if(IsBot) return;
            if(!IsOwner) return;
            GameMenuManager.Instance.TogglePause();
        }

        [UsedImplicitly]
        private void OnInteract(InputValue _) {
            if(IsBot) return;
            if(!IsOwner || IsPausedOrDead) return;
            var isMantling = MantleController != null && MantleController.IsMantling;
            if(isMantling) return;
            
            playerController.PickupHopball();
        }

        private void RefreshSniperOverlayState() {
            if(WeaponManager == null) return;
            var weaponData = WeaponManager.GetWeaponDataByIndex(WeaponManager.CurrentWeaponIndex);
            var canUseOverlay = weaponData != null && weaponData.useSniperOverlay;

            if(!canUseOverlay) {
                if(IsSniperOverlayActive) {
                    IsSniperOverlayActive = false;
                }

                if(SniperOverlayManager.Instance != null) {
                    SniperOverlayManager.Instance.ToggleSniperOverlay(false);
                }
                ApplySniperOverlayEffects(false, playZoomSound: false);
                UpdateSniperSensitivityMultiplier();
                return;
            }

            if(SniperOverlayManager.Instance != null) {
                SniperOverlayManager.Instance.ToggleSniperOverlay(IsSniperOverlayActive);
            }
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
                            var rotation = _cachedFpWeaponRotation.HasValue ? _cachedFpWeaponRotation.Value : Vector3.zero;
                            WeaponManager.OffsetCurrentFpWeapon(_cachedFpWeaponPosition.Value, rotation);
                        }

                        _cachedFpWeaponPosition = null;
                        _cachedFpWeaponRotation = null;
                    }
                }
            }

            if(playerController != null) {
                var lookController = playerController.LookController;
                if(lookController != null && lookController.IsSniperZoomActive != zoomEnabled) {
                    lookController.SetSniperZoomActive(zoomEnabled, sniperZoomFov);
                }
            }
            if(playZoomSound) {
                EventBus.Publish(new PlayUISoundEvent(SfxKey.SniperZoom));
            }
            UpdateSniperSensitivityMultiplier();
        }

        private void UpdateSniperSensitivityMultiplier() {
            if(_defaultFpFov <= 0f) return;
            _sniperSensitivityMultiplier = Mathf.Clamp(sniperZoomFov / _defaultFpFov, 0.01f, 1f);
        }

        /// <summary>
        /// Disables the sniper overlay and restores weapon visuals.
        /// </summary>
        public void ForceDisableSniperOverlay(bool playZoomSound) {
            if(!IsSniperOverlayActive) {
                if(SniperOverlayManager.Instance != null) {
                    SniperOverlayManager.Instance.ToggleSniperOverlay(false);
                }
                return;
            }

            IsSniperOverlayActive = false;
            if(SniperOverlayManager.Instance != null) {
                SniperOverlayManager.Instance.ToggleSniperOverlay(false);
            }
            ApplySniperOverlayEffects(false, playZoomSound);
        }

        #endregion
    }
}