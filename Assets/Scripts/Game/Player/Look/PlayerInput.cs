using System.Collections;
using Diagnostics;
using Events;
using Game.Match;
using Game.Menu;
using Game.Player.Core;
using Game.Player.Movement;
using Game.Settings;
using Game.Social;
using Game.UI.HUD;
using Game.UI.Screens.Scoreboard;
using Game.Weapon.Core;
using Game.Weapon.Manager;
using JetBrains.Annotations;
using Network.Singletons;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Player.Look {
    public class PlayerInput : NetworkBehaviour {
        #region Serialized Fields

        [Header("Components")]
        [SerializeField] private PlayerController playerController;

        private UnityEngine.InputSystem.PlayerInput _playerInputComponent;
        private InputActionMap _playerActionMap;
        private InputAction _moveAction;
        private InputAction _attackAction;
        private InputAction _jumpAction;
        private InputAction _voiceAction;
        private InputAction _grappleAction;

        private CinemachineCamera _fpCamera;
        private AudioListener _audioListener;
        private WeaponManager _weaponManager;
        private GrappleController _grappleController;
        private SwingGrapple _swingGrapple;
        private MantleController _mantleController;

        [Header("Input Settings")]
        [SerializeField] private bool toggleSprint = true;

        [SerializeField] private bool toggleCrouch = true;

        #endregion

        private static bool IsPaused => GameMenuManager.Instance != null && GameMenuManager.Instance.IsPaused;

        private bool IsPausedOrDead {
            get {
                if(GameMenuManager.Instance == null) return playerController != null && playerController.IsDead;
                if (GameMenuManager.Instance.IsChatOpen) return true;
                if (GameMenuManager.Instance.IsPaused) return true;

                return playerController != null && playerController.IsDead;
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

        private Weapon.Core.Weapon CurrentWeapon => WeaponManager == null ? null : WeaponManager.CurrentWeapon;

        private bool _sprintBtnDown;
        private bool _crouchBtnDown;
        private bool _voiceBtnDown;
        private bool _attackBtnDown;
        private bool _lastHopballPromptVisible;
        private string _lastHopballPromptText = "";
        private HUDManager _lastHudManager;
        private Coroutine _deferredAmmoHudRefreshRoutine;
        private int _queuedWeaponCycleOffset;
        private bool _jumpScrollUpBound;
        private bool _jumpScrollDownBound;
        private bool _nextWeaponScrollUpBound;
        private bool _nextWeaponScrollDownBound;
        private bool _previousWeaponScrollUpBound;
        private bool _previousWeaponScrollDownBound;
        public bool IsSniperOverlayActive { get; private set; }

        [SerializeField] private float sniperZoomFov = 20f;
        private float _defaultFpFov = -1f;
        
        /// <summary>
        /// Gets whether the jump action is currently being held (for bot recording).
        /// Respects custom keybinds including scroll wheel.
        /// </summary>
        public bool IsJumpHeld {
            get {
                if(_jumpAction == null) {
                    RefreshCachedInputActions();
                }

                return _jumpAction != null && _jumpAction.IsPressed();
            }
        }

        /// <summary>
        /// Gets whether the grapple action is currently being held (for bot recording).
        /// </summary>
        public bool IsGrappleHeld {
            get {
                if(_grappleAction == null) {
                    RefreshCachedInputActions();
                }

                return _grappleAction != null && _grappleAction.IsPressed();
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

            RefreshCachedInputActions();
            RefreshCachedScrollBindings();
        }

        private void OnEnable() {
            EventBus.Unsubscribe<BindingsAppliedEvent>(OnBindingsApplied);
            EventBus.Subscribe<BindingsAppliedEvent>(OnBindingsApplied);
            RefreshCachedScrollBindings();
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            RefreshCachedInputActions();
            RefreshCachedScrollBindings();

            if(WeaponManager != null)
                WeaponManager.InitializeWeapons();

            if(IsOwner && WeaponManager != null) {
                WeaponManager.RefreshAmmoHud();
                if(_deferredAmmoHudRefreshRoutine != null) {
                    StopCoroutine(_deferredAmmoHudRefreshRoutine);
                }
                _deferredAmmoHudRefreshRoutine = StartCoroutine(RefreshOwnerAmmoHudDeferred());
            }

            if(!IsOwner) {
                _fpCamera.gameObject.SetActive(false);
                _audioListener.enabled = false;

                if(_playerInputComponent != null) {
                    _playerInputComponent.enabled = false;
                }
                FlowLog.Emit(FlowEventIds.PlayerControlState,
                    ("player", OwnerClientId),
                    ("enabled", false),
                    ("reason", "RemotePlayerInputDisabled"));
            } else {
                if(_playerInputComponent != null) {
                    _playerInputComponent.enabled = true;
                }
                FlowLog.Emit(FlowEventIds.PlayerControlState,
                    ("player", OwnerClientId),
                    ("enabled", true),
                    ("reason", "OwnerPlayerInputEnabled"));

                RefreshSniperOverlayState();
            }
        }

        private void OnDisable() {
            this.UnsubscribeFromEventBus();
            _queuedWeaponCycleOffset = 0;
            if(_deferredAmmoHudRefreshRoutine != null) {
                StopCoroutine(_deferredAmmoHudRefreshRoutine);
                _deferredAmmoHudRefreshRoutine = null;
            }

            if(!IsOwner) return;
            FlowLog.Emit(FlowEventIds.PlayerControlState,
                ("player", OwnerClientId),
                ("enabled", false),
                ("reason", "PlayerInputComponentDisabled"));
            IsSniperOverlayActive = false;
            if(SniperOverlayManager.Instance != null) {
                SniperOverlayManager.Instance.ToggleSniperOverlay(false);
                ApplySniperOverlayEffects(false, playZoomSound: false);
            }

            ApplyHopballInteractPrompt(false, "PRESS INTERACT");
        }

        private IEnumerator RefreshOwnerAmmoHudDeferred() {
            yield return null;
            if(IsOwner && WeaponManager != null) {
                WeaponManager.RefreshAmmoHud();
            }

            yield return new WaitForSeconds(0.05f);
            if(IsOwner && WeaponManager != null) {
                WeaponManager.RefreshAmmoHud();
            }

            _deferredAmmoHudRefreshRoutine = null;
        }

        private void Start() {
            if(!IsOwner) return;

            Cursor.visible = false;
            Cursor.lockState = CursorLockMode.Locked;
        }

        private void Update() {
            if(!IsOwner) return;
            if(_queuedWeaponCycleOffset == 0) return;

            var offset = _queuedWeaponCycleOffset;
            _queuedWeaponCycleOffset = 0;
            TryCycleWeaponByOffset(offset);
        }

        // Direct Input System polling for certain actions
        private void LateUpdate() {
            if(!IsOwner) return;

            UpdateHopballInteractPrompt();
            if(!CurrentWeapon || WeaponManager == null) return;

            var weaponData = WeaponManager.GetWeaponDataByIndex(WeaponManager.CurrentWeaponIndex);
            var fireMode = weaponData.fireModeType;

            if(_attackAction == null || _jumpAction == null || _voiceAction == null) {
                RefreshCachedInputActions();
            }
            if(_jumpAction == null) return;

            if (VoiceManager.Instance != null && _voiceAction != null) {
                var isPressed = _voiceAction.IsPressed();
                var isChatOpen = GameMenuManager.Instance != null && GameMenuManager.Instance.IsChatOpen;
                VoiceManager.Instance.SetPttActive(isPressed && !isChatOpen);

                _voiceBtnDown = isPressed;
            }

            var attackPressed = _attackAction != null && _attackAction.IsPressed();
            var attackPressedThisFrame = attackPressed && _attackBtnDown == false;
            _attackBtnDown = attackPressed;

            if(!IsPreMatchOrPausedOrDead && fireMode == WeaponData.FireModeType.Full && attackPressed &&
               !(MantleController != null && MantleController.IsMantling) &&
               !(playerController != null && playerController.IsHoldingHopball)) {
                if(attackPressedThisFrame) {
                    if(CurrentWeapon.TryAutoReloadFromEmptyClick()) {
                        return;
                    }
                }
                CurrentWeapon.Shoot();
            }

            var jumpPressed = _jumpAction.IsPressed();
            var scrollPressed = false;

            if(Mouse.current != null && Mouse.current.scroll.value.magnitude > 0f) {
                var scrollY = Mouse.current.scroll.value.y;
                var nextWeaponScrollPressed =
                    IsScrollBindingTriggered(_nextWeaponScrollUpBound, _nextWeaponScrollDownBound, scrollY);
                var previousWeaponScrollPressed =
                    IsScrollBindingTriggered(_previousWeaponScrollUpBound, _previousWeaponScrollDownBound, scrollY);

                if(nextWeaponScrollPressed && !previousWeaponScrollPressed) {
                    QueueWeaponCycleFromScroll(1);
                } else if(previousWeaponScrollPressed && !nextWeaponScrollPressed) {
                    QueueWeaponCycleFromScroll(-1);
                }

                // If scroll was consumed for weapon cycling, do not also treat it as jump input.
                if(!nextWeaponScrollPressed && !previousWeaponScrollPressed) {
                    scrollPressed = IsScrollBindingTriggered(_jumpScrollUpBound, _jumpScrollDownBound, scrollY);
                }
            }

            if(!IsPausedOrDead && (jumpPressed || scrollPressed) && MantleController != null && MantleController.CanJump) {
                // Check if hold-to-mantle is enabled
                var controls = GameSettings.Data.controls;
                var holdMantleEnabled = controls == null || controls.holdMantle;

                // Prioritize Wall Jump over Mantle
                var isWallRunning = playerController.WallRunController != null && playerController.WallRunController.IsWallRunning;

                switch(isWallRunning) {
                    // Prevent "Auto-Hop" (holding jump) from unintentionally triggering wall jumps.
                    // If wall running, require a fresh jump press (triggered) or scroll wheel input.
                    case true when !scrollPressed && !_jumpAction.triggered:
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

            if(!IsPaused && Keyboard.current.tabKey.isPressed) {
                if(ScoreboardManager.Instance != null) {
                    EventBus.Publish(new ShowScoreboardEvent());
                }
            } else if(ScoreboardManager.Instance != null && ScoreboardManager.Instance.IsScoreboardVisible) {
                EventBus.Publish(new HideScoreboardEvent());
            }
            
            // Handle right-click to unlock mouse when scoreboard is open
            if(ScoreboardManager.Instance == null || !ScoreboardManager.Instance.IsScoreboardVisible) return;
            if(Mouse.current == null || !Mouse.current.rightButton.wasPressedThisFrame ||
               Cursor.lockState != CursorLockMode.Locked) return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            if(PlayerController.LocalPlayer != null) {
                PlayerController.LocalPlayer.LockLook = true;
            }

        }

        private void UpdateHopballInteractPrompt() {
            var canShowPrompt = false;
            var promptText = "PRESS INTERACT";

            var canCheckPickup = !IsPausedOrDead && !IsPreMatch &&
                                 playerController != null &&
                                 playerController.PlayerHopballController != null &&
                                 !playerController.IsHoldingHopball;
            if(canCheckPickup) {
                canShowPrompt = playerController.PlayerHopballController.CanPickupNearbyHopball();
                if(canShowPrompt) {
                    promptText = BuildInteractPromptText();
                }
            }

            ApplyHopballInteractPrompt(canShowPrompt, promptText);
        }

        private void ApplyHopballInteractPrompt(bool visible, string text) {
            var hud = HUDManager.Instance;
            var shouldApply = hud != _lastHudManager || visible != _lastHopballPromptVisible ||
                              !string.Equals(text, _lastHopballPromptText);
            if(!shouldApply) return;

            if(hud != null) hud.SetHopballInteractPrompt(visible, text);

            _lastHudManager = hud;
            _lastHopballPromptVisible = visible;
            _lastHopballPromptText = text;
        }

        private static string BuildInteractPromptText() {
            var binding = GetPrimaryInteractBindingName();
            return $"PRESS {binding}";
        }

        private static string GetPrimaryInteractBindingName() {
            var binding = KeybindManager.GetBindingDisplayString("interact", 0);
            if(string.IsNullOrWhiteSpace(binding) || string.Equals(binding, "None", System.StringComparison.OrdinalIgnoreCase)) {
                binding = KeybindManager.GetBindingDisplayString("interact", 1);
            }

            return string.IsNullOrWhiteSpace(binding) || string.Equals(binding, "None", System.StringComparison.OrdinalIgnoreCase)
                ? "INTERACT"
                : binding.ToUpperInvariant();
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
            if(!IsOwner || IsPausedOrDead) return;
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

            if(GrappleController == null) return;
            if(GrappleController.IsGrappling) {
                GrappleController.CancelGrapple();
            } else {
                GrappleController.TryGrapple();
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

            if(CurrentWeapon == null) return;
            if(CurrentWeapon.TryAutoReloadFromEmptyClick()) return;
            CurrentWeapon.Shoot();
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

        /// <summary>
        /// Reapplies current held movement action state directly from the input action map.
        /// Used after control restore where no new OnMove callback may fire if the key was already held.
        /// </summary>
        public Vector2 ResampleHeldMovementInput(string reason = "Unknown") {
            if(!IsOwner || playerController == null) return Vector2.zero;
            if(_moveAction == null) {
                RefreshCachedInputActions();
            }
            if(_moveAction == null) return Vector2.zero;

            var move = _moveAction.ReadValue<Vector2>();
            playerController.moveInput = move;

            FlowLog.Emit(FlowEventIds.PlayerControlState,
                ("player", OwnerClientId),
                ("enabled", true),
                ("reason", reason),
                ("sampledMove", move));
            return move;
        }

        #endregion

        #region Movement

        [UsedImplicitly]
        private void OnLook(InputValue value) {
            if(!IsOwner) return;
            if(IsPausedOrDead || playerController.LockLook) {
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
            if(IsPausedOrDead || PostMatchManager.IsPostMatchMovementLockedLocal) {
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
            if(!IsOwner || IsPreMatchOrPausedOrDead) return;
            var isMantling = MantleController != null && MantleController.IsMantling;
            if(isMantling) return;
            if(playerController != null && playerController.IsHoldingHopball)
                return; // Prevent shooting while holding hopball

            if(WeaponManager == null) return;
            var weaponData = WeaponManager.GetWeaponDataByIndex(WeaponManager.CurrentWeaponIndex);
            var fireMode = weaponData != null ? weaponData.fireModeType : WeaponData.FireModeType.Semi;
            if(CurrentWeapon == null || fireMode != WeaponData.FireModeType.Semi) return;
            if(CurrentWeapon.TryAutoReloadFromEmptyClick()) return;
            CurrentWeapon.Shoot();
        }

        [UsedImplicitly]
        private void OnZoom(InputValue value) {
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
            if(!IsOwner || IsPausedOrDead) return;
            var isMantling = MantleController != null && MantleController.IsMantling;
            if(isMantling) return;

            SwitchWeapon(0);
        }

        [UsedImplicitly]
        private void OnSecondary(InputValue _) {
            if(!IsOwner || IsPausedOrDead) return;
            var isMantling = MantleController != null && MantleController.IsMantling;
            if(isMantling) return;

            SwitchWeapon(1);
        }

        private void OnTertiary(InputValue _) {
            if(!IsOwner || IsPausedOrDead) return;
            var isMantling = MantleController != null && MantleController.IsMantling;
            if(isMantling) return;

            //SwitchWeapon(2);
        }

        [UsedImplicitly]
        private void OnNextWeapon(InputValue _) {
            TryCycleWeaponByOffset(1);
        }
        
        [UsedImplicitly]
        private void OnPreviousWeapon(InputValue _) {
            TryCycleWeaponByOffset(-1);
        }

        private void TryCycleWeaponByOffset(int offset) {
            if(offset == 0) return;
            if(!IsOwner || IsPausedOrDead) return;

            var isMantling = MantleController != null && MantleController.IsMantling;
            if(isMantling) return;
            if(WeaponManager == null) return;

            var weaponCount = WeaponManager.WeaponCount;
            if(weaponCount <= 1) return;

            var targetIndex = (WeaponManager.CurrentWeaponIndex + offset) % weaponCount;
            if(targetIndex < 0) {
                targetIndex += weaponCount;
            }

            SwitchWeapon(targetIndex);
        }

        private void QueueWeaponCycleFromScroll(int offset) {
            if(offset == 0) return;
            _queuedWeaponCycleOffset = offset;
        }

        private static bool IsScrollBindingTriggered(bool upBound, bool downBound, float scrollY) {
            if(Mathf.Abs(scrollY) <= Mathf.Epsilon) {
                return false;
            }

            return (upBound && scrollY > 0f) || (downBound && scrollY < 0f);
        }

        private void RefreshCachedInputActions() {
            if(_playerInputComponent == null) {
                _playerInputComponent = playerController != null
                    ? playerController.UnityPlayerInput
                    : null;
            }

            if(_playerInputComponent == null || _playerInputComponent.actions == null) {
                _playerActionMap = null;
                _moveAction = null;
                _attackAction = null;
                _jumpAction = null;
                _voiceAction = null;
                _grappleAction = null;
                return;
            }

            _playerActionMap = _playerInputComponent.actions.FindActionMap("Player");
            _moveAction = _playerActionMap?.FindAction("Move");
            _attackAction = _playerActionMap?.FindAction("Attack");
            _jumpAction = _playerActionMap?.FindAction("Jump");
            _voiceAction = _playerActionMap?.FindAction("Voice");
            _grappleAction = _playerActionMap?.FindAction("Grapple");
        }

        private void OnBindingsApplied(BindingsAppliedEvent _) {
            RefreshCachedScrollBindings();
        }

        private void RefreshCachedScrollBindings() {
            _jumpScrollUpBound = false;
            _jumpScrollDownBound = false;
            _nextWeaponScrollUpBound = false;
            _nextWeaponScrollDownBound = false;
            _previousWeaponScrollUpBound = false;
            _previousWeaponScrollDownBound = false;

            var binds = GameSettings.Data.keybinds;
            if(binds?.entries == null) {
                return;
            }

            foreach(var entry in binds.entries) {
                if(entry == null) continue;

                switch(entry.name) {
                    case "jump":
                        ApplyScrollBinding(entry.binding0, ref _jumpScrollUpBound, ref _jumpScrollDownBound);
                        ApplyScrollBinding(entry.binding1, ref _jumpScrollUpBound, ref _jumpScrollDownBound);
                        break;
                    case "nextweapon":
                        ApplyScrollBinding(entry.binding0, ref _nextWeaponScrollUpBound, ref _nextWeaponScrollDownBound);
                        ApplyScrollBinding(entry.binding1, ref _nextWeaponScrollUpBound, ref _nextWeaponScrollDownBound);
                        break;
                    case "previousweapon":
                        ApplyScrollBinding(entry.binding0, ref _previousWeaponScrollUpBound, ref _previousWeaponScrollDownBound);
                        ApplyScrollBinding(entry.binding1, ref _previousWeaponScrollUpBound, ref _previousWeaponScrollDownBound);
                        break;
                }
            }
        }

        private static void ApplyScrollBinding(string binding, ref bool upBound, ref bool downBound) {
            if(string.IsNullOrWhiteSpace(binding)) return;

            if(string.Equals(binding, "SCROLL_UP", System.StringComparison.OrdinalIgnoreCase)) {
                upBound = true;
            } else if(string.Equals(binding, "SCROLL_DOWN", System.StringComparison.OrdinalIgnoreCase)) {
                downBound = true;
            }
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
            if(!IsOwner) return;

            // If chat is open, ignore pause input (Escape closes chat instead)
            if (GameMenuManager.Instance != null && GameMenuManager.Instance.IsChatOpen) return;

            GameMenuManager.Instance.TogglePause();
        }

        [UsedImplicitly]
        private void OnInteract(InputValue _) {
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
                UpdateSniperSensitivity();
                return;
            }

            if(SniperOverlayManager.Instance != null) {
                SniperOverlayManager.Instance.ToggleSniperOverlay(IsSniperOverlayActive);
            }
            ApplySniperOverlayEffects(IsSniperOverlayActive, playZoomSound: false);
            UpdateSniperSensitivity();
        }

        private Vector3? _cachedFpWeaponPosition;
        private Vector3? _cachedFpWeaponRotation;
        [SerializeField] private Vector3 sniperScopedWeaponPosition = new(0f, -0.05f, 0.15f);
        [SerializeField] private Vector3 sniperScopedWeaponRotation = Vector3.zero;
        [SerializeField] private Vector3 sniperMuzzleCameraOffset = new(0f, -0.05f, 0.15f);
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
                if(Audio2.AudioService.Instance != null) {
                    Audio2.AudioService.Instance.Play("ui.sniper.zoom", Vector3.zero);
                }
            }
            UpdateSniperSensitivity();
        }

        /// <summary>Updates sniper scope sensitivity multiplier from settings.</summary>
        private void UpdateSniperSensitivity() {
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
