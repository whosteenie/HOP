using System.Collections;
using Diagnostics;
using Events;
using Game.Audio.System;
using Game.Player.Contracts;
using Game.Settings;
using Game.Social;
using Game.Weapon.Core;
using Game.Weapon.Manager;
using JetBrains.Annotations;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityPlayerInputComponent = UnityEngine.InputSystem.PlayerInput;

namespace Game.Player.Input {
    public class PlayerInputController : NetworkBehaviour {
        #region Serialized Fields

        [Header("Components")]
        [HideInInspector, SerializeField] private MonoBehaviour playerContextSource;

        private IPlayerInputContext _playerContext;

        private UnityPlayerInputComponent _playerInputComponent;
        private InputActionMap _playerActionMap;
        private InputAction _moveAction;
        private InputAction _attackAction;
        private InputAction _jumpAction;
        private InputAction _voiceAction;

        private CinemachineCamera _fpCamera;
        private AudioListener _audioListener;
        private WeaponManager _weaponManager;

        [Header("Input Settings")]
        [SerializeField] private bool toggleSprint = true;

        [SerializeField] private bool toggleCrouch = true;

        #endregion

        private bool _isPauseMenuOpen;
        private bool _isChatOpen;
        private bool _isScoreboardVisible;

        private bool IsPausedOrDead {
            get {
                if(_isChatOpen) return true;
                if(_isPauseMenuOpen) return true;

                return _playerContext is { IsDead: true };
            }
        }

        private bool IsPreMatchMovementLocked => _playerContext is { IsPreMatchMovementLocked: true };
        private bool IsPreMatchOrPausedOrDead => IsPreMatchMovementLocked || IsPausedOrDead;

        private WeaponManager WeaponManager {
            get {
                if(_weaponManager == null && _playerContext != null) {
                    _weaponManager = _playerContext.WeaponManager;
                }

                return _weaponManager;
            }
        }

        private Game.Weapon.Core.Weapon CurrentWeapon => WeaponManager == null ? null : WeaponManager.CurrentWeapon;
        private bool IsMantling => _playerContext is { IsMantling: true };
        private bool CanMantleJump => _playerContext is { CanMantleJump: true };
        private bool IsGrappling => _playerContext is { IsGrappling: true };

        private bool _sprintBtnDown;
        private bool _crouchBtnDown;
        private bool _attackBtnDown;
        private bool _jumpBtnDown;
        private bool _lastHopballPromptVisible;
        private string _lastHopballPromptText = "";
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

                var jumpPressed = _jumpBtnDown || _jumpAction != null && _jumpAction.IsPressed();
                if(jumpPressed) {
                    return true;
                }

                if(Mouse.current == null || Mouse.current.scroll.value.magnitude <= 0f) {
                    return false;
                }

                var scrollY = Mouse.current.scroll.value.y;
                var nextWeaponScrollPressed =
                    IsScrollBindingTriggered(_nextWeaponScrollUpBound, _nextWeaponScrollDownBound, scrollY);
                var previousWeaponScrollPressed =
                    IsScrollBindingTriggered(_previousWeaponScrollUpBound, _previousWeaponScrollDownBound, scrollY);

                if(nextWeaponScrollPressed || previousWeaponScrollPressed) {
                    return false;
                }

                return IsScrollBindingTriggered(_jumpScrollUpBound, _jumpScrollDownBound, scrollY);
            }
        }

        #region Unity Methods

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(!PlayerContractResolver.TryResolve(this, ref playerContextSource, out _playerContext)) {
                DevLog.LogError("[PlayerInputController] IPlayerInputContext not found!");
                enabled = false;
                return;
            }

            if(_playerInputComponent == null) _playerInputComponent = _playerContext.UnityPlayerInput;
            if(_fpCamera == null) _fpCamera = _playerContext.FpCamera;
            if(_audioListener == null) _audioListener = _playerContext.AudioListener;

            if(_fpCamera != null) {
                _defaultFpFov = _fpCamera.Lens.FieldOfView;
            }

            RefreshCachedInputActions();
            RefreshCachedScrollBindings();
        }

        private void OnEnable() {
            EventBus.Unsubscribe<BindingsAppliedEvent>(OnBindingsApplied);
            EventBus.Unsubscribe<PauseMenuStateChangedEvent>(OnPauseMenuStateChanged);
            EventBus.Unsubscribe<ChatOpenStateChangedEvent>(OnChatOpenStateChanged);
            EventBus.Unsubscribe<ScoreboardVisibilityChangedEvent>(OnScoreboardVisibilityChanged);
            EventBus.Unsubscribe<PlayerDiedEvent>(OnPlayerDied);
            EventBus.Unsubscribe<PostMatchSniperOverlayDisableRequestedEvent>(OnSniperOverlayDisableRequested);
            EventBus.Subscribe<BindingsAppliedEvent>(OnBindingsApplied);
            EventBus.Subscribe<PauseMenuStateChangedEvent>(OnPauseMenuStateChanged);
            EventBus.Subscribe<ChatOpenStateChangedEvent>(OnChatOpenStateChanged);
            EventBus.Subscribe<ScoreboardVisibilityChangedEvent>(OnScoreboardVisibilityChanged);
            EventBus.Subscribe<PlayerDiedEvent>(OnPlayerDied);
            EventBus.Subscribe<PostMatchSniperOverlayDisableRequestedEvent>(OnSniperOverlayDisableRequested);
            RefreshCachedScrollBindings();
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            RefreshCachedInputActions();
            RefreshCachedScrollBindings();

            if(WeaponManager != null)
                WeaponManager.InitializeWeapons();

            if(IsOwner && WeaponManager != null) {
                StartCoroutine(RefreshFpWeaponVisualsNextFrame());
            }

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
            EventBus.Unsubscribe<PostMatchSniperOverlayDisableRequestedEvent>(OnSniperOverlayDisableRequested);
            _queuedWeaponCycleOffset = 0;
            _jumpBtnDown = false;
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
            EventBus.Publish(new SetSniperOverlayVisibilityEvent(false));
            ApplySniperOverlayEffects(false, playZoomSound: false);

            ApplyHopballInteractPrompt(false, "PRESS INTERACT");
        }

        private void OnSniperOverlayDisableRequested(PostMatchSniperOverlayDisableRequestedEvent evt) {
            if(evt == null || !IsOwner || evt.PlayerClientId != OwnerClientId) return;
            ForceDisableSniperOverlay(evt.PlayZoomSound);
        }

        private void OnPlayerDied(PlayerDiedEvent evt) {
            if(evt == null || !IsOwner || evt.PlayerId != OwnerClientId) return;
            ForceDisableSniperOverlay(false);
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

        private IEnumerator RefreshFpWeaponVisualsNextFrame() {
            yield return null;

            if(IsOwner && WeaponManager != null) {
                WeaponManager.RefreshOwnerFpWeaponVisuals();
            }
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

            if(VoiceManager.Instance != null && _voiceAction != null) {
                var isPressed = _voiceAction.IsPressed();
                VoiceManager.Instance.SetPttActive(isPressed && !_isChatOpen);
            }

            var attackPressed = _attackAction != null && _attackAction.IsPressed();
            var attackPressedThisFrame = attackPressed && _attackBtnDown == false;
            _attackBtnDown = attackPressed;

            if(!IsPreMatchOrPausedOrDead && fireMode == WeaponData.FireModeType.Full && attackPressed &&
               !IsMantling &&
               _playerContext is not { IsHoldingHopball: true }) {
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

            if(!IsPausedOrDead && (jumpPressed || scrollPressed) && CanMantleJump) {
                // Check if hold-to-mantle is enabled
                var controls = GameSettings.Data.controls;
                var holdMantleEnabled = controls == null || controls.holdMantle;

                // Prioritize Wall Jump over Mantle
                var isWallRunning = _playerContext is { IsWallRunning: true };

                switch(isWallRunning) {
                    // Prevent "Auto-Hop" (holding jump) from unintentionally triggering wall jumps.
                    // If wall running, require a fresh jump press (triggered) or scroll wheel input.
                    case true when !scrollPressed && !_jumpAction.triggered:
                        // Jump is held, but not fresh press - ignore for wall jumping
                        return;
                    // Try mantle if enabled and not grounded (and not wall running)
                    case false when holdMantleEnabled && _playerContext is not {
                        IsGrounded: true
                    }: {
                        _playerContext?.TryMantle();

                        // If we started mantling, don't jump
                        if(IsMantling) {
                            return;
                        }

                        break;
                    }
                }

                // Always allow hold-to-jump (for scroll wheel support)
                _playerContext?.TryJump();

                if(IsGrappling)
                    _playerContext?.CancelGrapple();
            }

            if(!_isPauseMenuOpen && Keyboard.current.tabKey.isPressed) {
                EventBus.Publish(new ShowScoreboardEvent());
            } else if(_isScoreboardVisible) {
                EventBus.Publish(new HideScoreboardEvent());
            }

            // Handle right-click to unlock mouse when scoreboard is open
            if(!_isScoreboardVisible) return;
            if(Mouse.current == null || !Mouse.current.rightButton.wasPressedThisFrame ||
               Cursor.lockState != CursorLockMode.Locked) return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            if(_playerContext != null) {
                _playerContext.LockLook = true;
            }
        }

        private void UpdateHopballInteractPrompt() {
            var canShowPrompt = false;
            var promptText = "PRESS INTERACT";

            var canCheckPickup = !IsPausedOrDead && !IsPreMatchMovementLocked &&
                                 NetworkObject != null &&
                                 _playerContext is { IsHoldingHopball: false };
            if(canCheckPickup) {
                var promptRequest = new PlayerHopballPickupPromptEvaluationRequestedEvent(NetworkObjectId);
                EventBus.Publish(promptRequest);
                canShowPrompt = promptRequest.CanPickupNearbyHopball;
                if(canShowPrompt) {
                    promptText = BuildInteractPromptText();
                }
            }

            ApplyHopballInteractPrompt(canShowPrompt, promptText);
        }

        private void ApplyHopballInteractPrompt(bool visible, string text) {
            var shouldApply = visible != _lastHopballPromptVisible || !string.Equals(text, _lastHopballPromptText);
            if(!shouldApply) return;

            EventBus.Publish(new UpdateHopballInteractPromptEvent(visible, text));
            _lastHopballPromptVisible = visible;
            _lastHopballPromptText = text;
        }

        private static string BuildInteractPromptText() {
            var binding = GetPrimaryInteractBindingName();
            return $"PRESS {binding}";
        }

        private static string GetPrimaryInteractBindingName() {
            var binding = KeybindManager.GetBindingDisplayString("interact", 0);
            if(string.IsNullOrWhiteSpace(binding) ||
               string.Equals(binding, "None", System.StringComparison.OrdinalIgnoreCase)) {
                binding = KeybindManager.GetBindingDisplayString("interact", 1);
            }

            return string.IsNullOrWhiteSpace(binding) ||
                   string.Equals(binding, "None", System.StringComparison.OrdinalIgnoreCase)
                ? "INTERACT"
                : binding.ToUpperInvariant();
        }

        #endregion

        #region Movement

        [UsedImplicitly]
        private void OnLook(InputValue value) {
            if(!IsOwner) return;
            if(IsPausedOrDead || _playerContext is { LockLook: true }) {
                _playerContext?.SetLookInput(Vector2.zero);
                return;
            }

            var rawDelta = value.Get<Vector2>();

            var zoomMultiplier = IsSniperOverlayActive ? _sniperSensitivityMultiplier : 1f;
            _playerContext?.SetLookInput(rawDelta * zoomMultiplier);
        }

        [UsedImplicitly]
        private void OnMove(InputValue value) {
            if(!IsOwner) return;
            if(IsPausedOrDead || _playerContext is { IsPostMatchMovementLocked: true }) {
                _playerContext?.SetMoveInput(Vector2.zero);
                return;
            }

            // Allow movement input to be set even during pre-match
            // It will be ignored during movement processing instead
            _playerContext?.SetMoveInput(value.Get<Vector2>());
        }

        #endregion

        #region Input Recovery

        /// <summary>
        /// Reapplies current held movement action state directly from the input action map.
        /// Used after control restore where no new OnMove callback may fire if the key was already held.
        /// </summary>
        public Vector2 ResampleHeldMovementInput(string reason = "Unknown") {
            if(!IsOwner || _playerContext == null) return Vector2.zero;
            if(_moveAction == null) {
                RefreshCachedInputActions();
            }

            if(_moveAction == null) return Vector2.zero;

            var move = _moveAction.ReadValue<Vector2>();
            _playerContext.SetMoveInput(move);

            FlowLog.Emit(FlowEventIds.PlayerControlState,
                ("player", OwnerClientId),
                ("enabled", true),
                ("reason", reason),
                ("sampledMove", move));
            return move;
        }

        #endregion

        #region Sprint / Crouch

        [UsedImplicitly]
        private void OnSprint(InputValue value) {
            if(!IsOwner) return;
            if(IsPausedOrDead) {
                if(toggleSprint) return;
                if(_playerContext != null) _playerContext.SprintInputState = false;
                return;
            }

            var pressed = value.isPressed;

            if(toggleSprint) {
                // Toggle only on rising edge
                if(pressed && !_sprintBtnDown) {
                    if(_playerContext != null) _playerContext.SprintInputState = !_playerContext.SprintInputState;
                }

                _sprintBtnDown = pressed;
            } else {
                // Hold-to-sprint
                if(_playerContext != null) _playerContext.SprintInputState = pressed;
            }
        }

        [UsedImplicitly]
        private void OnCrouch(InputValue value) {
            if(!IsOwner) return;
            var isMantling = IsMantling;
            if(IsPausedOrDead || isMantling) {
                if(toggleCrouch) return;
                if(_playerContext != null) _playerContext.CrouchInputState = false;
                return;
            }

            var pressed = value.isPressed;

            if(toggleCrouch) {
                // Toggle only on rising edge
                if(pressed && !_crouchBtnDown) {
                    if(_playerContext != null) _playerContext.CrouchInputState = !_playerContext.CrouchInputState;
                }

                _crouchBtnDown = pressed;
            } else {
                // Hold-to-crouch
                if(_playerContext != null) _playerContext.CrouchInputState = pressed;
            }
        }

        [UsedImplicitly]
        private void OnJump(InputValue value) {
            if(!IsOwner || IsPausedOrDead) return;
            _jumpBtnDown = value.isPressed;
            var isMantling = IsMantling;
            if(isMantling) return;

            if(_playerContext is { IsGrounded: false }) {
                // Prioritize Wall Jump over Mantle
                if(_playerContext is { IsWallRunning: true }) {
                    _playerContext.TryJump();
                    return;
                }

                _playerContext?.TryMantle();

                // If we started mantling, don't jump
                if(IsMantling) {
                    return;
                }
            }

            _playerContext?.TryJump();

            if(IsGrappling) {
                _playerContext?.CancelGrapple();
            }
        }

        [UsedImplicitly]
        private void OnScrollWheel(InputValue _) {
            if(!IsOwner || IsPreMatchOrPausedOrDead) return;
            var isMantling = IsMantling;
            if(isMantling) return;

            _playerContext?.TryJump();

            if(IsGrappling) {
                _playerContext?.CancelGrapple();
            }
        }

        [UsedImplicitly]
        private void OnAttack(InputValue value) {
            if(!IsOwner || IsPreMatchOrPausedOrDead) return;
            var isMantling = IsMantling;
            if(isMantling) return;
            if(_playerContext is { IsHoldingHopball: true })
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

                EventBus.Publish(new SetSniperOverlayVisibilityEvent(false));

                return;
            }

            IsSniperOverlayActive = !IsSniperOverlayActive;
            EventBus.Publish(new SetSniperOverlayVisibilityEvent(IsSniperOverlayActive));
            ApplySniperOverlayEffects(IsSniperOverlayActive, playZoomSound: true);
        }

        [UsedImplicitly]
        private void OnGrapple(InputValue value) {
            if(!IsOwner || IsPreMatchOrPausedOrDead || _playerContext is { IsPostMatchFlowStarted: true }) return;
            var isMantling = IsMantling;
            if(isMantling) return;

            if(IsGrappling) {
                _playerContext?.CancelGrapple();
            } else {
                _playerContext?.TryGrapple();
            }
        }

        #endregion

        #region Weapons

        [UsedImplicitly]
        private void OnPrimary(InputValue _) {
            if(!IsOwner || IsPausedOrDead) return;
            var isMantling = IsMantling;
            if(isMantling) return;

            SwitchWeapon(0);
        }

        [UsedImplicitly]
        private void OnSecondary(InputValue _) {
            if(!IsOwner || IsPausedOrDead) return;
            var isMantling = IsMantling;
            if(isMantling) return;

            SwitchWeapon(1);
        }

        [UsedImplicitly]
        private void OnTertiary(InputValue _) {
            if(!IsOwner || IsPausedOrDead) return;
            var isMantling = IsMantling;
            if(isMantling) return;

            DevLog.Log("Tertiary weapons don't exist yet!");
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

            var isMantling = IsMantling;
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
                _playerInputComponent = _playerContext?.UnityPlayerInput;
            }

            if(_playerInputComponent == null || _playerInputComponent.actions == null) {
                _playerActionMap = null;
                _moveAction = null;
                _attackAction = null;
                _jumpAction = null;
                _voiceAction = null;
                return;
            }

            _playerActionMap = _playerInputComponent.actions.FindActionMap("Player");
            _moveAction = _playerActionMap?.FindAction("Move");
            _attackAction = _playerActionMap?.FindAction("Attack");
            _jumpAction = _playerActionMap?.FindAction("Jump");
            _voiceAction = _playerActionMap?.FindAction("Voice");
            _playerActionMap?.FindAction("Grapple");
        }

        private void OnBindingsApplied(BindingsAppliedEvent _) {
            RefreshCachedScrollBindings();
        }

        private void OnPauseMenuStateChanged(PauseMenuStateChangedEvent evt) {
            _isPauseMenuOpen = evt.IsPaused;
        }

        private void OnChatOpenStateChanged(ChatOpenStateChangedEvent evt) {
            _isChatOpen = evt.IsOpen;
        }

        private void OnScoreboardVisibilityChanged(ScoreboardVisibilityChangedEvent evt) {
            _isScoreboardVisible = evt.IsVisible;
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
                        ApplyScrollBinding(entry.binding0, ref _nextWeaponScrollUpBound,
                            ref _nextWeaponScrollDownBound);
                        ApplyScrollBinding(entry.binding1, ref _nextWeaponScrollUpBound,
                            ref _nextWeaponScrollDownBound);
                        break;
                    case "previousweapon":
                        ApplyScrollBinding(entry.binding0, ref _previousWeaponScrollUpBound,
                            ref _previousWeaponScrollDownBound);
                        ApplyScrollBinding(entry.binding1, ref _previousWeaponScrollUpBound,
                            ref _previousWeaponScrollDownBound);
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
            var isMantling = IsMantling;
            if(isMantling) return;
            if(_playerContext is { IsHoldingHopball: true })
                return; // Prevent reloading while holding hopball

            CurrentWeapon.StartReload();
        }

        #endregion

        #region System

        [UsedImplicitly]
        private void OnPause(InputValue _) {
            if(!IsOwner) return;

            // If chat is open, ignore pause input (Escape closes chat instead)
            if(_isChatOpen) return;

            EventBus.Publish(new TogglePauseMenuRequestedEvent());
        }

        [UsedImplicitly]
        private void OnInteract(InputValue _) {
            if(!IsOwner || IsPausedOrDead) return;
            var isMantling = IsMantling;
            if(isMantling) return;

            _playerContext?.PickupHopball();
        }

        private void RefreshSniperOverlayState() {
            if(WeaponManager == null) return;
            var weaponData = WeaponManager.GetWeaponDataByIndex(WeaponManager.CurrentWeaponIndex);
            var canUseOverlay = weaponData != null && weaponData.useSniperOverlay;

            if(!canUseOverlay) {
                if(IsSniperOverlayActive) {
                    IsSniperOverlayActive = false;
                }

                EventBus.Publish(new SetSniperOverlayVisibilityEvent(false));
                ApplySniperOverlayEffects(false, playZoomSound: false);
                UpdateSniperSensitivity();
                return;
            }

            EventBus.Publish(new SetSniperOverlayVisibilityEvent(IsSniperOverlayActive));
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
            if(_playerContext != null) {
                _playerContext.SetCurrentFpWeaponVisible(!zoomEnabled);

                var fpWeapon = _playerContext.GetCurrentFpWeapon();
                if(fpWeapon != null) {
                    if(zoomEnabled) {
                        if(_cachedFpWeaponPosition == null)
                            _cachedFpWeaponPosition = fpWeapon.transform.localPosition;
                        if(_cachedFpWeaponRotation == null)
                            _cachedFpWeaponRotation = fpWeapon.transform.localEulerAngles;

                        _playerContext.OffsetCurrentFpWeapon(sniperScopedWeaponPosition, sniperScopedWeaponRotation);
                    } else {
                        if(_cachedFpWeaponPosition.HasValue) {
                            var rotation = _cachedFpWeaponRotation.HasValue
                                ? _cachedFpWeaponRotation.Value
                                : Vector3.zero;
                            _playerContext.OffsetCurrentFpWeapon(_cachedFpWeaponPosition.Value, rotation);
                        }

                        _cachedFpWeaponPosition = null;
                        _cachedFpWeaponRotation = null;
                    }
                }
            }

            if(_playerContext != null && _playerContext.IsSniperZoomActive != zoomEnabled) {
                _playerContext.SetSniperZoomActive(zoomEnabled, sniperZoomFov);
            }

            if(playZoomSound) {
                if(AudioService.Instance != null) {
                    AudioService.Instance.Play("ui.sniper.zoom", Vector3.zero);
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
                EventBus.Publish(new SetSniperOverlayVisibilityEvent(false));
                return;
            }

            IsSniperOverlayActive = false;
            EventBus.Publish(new SetSniperOverlayVisibilityEvent(false));
            ApplySniperOverlayEffects(false, playZoomSound);
        }

        #endregion
    }
}
