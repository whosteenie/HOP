using System;
using System.Collections;
using System.Collections.Generic;
using Game.Player;
using Game.Menu;
using Game.UI;
using KINEMATION.FPSAnimationPack.Scripts.Weapon;
using Network.AntiCheat;
using Network.Diagnostics;
using Network.Events;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Weapons {
    public class WeaponManager : NetworkBehaviour {
        [Serializable]
        private class KinemationWeaponBinding {
            public WeaponData weaponData;
            public GameObject kinemationWeaponPrefab;
            public bool useCustomViewmodelPose;
            public Vector3 viewmodelLocalPosition = Vector3.zero;
            public Vector3 viewmodelLocalEulerAngles = Vector3.zero;
        }

        [SerializeField] private PlayerController playerController;
        private CinemachineCamera _fpCamera;
        private Camera _weaponCamera;
        private Transform _worldWeaponSocket;
        private Animator _playerAnimator;
        private PlayerRenderer _playerRenderer;

        [Header("Weapon System")]
        [SerializeField, HideInInspector] private List<WeaponData> weaponDataList = new();

        [Header("KINEMATION FP Integration")]
        [SerializeField] private GameObject kinemationFpsPlayerPrefab;
        [SerializeField] private List<KinemationWeaponBinding> kinemationWeaponBindings = new();
        [SerializeField, Range(0f, 1.99f)] private float kinemationSprintWalkGaitValue = 1.2f;
        [SerializeField, Range(0f, 1f)] private float kinemationEquipUnlockNormalizedTime = 0.82f;
        [SerializeField] private bool autoCompleteKinemationPullOut = true;
        [SerializeField, Min(0f)] private float kinemationPullOutCompleteDelay = 0.12f;
        [SerializeField] private Vector3 kinemationViewmodelLocalPosition = Vector3.zero;
        [SerializeField] private Vector3 kinemationViewmodelLocalEulerAngles = Vector3.zero;

        private readonly List<GameObject> _fpWeaponInstances = new();
        private readonly List<WeaponData> _primaryWeaponOptions = new();
        private readonly List<WeaponData> _secondaryWeaponOptions = new();
        private readonly Dictionary<int, int> _weaponAmmo = new();
        private readonly Dictionary<WeaponData, KinemationWeaponBinding> _kinemationWeaponLookup = new();
        private GameObject _pendingTpWeapon; // Track pending TP weapon to show via animation event
        private class ServerWeaponState {
            public float LastShotTime;
            public int ServerAmmo;
            public ulong LastShotId;
        }

        private readonly Dictionary<int, ServerWeaponState> _serverWeaponStates = new();

        public Weapon CurrentWeapon { get; private set; }
        public GameObject CurrentWorldWeaponInstance { get; private set; }

        public int CurrentWeaponIndex { get; private set; } = -1;

        public int WeaponCount => weaponDataList.Count;
        public IReadOnlyList<WeaponData> PrimaryWeaponOptions => _primaryWeaponOptions;
        public IReadOnlyList<WeaponData> SecondaryWeaponOptions => _secondaryWeaponOptions;
        public bool IsPullingOut { get; private set; }

        private static readonly int PullOutHash = Animator.StringToHash("PullOut");
        private static readonly int WeaponIndexHash = Animator.StringToHash("WeaponIndex");
        private const bool UseLegacyBobOnKinemationViewmodel = true;
        private const bool LegacyKinemationMovementBob = false;
        private const bool LegacyKinemationIdleBreathBob = false;
        private const bool LegacyKinemationJumpFallBob = true;
        private const bool LegacyKinemationLandingBob = true;
        private const bool DisableKinemationGlobalSounds = false;
        private const bool DisableKinemationWeaponSounds = false;
        private const bool DisableKinemationPlayerSounds = true;
        private const bool RouteKinemationWeaponSoundEventsToAudioService = true;
        private const bool SyncKinemationLookPitchWithPlayer = false;
        private const bool SyncKinemationAirborneState = false;
        private const bool FreezeKinemationLocomotionInAir = true;
        private const bool ForceKinemationWalkAnimationWhileSprinting = true;
        private const bool RequireKinemationEquipCompleteEvent = true;
        private readonly Dictionary<WeaponData, GameObject> _worldWeaponByData = new();
        public GameObject PrimaryHolster { get; private set; }

        public GameObject SecondaryHolster { get; private set; }

        private int _pendingHolsterHideSlot = -1;
        private bool _suppressLoadoutRebuildCallbacks;
        private bool _deferTpRevealUntilRespawn;
        private GameObject _deferredRespawnWorldWeapon;
        private Coroutine _kinemationPullOutCompletionCoroutine;
        private bool _requiresKinemationEquipCompleteForCurrentPullOut;

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(playerController == null) {
                playerController = this.GetComponentSafe<PlayerController>("WeaponManager.ValidateComponents");
            }

            if(playerController == null) {
                enabled = false;
                return;
            }

            if(CurrentWeapon == null) CurrentWeapon = playerController.WeaponComponent;
            if(_fpCamera == null) _fpCamera = playerController.FpCamera;
            if(_weaponCamera == null) _weaponCamera = playerController.WeaponCamera;
            if(_worldWeaponSocket == null) _worldWeaponSocket = playerController.WorldWeaponSocket;
            if(_playerAnimator == null) _playerAnimator = playerController.PlayerAnimator;
            
            // Validate PlayerRenderer (required for renderer operations)
            if(_playerRenderer == null) _playerRenderer = playerController.PlayerRenderer;
            if(_playerRenderer == null) {
                // PlayerRenderer not found - event already published by GetComponentSafe if used
                enabled = false;
                return;
            }

            BuildKinemationWeaponLookup();
        }

        private void Update() {
            UpdateKinemationEquipCompletionGate();
        }

        private void UpdateKinemationEquipCompletionGate() {
            if(!IsPullingOut || !_requiresKinemationEquipCompleteForCurrentPullOut) return;
            if(CurrentWeaponIndex < 0 || CurrentWeaponIndex >= _fpWeaponInstances.Count) return;

            var currentFpWeapon = _fpWeaponInstances[CurrentWeaponIndex];
            if(!TryGetKinemationDriver(currentFpWeapon, out var kinemationDriver) || kinemationDriver == null) return;
            if(!kinemationDriver.HasActiveWeapon()) return;
            if(kinemationDriver.IsEquipSequenceInProgress()) return;

            HandlePullOutCompleted();
        }

        private void BuildKinemationWeaponLookup() {
            _kinemationWeaponLookup.Clear();
            _primaryWeaponOptions.Clear();
            _secondaryWeaponOptions.Clear();
            if(kinemationWeaponBindings == null || kinemationWeaponBindings.Count == 0) return;

            var primarySeen = new HashSet<WeaponData>();
            var secondarySeen = new HashSet<WeaponData>();
            foreach(var binding in kinemationWeaponBindings) {
                if(binding == null || binding.weaponData == null || binding.kinemationWeaponPrefab == null) continue;
                _kinemationWeaponLookup[binding.weaponData] = binding;

                var slot = ResolveWeaponSlot(binding.weaponData);
                if(slot < 0) {
                    Debug.LogError(
                        $"[WeaponManager] Invalid weapon slot on binding weapon '{binding.weaponData.name}'. " +
                        "Expected Primary/Secondary slot assignment.");
                    continue;
                }

                if(slot == 0) {
                    if(primarySeen.Add(binding.weaponData)) {
                        _primaryWeaponOptions.Add(binding.weaponData);
                    }
                } else {
                    if(secondarySeen.Add(binding.weaponData)) {
                        _secondaryWeaponOptions.Add(binding.weaponData);
                    }
                }
            }
        }

        private bool TryGetKinemationBindingForData(WeaponData data, out KinemationWeaponBinding kinemationBinding) {
            kinemationBinding = null;
            if(kinemationFpsPlayerPrefab == null || data == null) return false;

            if(_kinemationWeaponLookup.Count == 0) {
                BuildKinemationWeaponLookup();
            }

            return _kinemationWeaponLookup.TryGetValue(data, out kinemationBinding) &&
                   kinemationBinding != null &&
                   kinemationBinding.kinemationWeaponPrefab != null;
        }

        private static int ResolveKinemationWeaponCapacity(GameObject kinemationWeaponPrefab) {
            if(kinemationWeaponPrefab == null) return 0;
            var fpsWeapon = kinemationWeaponPrefab.GetComponentInChildren<FPSWeapon>(true);
            if(fpsWeapon == null || fpsWeapon.weaponSettings == null) return 0;
            return Mathf.Max(1, fpsWeapon.weaponSettings.ammo);
        }

        private int ResolveWeaponCapacity(WeaponData data) {
            if(data == null) return 1;

            if(!TryGetKinemationBindingForData(data, out var kinemationBinding)) {
                Debug.LogError(
                    $"[WeaponManager] Missing KINEMATION binding for '{data.weaponName}'. " +
                    "Strict mode requires a KIN binding for every equipped weapon.");
                return 1;
            }

            var kinemationCapacity = ResolveKinemationWeaponCapacity(kinemationBinding.kinemationWeaponPrefab);
            if(kinemationCapacity <= 0) {
                Debug.LogError(
                    $"[WeaponManager] Invalid KINEMATION ammo capacity for '{data.weaponName}'. " +
                    "Strict mode requires FPSWeaponSettings.ammo > 0.");
                return 1;
            }

            return kinemationCapacity;
        }

        private void ResolveKinemationViewmodelPose(KinemationWeaponBinding binding, out Vector3 localPosition,
            out Vector3 localEulerAngles) {
            if(binding != null && binding.useCustomViewmodelPose) {
                localPosition = binding.viewmodelLocalPosition;
                localEulerAngles = binding.viewmodelLocalEulerAngles;
                return;
            }

            localPosition = kinemationViewmodelLocalPosition;
            localEulerAngles = kinemationViewmodelLocalEulerAngles;
        }

        private void ApplyResolvedKinemationViewmodelPose(GameObject fpWeaponRoot, KinemationWeaponBinding binding) {
            if(fpWeaponRoot == null) return;
            ResolveKinemationViewmodelPose(binding, out var localPosition, out var localEulerAngles);
            fpWeaponRoot.transform.localPosition = localPosition;
            fpWeaponRoot.transform.localEulerAngles = localEulerAngles;
        }

        private static bool TryGetKinemationDriver(GameObject fpWeaponRoot, out KinemationFpWeaponDriver driver) {
            driver = fpWeaponRoot != null ? fpWeaponRoot.GetComponent<KinemationFpWeaponDriver>() : null;
            return driver != null;
        }

        private int GetFpWeaponLayer() {
            return IsOwner ? LayerMask.NameToLayer("Weapon") : LayerMask.NameToLayer("Masked");
        }

        private void BuildWorldWeaponLookup() {
            _worldWeaponByData.Clear();
            if(_worldWeaponSocket == null) return;

            foreach(Transform child in _worldWeaponSocket) {
                if(child == null) continue;

                var binding = child.GetComponentInChildren<WorldWeaponBinding>(true);
                if(binding == null || binding.WeaponData == null) {
                    continue;
                }

                if(_worldWeaponByData.ContainsKey(binding.WeaponData)) {
                    Debug.LogWarning(
                        $"[WeaponManager] Duplicate WorldWeaponBinding for '{binding.WeaponData.weaponName}' on '{child.name}'.");
                    continue;
                }

                _worldWeaponByData[binding.WeaponData] = child.gameObject;
            }
        }

        private GameObject ResolveWorldWeaponObject(WeaponData data) {
            if(data == null) return null;

            if(_worldWeaponByData.TryGetValue(data, out var worldWeapon) && worldWeapon != null) {
                return worldWeapon;
            }

            BuildWorldWeaponLookup();
            if(_worldWeaponByData.TryGetValue(data, out worldWeapon) && worldWeapon != null) {
                return worldWeapon;
            }

            return null;
        }

        private void HideCurrentWorldWeapon() {
            if(CurrentWorldWeaponInstance != null) {
                CurrentWorldWeaponInstance.SetActive(false);
                CurrentWorldWeaponInstance = null;
                return;
            }

            if(weaponDataList == null || CurrentWeaponIndex < 0 || CurrentWeaponIndex >= weaponDataList.Count) return;

            var oldData = weaponDataList[CurrentWeaponIndex];
            var oldObj = ResolveWorldWeaponObject(oldData);
            if(oldObj != null) {
                oldObj.SetActive(false);
            }
            CurrentWorldWeaponInstance = null;
        }

        private void HideCurrentWeaponVisuals() {
            if(CurrentWeaponIndex >= 0 && CurrentWeaponIndex < _fpWeaponInstances.Count) {
                var oldFp = _fpWeaponInstances[CurrentWeaponIndex];
                if(oldFp != null) {
                    oldFp.SetActive(false);
                }
            }

            HideCurrentWorldWeapon();
        }

        private GameObject ActivateFpWeapon(int weaponIndex, WeaponData data, bool triggerPullOutAnimation) {
            if(weaponIndex < 0 || weaponIndex >= _fpWeaponInstances.Count || data == null) return null;

            var fp = _fpWeaponInstances[weaponIndex];
            if(fp == null) return null;

            if(!TryGetKinemationDriver(fp, out var kinemationDriver) || kinemationDriver == null) {
                return null;
            }

            TryGetKinemationBindingForData(data, out var kinemationBinding);
            ApplyResolvedKinemationViewmodelPose(fp, kinemationBinding);
            fp.SetActive(true);
            kinemationDriver.InitializeIfNeeded(GetFpWeaponLayer());
            kinemationDriver.PlayEquipAnimation(immediate: !triggerPullOutAnimation);
            return fp;
        }

        private void QueuePendingTpWeapon(WeaponData data) {
            _pendingTpWeapon = ResolveWorldWeaponObject(data);
            if(_pendingTpWeapon != null) {
                _pendingTpWeapon.SetActive(false);
            }

            CurrentWorldWeaponInstance = null;
        }

        private int ResolveRestoredAmmo(int weaponIndex, int magCapacity, bool seedWhenMissing) {
            var clampedCapacity = Mathf.Max(1, magCapacity);
            if(_weaponAmmo.TryGetValue(weaponIndex, out var storedAmmo)) {
                var clampedStored = Mathf.Clamp(storedAmmo, 0, clampedCapacity);
                _weaponAmmo[weaponIndex] = clampedStored;
                return clampedStored;
            }

            if(seedWhenMissing) {
                _weaponAmmo[weaponIndex] = clampedCapacity;
            }

            return clampedCapacity;
        }

        private void RefreshOwnerHolsterShadowState() {
            if(IsOwner && playerController != null && playerController.PlayerShadow != null) {
                playerController.PlayerShadow.UpdateHolsterShadowStateForOwner();
            }
        }

        private void TriggerTpPullOutAnimation(int weaponIndex) {
            if(_playerAnimator == null) return;
            _playerAnimator.SetInteger(WeaponIndexHash, weaponIndex);
            _playerAnimator.SetTrigger(PullOutHash);
        }

        private void ScheduleKinemationPullOutCompletionIfNeeded(int weaponIndex) {
            if(!autoCompleteKinemationPullOut) return;
            if(_requiresKinemationEquipCompleteForCurrentPullOut) return;
            if(weaponIndex < 0 || weaponIndex >= _fpWeaponInstances.Count) return;

            var fpWeaponRoot = _fpWeaponInstances[weaponIndex];
            if(!TryGetKinemationDriver(fpWeaponRoot, out _)) return;

            if(_kinemationPullOutCompletionCoroutine != null) {
                StopCoroutine(_kinemationPullOutCompletionCoroutine);
            }

            _kinemationPullOutCompletionCoroutine = StartCoroutine(KinemationPullOutCompletionRoutine());
        }

        private IEnumerator KinemationPullOutCompletionRoutine() {
            var delay = Mathf.Max(0f, kinemationPullOutCompleteDelay);
            if(delay > 0f) {
                yield return new WaitForSeconds(delay);
            } else {
                yield return null;
            }

            _kinemationPullOutCompletionCoroutine = null;
            HandlePullOutCompleted();
        }

        public void InitializeWeapons() {
            if(CurrentWeapon == null) {
                Debug.LogError("[WeaponManager] Weapon component not assigned!");
                return;
            }

            // Subscribe to weapon index changes to rebuild weapon list when they sync
            if(playerController != null) {
                playerController.primaryWeaponIndex.OnValueChanged -= OnWeaponIndexChanged;
                playerController.primaryWeaponIndex.OnValueChanged += OnWeaponIndexChanged;
                playerController.secondaryWeaponIndex.OnValueChanged -= OnWeaponIndexChanged;
                playerController.secondaryWeaponIndex.OnValueChanged += OnWeaponIndexChanged;
            }

            BuildEquippedWeaponList();
            BuildWorldWeaponLookup();
            if(!ValidateStrictEquippedWeaponConfiguration()) return;
            SetupHolsteredWeaponModels();
            DisableUnequippedWorldWeapons();

            if(weaponDataList == null || weaponDataList.Count == 0) {
                Debug.LogError("[WeaponManager] weaponDataList is empty!");
                return;
            }

            HideAllWorldWeapons();
            InstantiateFpWeaponInstances();

            // Switch to first weapon
            if(_fpWeaponInstances.Count > 0) {
                EquipInitialWeapon(0);
            } else {
                Debug.LogError("[WeaponManager] No weapons instantiated!");
            }

            UpdateHolsterVisibility();
        }


        public void SwitchWeapon(int newIndex) {
            if(newIndex < 0 || newIndex >= weaponDataList.Count)
                return;

            // Check if holding hopball - if so, allow switching even to same weapon
            // Also check if restoring after dissolve to allow switch
            var isHoldingHopball = false;
            var isRestoringAfterDissolve = false;
            if(IsOwner) {
                if(playerController == null) return;
                var hopballController = playerController.PlayerHopballController;
                if(hopballController != null) {
                    if(hopballController.IsHoldingHopball) {
                        isHoldingHopball = true;
                        // Drop hopball when switching weapons (let weapon switch visuals handle showing)
                        hopballController.DropHopball(PlayerHopballController.HopballDropReason.WeaponSwitch);
                    }

                    // Check if restoring after dissolve
                    if(PlayerHopballController.IsRestoringAfterDissolve) {
                        isRestoringAfterDissolve = true;
                    }
                }
            }

            // Block switching to same weapon unless holding hopball or restoring after dissolve
            if(newIndex == CurrentWeaponIndex && !isHoldingHopball && !isRestoringAfterDissolve)
                return;

            if(IsOwner) {
                if(Audio2.AudioService.Instance != null) {
                    Audio2.AudioService.Instance.Play("ui.weapon.switch", Vector3.zero);
                }
            }

            // Publish weapon switch event
            EventBus.Publish(new WeaponSwitchedEvent(newIndex));

            // Cache ammo from current weapon before switching away
            if(CurrentWeapon != null && CurrentWeaponIndex >= 0) {
                _weaponAmmo[CurrentWeaponIndex] = CurrentWeapon.currentAmmo;
            }

            // Immediately hide current weapon (no sheath delay)
            if(CurrentWeaponIndex >= 0) {
                HideCurrentWeaponVisuals();
            }

            // Commit to new weapon index immediately
            CurrentWeaponIndex = newIndex;
            var data = weaponDataList[CurrentWeaponIndex];
            _pendingHolsterHideSlot = GetSlotForIndex(CurrentWeaponIndex);

            // Prepare and show new FP weapon
            var fp = ActivateFpWeapon(CurrentWeaponIndex, data, triggerPullOutAnimation: true);
            _requiresKinemationEquipCompleteForCurrentPullOut =
                RequireKinemationEquipCompleteEvent &&
                fp != null &&
                TryGetKinemationDriver(fp, out _);

            // Prepare new 3P weapon but DON'T show it yet - wait for animation event
            QueuePendingTpWeapon(data);

            // Restore ammo from authoritative KINEMATION capacity path.
            var magCapacity = ResolveWeaponCapacity(data);
            var restoredAmmo = ResolveRestoredAmmo(CurrentWeaponIndex, magCapacity, seedWhenMissing: false);

            // Update weapon data immediately (no waiting for animations)
            // Pass null for worldWeaponInstance since it's not shown yet - will be set when TP weapon is shown
            CurrentWeapon.SwitchToWeapon(data, fp, null, restoredAmmo, magCapacity);
            ReportAmmoSync(CurrentWeaponIndex, restoredAmmo);

            // Set pulling out state
            // The pull-out animation will call HandlePullOutCompleted() when done
            IsPullingOut = true;
            ScheduleKinemationPullOutCompletionIfNeeded(CurrentWeaponIndex);

            if(_playerAnimator == null) return;
            TriggerTpPullOutAnimation(newIndex);

            if(IsOwner) {
                if(IsServer) {
                    if(TryConsumeWeaponSwitchQuota()) {
                        BroadcastWeaponSwitchClientRpc(newIndex);
                    }
                } else {
                    RequestWeaponSwitchBroadcastServerRpc(newIndex);
                }
            }

            UpdateHolsterVisibility();
            RefreshOwnerHolsterShadowState();
        }

        /// <summary>
        /// Called from player animation event to show the TP weapon during pull out animation.
        /// </summary>
        public void ShowTpWeapon() {
            if(_pendingTpWeapon == null) return;
            _pendingTpWeapon.SetActive(true);

            // Update weapon data with the now-active TP weapon
            if(CurrentWeapon != null && CurrentWeaponIndex >= 0) {
                var data = weaponDataList[CurrentWeaponIndex];
                var fpWeapon = _fpWeaponInstances[CurrentWeaponIndex];
                var magCapacity = ResolveWeaponCapacity(data);
                var restoredAmmo = ResolveRestoredAmmo(CurrentWeaponIndex, magCapacity, seedWhenMissing: false);

                CurrentWeapon.SwitchToWeapon(
                    data,
                    fpWeapon,
                    _pendingTpWeapon,
                    restoredAmmo,
                    magCapacity
                );
            }

            CurrentWorldWeaponInstance = _pendingTpWeapon;
            _pendingTpWeapon = null;

            EnsureWorldWeaponShadowState();
            EnsureWeaponHierarchyActive();

            _pendingHolsterHideSlot = -1;
            UpdateHolsterVisibility();
            RefreshOwnerHolsterShadowState();
        }

        /// <summary>
        /// Called when the pull-out animation completes (via animation event).
        /// Allows shooting and reloading again.
        /// </summary>
        public void HandlePullOutCompleted() {
            IsPullingOut = false;
            _requiresKinemationEquipCompleteForCurrentPullOut = false;
            if(_kinemationPullOutCompletionCoroutine != null) {
                StopCoroutine(_kinemationPullOutCompletionCoroutine);
                _kinemationPullOutCompletionCoroutine = null;
            }
        }

        public void HandleThirdPersonPullOutCompleted() {
            if(_requiresKinemationEquipCompleteForCurrentPullOut) {
                return;
            }

            HandlePullOutCompleted();
        }

        public void HandleKinemationEquipCompleted() {
            HandlePullOutCompleted();
        }

        /// <summary>
        /// Triggers the pullout animation. Used when hopball dissolves to restore weapon visibility.
        /// </summary>
        public void TriggerPullOutAnimation() {
            if(_playerAnimator == null) return;
            
            // If we're not switching weapons (e.g., after hopball dissolve), we need to set up _pendingTpWeapon
            // so the animation event can show it. The weapon might already be inactive from HideWorldWeapon().
            if(_pendingTpWeapon == null && CurrentWeaponIndex >= 0 && CurrentWeaponIndex < weaponDataList.Count) {
                QueuePendingTpWeapon(weaponDataList[CurrentWeaponIndex]);
                // Set holster slot to hide the correct holster during pullout
                _pendingHolsterHideSlot = GetSlotForIndex(CurrentWeaponIndex);
                UpdateHolsterVisibility();
            }
            
            TriggerTpPullOutAnimation(CurrentWeaponIndex);
            
            // Mark as pulling out
            IsPullingOut = true;
            _requiresKinemationEquipCompleteForCurrentPullOut = false;
            ScheduleKinemationPullOutCompletionIfNeeded(CurrentWeaponIndex);
        }

        /// <summary>
        /// Cancels any pending pull-out transition and forces a stable TP weapon state.
        /// Used during post-match blackout to avoid visible switch artifacts on podium.
        /// </summary>
        public void CancelPendingPullOutForPostMatch() {
            IsPullingOut = false;
            _requiresKinemationEquipCompleteForCurrentPullOut = false;
            _pendingHolsterHideSlot = -1;
            if(_kinemationPullOutCompletionCoroutine != null) {
                StopCoroutine(_kinemationPullOutCompletionCoroutine);
                _kinemationPullOutCompletionCoroutine = null;
            }

            if(_playerAnimator != null) {
                _playerAnimator.ResetTrigger(PullOutHash);
            }

            if(CurrentWorldWeaponInstance == null) {
                ResolveCurrentWorldWeaponReference();
            }

            _pendingTpWeapon = null;
            if(CurrentWorldWeaponInstance != null && !CurrentWorldWeaponInstance.activeSelf) {
                CurrentWorldWeaponInstance.SetActive(true);
            }

            EnsureWeaponHierarchyActive();

            // Podium flow needs visible TP weapon even for owners.
            if(playerController != null) {
                if(playerController.PlayerRenderer != null) {
                    playerController.PlayerRenderer.SetWorldWeaponRenderersEnabled(true);
                }

                if(playerController.PlayerShadow != null) {
                    playerController.PlayerShadow.SetWorldWeaponRenderersShadowMode(ShadowCastingMode.On);
                }
            }

            UpdateHolsterVisibility();
        }

        private void EnsureWorldWeaponShadowState() {
            if(CurrentWorldWeaponInstance == null) return;

            if(!CurrentWorldWeaponInstance.activeSelf) {
                CurrentWorldWeaponInstance.SetActive(true);
            }

            var isOwner = playerController != null && playerController.IsOwner;
            var isPostMatch = GameMenuManager.Instance != null && GameMenuManager.Instance.IsPostMatch;
            var targetMode = isOwner && !isPostMatch
                ? ShadowCastingMode.ShadowsOnly
                : ShadowCastingMode.On;

            var playerShadow = playerController != null ? playerController.PlayerShadow : null;
            if(playerShadow != null) {
                playerShadow.SetWorldWeaponRenderersShadowMode(targetMode);
                return;
            }

            var renderers = CurrentWorldWeaponInstance.GetComponentsInChildren<MeshRenderer>(true);
            foreach(var mr in renderers) {
                if(mr == null) continue;
                mr.enabled = true;
                mr.shadowCastingMode = targetMode;
            }
        }

        private void EnsureWeaponHierarchyActive() {
            if(CurrentWorldWeaponInstance == null) return;
            EnsureHierarchyActive(CurrentWorldWeaponInstance);
        }

        private static void EnsureHierarchyActive(GameObject instanceRoot) {
            if(instanceRoot == null) return;
            var parent = instanceRoot.transform;
            while(parent != null) {
                if(!parent.gameObject.activeSelf) {
                    parent.gameObject.SetActive(true);
                }

                parent = parent.parent;
            }
        }

        [Rpc(SendTo.Server)]
        private void RequestWeaponSwitchBroadcastServerRpc(int newIndex) {
            if(!TryConsumeWeaponSwitchQuota()) return;
            BroadcastWeaponSwitchClientRpc(newIndex);
        }

        [Rpc(SendTo.Everyone, Delivery = RpcDelivery.Unreliable)]
        private void BroadcastWeaponSwitchClientRpc(int newIndex) {
            if(IsOwner) return;
            ApplyRemoteWeaponSwitch(newIndex);
        }

        private void ApplyRemoteWeaponSwitch(int newIndex) {
            if(newIndex < 0 || newIndex >= weaponDataList.Count) return;

            HideCurrentWorldWeapon();

            CurrentWeaponIndex = newIndex;
            var data = weaponDataList[newIndex];
            _pendingHolsterHideSlot = GetSlotForIndex(CurrentWeaponIndex);

            QueuePendingTpWeapon(data);

            if(_playerAnimator == null) return;
            TriggerTpPullOutAnimation(newIndex);

            UpdateHolsterVisibility();
        }

        public void ResetAllWeaponAmmo() {
            _weaponAmmo.Clear();
            for(var i = 0; i < weaponDataList.Count; i++) {
                var data = weaponDataList[i];
                if(data != null) {
                    var magCapacity = ResolveWeaponCapacity(data);
                    _weaponAmmo[i] = Mathf.Clamp(magCapacity, 0, int.MaxValue);
                }
            }
        }

        /// <summary>
        /// Drains ammo for the currently equipped weapon for this player.
        /// Server-authoritative: updates server validation ammo and syncs owner's FP/HUD state.
        /// </summary>
        public void DrainCurrentWeaponAmmoForTag() {
            if(!IsServer) return;
            if(CurrentWeaponIndex < 0 || CurrentWeaponIndex >= weaponDataList.Count) return;

            var data = weaponDataList[CurrentWeaponIndex];
            if(data == null) return;
            var magCapacity = ResolveWeaponCapacity(data);

            _weaponAmmo[CurrentWeaponIndex] = 0;
            UpdateServerAmmo(CurrentWeaponIndex, 0);
            ApplyDrainedAmmoOwnerClientRpc(CurrentWeaponIndex, 0, magCapacity);
        }

        [Rpc(SendTo.Owner)]
        private void ApplyDrainedAmmoOwnerClientRpc(int weaponIndex, int ammo, int magSize) {
            _weaponAmmo[weaponIndex] = Mathf.Max(0, ammo);

            if(CurrentWeapon != null && CurrentWeaponIndex == weaponIndex) {
                CurrentWeapon.currentAmmo = Mathf.Max(0, ammo);
            }

            if(IsOwner && HUDManager.Instance != null && CurrentWeaponIndex == weaponIndex) {
                EventBus.Publish(new UpdateAmmoEvent(Mathf.Max(0, ammo), Mathf.Max(0, magSize)));
            }
        }

        public int GetPrimarySelectionIndex() {
            if(_primaryWeaponOptions.Count == 0 && _secondaryWeaponOptions.Count == 0) {
                BuildKinemationWeaponLookup();
            }
            if(playerController == null || _primaryWeaponOptions.Count == 0) {
                return 0;
            }

            return Mathf.Clamp(playerController.primaryWeaponIndex.Value, 0, _primaryWeaponOptions.Count - 1);
        }

        public int GetSecondarySelectionIndex() {
            if(_primaryWeaponOptions.Count == 0 && _secondaryWeaponOptions.Count == 0) {
                BuildKinemationWeaponLookup();
            }
            if(playerController == null || _secondaryWeaponOptions.Count == 0) {
                return 0;
            }

            return Mathf.Clamp(playerController.secondaryWeaponIndex.Value, 0, _secondaryWeaponOptions.Count - 1);
        }

        public bool ApplyOwnerLoadoutSelection(int primaryIndex, int secondaryIndex,
            bool deferTpRevealUntilRespawn = true) {
            if(!IsOwner || playerController == null) return false;
            if(_primaryWeaponOptions.Count == 0 && _secondaryWeaponOptions.Count == 0) {
                BuildKinemationWeaponLookup();
            }

            var clampedPrimary = ClampOptionIndex(_primaryWeaponOptions, primaryIndex);
            var clampedSecondary = ClampOptionIndex(_secondaryWeaponOptions, secondaryIndex);

            var primaryChanged = playerController.primaryWeaponIndex.Value != clampedPrimary;
            var secondaryChanged = playerController.secondaryWeaponIndex.Value != clampedSecondary;
            if(!primaryChanged && !secondaryChanged) {
                return false;
            }

            _suppressLoadoutRebuildCallbacks = true;
            try {
                if(primaryChanged) {
                    playerController.primaryWeaponIndex.Value = clampedPrimary;
                }

                if(secondaryChanged) {
                    playerController.secondaryWeaponIndex.Value = clampedSecondary;
                }
            } finally {
                _suppressLoadoutRebuildCallbacks = false;
            }

            RebuildEquippedWeapons(
                preserveCurrentSlot: false,
                deferTpRevealUntilRespawn: deferTpRevealUntilRespawn
            );
            return true;
        }

        private static int ClampOptionIndex(List<WeaponData> options, int requestedIndex) {
            if(options == null || options.Count == 0) return 0;
            return Mathf.Clamp(requestedIndex, 0, options.Count - 1);
        }

        public void ApplyTpWeaponStateOnRespawn() {
            if(_playerAnimator != null) {
                var slot = Mathf.Clamp(GetSlotForIndex(CurrentWeaponIndex), 0, 1);
                _playerAnimator.SetInteger(WeaponIndexHash, slot);
                _playerAnimator.Rebind();
                _playerAnimator.Update(0f);
            }

            if(_deferredRespawnWorldWeapon != null) {
                if(CurrentWorldWeaponInstance != null && CurrentWorldWeaponInstance != _deferredRespawnWorldWeapon) {
                    CurrentWorldWeaponInstance.SetActive(false);
                }

                CurrentWorldWeaponInstance = _deferredRespawnWorldWeapon;
                _deferredRespawnWorldWeapon = null;
            }

            ResolveCurrentWorldWeaponReference();
            if(CurrentWorldWeaponInstance != null && !CurrentWorldWeaponInstance.activeSelf) {
                CurrentWorldWeaponInstance.SetActive(true);
            }

            if(CurrentWorldWeaponInstance != null) {
                EnsureWeaponHierarchyActive();
                EnsureWorldWeaponShadowState();

                if(IsOwner && _playerRenderer != null) {
                    _playerRenderer.SetWorldWeaponRenderersEnabled(true);
                }
            }

            if(IsOwner) {
                var currentFpWeapon = GetCurrentFpWeapon();
                if(currentFpWeapon != null) {
                    if(CurrentWeaponIndex >= 0 && CurrentWeaponIndex < weaponDataList.Count &&
                       TryGetKinemationDriver(currentFpWeapon, out _)) {
                        var data = weaponDataList[CurrentWeaponIndex];
                        TryGetKinemationBindingForData(data, out var kinemationBinding);
                        ApplyResolvedKinemationViewmodelPose(currentFpWeapon, kinemationBinding);
                    }

                    EnsureHierarchyActive(currentFpWeapon);
                    currentFpWeapon.SetActive(true);

                    SetupFpWeaponSkinnedMeshRenderers(currentFpWeapon);
                    if(_playerRenderer != null) {
                        _playerRenderer.SetFpWeaponRenderersEnabled(true, currentFpWeapon);
                        _playerRenderer.SetFpWeaponSkinnedRenderersEnabled(true, currentFpWeapon);
                    }
                }
            }

            _deferTpRevealUntilRespawn = false;
            UpdateHolsterVisibility();
        }

        public GameObject GetCurrentFpWeapon() {
            if(CurrentWeaponIndex < 0 || CurrentWeaponIndex >= _fpWeaponInstances.Count) return null;
            return _fpWeaponInstances[CurrentWeaponIndex];
        }

        public void UpdateAllFpArmTagGlow(bool isTagged) {
            if(!IsOwner || playerController == null) return;
            var visualController = playerController.VisualController;
            if(visualController == null) return;

            for(var i = 0; i < _fpWeaponInstances.Count; i++) {
                var fpWeapon = _fpWeaponInstances[i];
                if(fpWeapon == null) continue;
                visualController.UpdateFpArmTagGlow(isTagged, fpWeapon);
            }
        }

        public void SetCurrentFpWeaponVisible(bool visible) {
            var fpWeapon = GetCurrentFpWeapon();
            if(fpWeapon == null) return;

            _playerRenderer.SetFpWeaponRenderersEnabled(visible, fpWeapon);
        }

        public void OffsetCurrentFpWeapon(Vector3 localPosition, Vector3 localEulerAngles) {
            var fpWeapon = GetCurrentFpWeapon();
            if(fpWeapon == null) return;
            fpWeapon.transform.localPosition = localPosition;
            fpWeapon.transform.localEulerAngles = localEulerAngles;
        }

        public WeaponData GetWeaponDataByIndex(int index) {
            if(index < 0 || index >= weaponDataList.Count) return null;
            return weaponDataList[index];
        }

        public string GetWeaponIdByIndex(int index) {
            var data = GetWeaponDataByIndex(index);
            return data != null ? data.weaponName : null;
        }

        private void EquipInitialWeapon(int index) {
            if(index < 0 || index >= weaponDataList.Count) {
                Debug.LogError($"[WeaponManager] EquipInitialWeapon: invalid index {index}");
                return;
            }

            CurrentWeaponIndex = index;
            IsPullingOut = false;
            _requiresKinemationEquipCompleteForCurrentPullOut = false;

            var data = weaponDataList[index];
            var fp = ActivateFpWeapon(index, data, triggerPullOutAnimation: false);

            // ---- 3P WORLD WEAPON ----
            var worldWeaponInstance = ResolveWorldWeaponObject(data);
            if(worldWeaponInstance != null) {
                worldWeaponInstance.SetActive(true);
                CurrentWorldWeaponInstance = worldWeaponInstance;
            }

            // ---- AMMO ----
            var magCapacity = ResolveWeaponCapacity(data);
            var restoredAmmo = ResolveRestoredAmmo(index, magCapacity, seedWhenMissing: true);

            // This sets weapon data, ammo, HUD, muzzle lights, etc.
            CurrentWeapon.SwitchToWeapon(
                data,
                fp,
                worldWeaponInstance,
                restoredAmmo,
                magCapacity
            );

            ReportAmmoSync(CurrentWeaponIndex, restoredAmmo);

            _pendingHolsterHideSlot = -1;
            UpdateHolsterVisibility();
            RefreshOwnerHolsterShadowState();
        }

        /// <summary>
        /// Recursively sets the layer of a GameObject and all its children
        /// </summary>
        private static void SetGameObjectAndChildrenLayer(GameObject obj, int layer) {
            if(obj == null) return;

            obj.layer = layer;
            foreach(Transform child in obj.transform) {
                SetGameObjectAndChildrenLayer(child.gameObject, layer);
            }
        }

        /// <summary>
        /// Enables and configures SkinnedMeshRenderers for FP weapon models (e.g., arm models).
        /// Sets shadow casting to Off and ensures they are enabled.
        /// Also applies player material customization from PlayerPrefs (owner only).
        /// </summary>
        private void SetupFpWeaponSkinnedMeshRenderers(GameObject fpWeaponInstance) {
            if(fpWeaponInstance == null) return;

            // Use PlayerRenderer for enabled state
            _playerRenderer.SetFpWeaponSkinnedRenderersEnabled(true, fpWeaponInstance);

            var skinnedRenderers = fpWeaponInstance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach(var skinnedRenderer in skinnedRenderers) {
                if(skinnedRenderer == null) continue;
                // Shadow mode is handled by PlayerShadow, but we set it here for initial setup
                skinnedRenderer.shadowCastingMode = ShadowCastingMode.Off;
            }

            // Apply player material customization (owner only, local rendering)
            // Use same approach as hopball arms - apply to all renderers
            if(!IsOwner) return;
            ApplyPlayerMaterialToFpWeapon(fpWeaponInstance);

            // Add tag glow update
            var tagController = playerController.GetComponent<PlayerTagController>();
            if(tagController == null || !tagController.isTagged.Value) return;
            var visualController = playerController.GetComponent<PlayerVisualController>();
            if (visualController != null) {
                visualController.UpdateFpArmTagGlow(true, fpWeaponInstance);
            }
        }

        /// <summary>
        /// Applies player material customization from PlayerVisualController to FP weapon arms only.
        /// Only called for owners since FP weapon rendering is fully local.
        /// </summary>
        private void ApplyPlayerMaterialToFpWeapon(GameObject fpWeaponInstance) {
            if(fpWeaponInstance == null || playerController == null) return;

            // Use PlayerVisualController to ensure we use the cached, generated material
            // instead of creating a new one from the mesh which misses customization packets.
            var visualController = playerController.GetComponent<PlayerVisualController>();
            if(visualController != null) {
                visualController.ApplyMaterialToFpArms(fpWeaponInstance);
            }
        }

        #region Holstered Weapons

        private void SetupHolsteredWeaponModels() {
            PrimaryHolster = ResolveWorldWeaponObject(GetWeaponDataForSlot(0));
            SecondaryHolster = ResolveWorldWeaponObject(GetWeaponDataForSlot(1));

            if(PrimaryHolster == null) {
                Debug.LogError("[WeaponManager] Missing Primary holster world weapon binding.");
            }

            if(SecondaryHolster == null) {
                Debug.LogError("[WeaponManager] Missing Secondary holster world weapon binding.");
            }

            DisableHolster(PrimaryHolster);
            DisableHolster(SecondaryHolster);
        }

        private WeaponData GetWeaponDataForSlot(int slot) {
            if(weaponDataList == null || weaponDataList.Count == 0) return null;
            for(var i = 0; i < weaponDataList.Count; i++) {
                var data = weaponDataList[i];
                if(data == null) continue;
                var weaponSlot = ResolveWeaponSlot(data);
                if(weaponSlot == slot) {
                    return data;
                }
            }

            return null;
        }

        private static int ResolveWeaponSlot(WeaponData data) {
            if(data == null) return -1;
            var slot = data.WeaponSlotIndex;
            return slot is 0 or 1 ? slot : -1;
        }

        private static void DisableHolster(GameObject holster) {
            if(holster == null) return;
            if(holster.activeSelf) {
                holster.SetActive(false);
            }
        }

        private void UpdateHolsterVisibility() {
            var currentSlot = GetSlotForIndex(CurrentWeaponIndex);

            if(PrimaryHolster != null) {
                var showPrimary = currentSlot != 0 || _pendingHolsterHideSlot == 0;
                if(PrimaryHolster.activeSelf != showPrimary) {
                    PrimaryHolster.SetActive(showPrimary);
                }
            }

            if(SecondaryHolster == null) return;
            var showSecondary = currentSlot != 1 || _pendingHolsterHideSlot == 1;
            if(SecondaryHolster.activeSelf != showSecondary) {
                SecondaryHolster.SetActive(showSecondary);
            }
        }
        
        #endregion

        private int GetSlotForIndex(int index) {
            var data = GetWeaponDataByIndex(index);
            if(data == null) return -1;
            return ResolveWeaponSlot(data);
        }
        public int GetCurrentHolsterSlot() => GetSlotForIndex(CurrentWeaponIndex);
        public void RefreshHolsterVisibility() => UpdateHolsterVisibility();

        /// <summary>
        /// Rebuilds equipped FP/TP weapons from current loadout indices.
        /// </summary>
        private void RebuildEquippedWeapons(bool preserveCurrentSlot, bool deferTpRevealUntilRespawn) {
            if(CurrentWeapon == null || _fpCamera == null) {
                ValidateComponents();
            }

            if(CurrentWeapon == null || _fpCamera == null) {
                return;
            }

            var targetSlot = preserveCurrentSlot
                ? Mathf.Clamp(GetSlotForIndex(CurrentWeaponIndex), 0, 1)
                : 0;
            var previousWorldWeapon = CurrentWorldWeaponInstance;
            var keepPreviousWorldWeaponVisible =
                deferTpRevealUntilRespawn && previousWorldWeapon != null && previousWorldWeapon.activeSelf;

            BuildEquippedWeaponList();
            BuildWorldWeaponLookup();
            if(!ValidateStrictEquippedWeaponConfiguration()) return;
            SetupHolsteredWeaponModels();
            DisableUnequippedWorldWeapons();

            if(weaponDataList == null || weaponDataList.Count == 0) {
                Debug.LogError("[WeaponManager] weaponDataList is empty after rebuild!");
                return;
            }

            DestroyFpWeaponInstances();
            HideAllWorldWeapons(keepPreviousWorldWeaponVisible ? previousWorldWeapon : null);
            InstantiateFpWeaponInstances();

            if(_fpWeaponInstances.Count == 0) {
                Debug.LogError("[WeaponManager] No FP weapons available after rebuild!");
                return;
            }

            var targetIndex = ResolveIndexForSlot(targetSlot);
            EquipInitialWeapon(targetIndex);

            _deferTpRevealUntilRespawn = deferTpRevealUntilRespawn;
            if(_deferTpRevealUntilRespawn) {
                var nextWorldWeapon = CurrentWorldWeaponInstance;
                if(nextWorldWeapon != null) {
                    nextWorldWeapon.SetActive(false);
                    _deferredRespawnWorldWeapon = nextWorldWeapon != previousWorldWeapon || !keepPreviousWorldWeaponVisible
                        ? nextWorldWeapon
                        : null;
                } else {
                    _deferredRespawnWorldWeapon = null;
                }

                if(keepPreviousWorldWeaponVisible && previousWorldWeapon != null) {
                    previousWorldWeapon.SetActive(true);
                    CurrentWorldWeaponInstance = previousWorldWeapon;
                } else {
                    CurrentWorldWeaponInstance = null;
                }
            } else {
                _deferredRespawnWorldWeapon = null;
                ResolveCurrentWorldWeaponReference();
                if(CurrentWorldWeaponInstance != null && !CurrentWorldWeaponInstance.activeSelf) {
                    CurrentWorldWeaponInstance.SetActive(true);
                }

                if(CurrentWorldWeaponInstance != null) {
                    EnsureWorldWeaponShadowState();
                }
            }

            UpdateHolsterVisibility();
        }

        private void DestroyFpWeaponInstances() {
            if(_kinemationPullOutCompletionCoroutine != null) {
                StopCoroutine(_kinemationPullOutCompletionCoroutine);
                _kinemationPullOutCompletionCoroutine = null;
            }

            foreach(var fpWeapon in _fpWeaponInstances) {
                if(fpWeapon == null) continue;
                var holderRoot = ResolveFpHolderRoot(fpWeapon);
                Destroy(holderRoot != null ? holderRoot : fpWeapon);
            }

            _fpWeaponInstances.Clear();
            _weaponAmmo.Clear();
            _serverWeaponStates.Clear();
        }

        private GameObject ResolveFpHolderRoot(GameObject fpWeapon) {
            if(fpWeapon == null) return null;

            var node = fpWeapon.transform;
            while(node.parent != null && !IsFpHolderParent(node.parent)) {
                node = node.parent;
            }

            return IsFpHolderParent(node.parent) ? node.gameObject : null;
        }

        private bool IsFpHolderParent(Transform parent) {
            if(parent == null) return false;
            if(_fpCamera != null && parent == _fpCamera.transform) return true;
            return _weaponCamera != null && parent == _weaponCamera.transform;
        }

        private void HideAllWorldWeapons(GameObject keepVisible = null) {
            if(_worldWeaponSocket == null) return;

            foreach(Transform child in _worldWeaponSocket) {
                if(child == null) continue;
                if(keepVisible != null && child.gameObject == keepVisible) continue;
                child.gameObject.SetActive(false);
            }

            CurrentWorldWeaponInstance = keepVisible;
            _pendingTpWeapon = null;
        }

        private void InstantiateFpWeaponInstances() {
            for(var i = 0; i < weaponDataList.Count; i++) {
                var data = weaponDataList[i];
                if(data == null) {
                    Debug.LogError($"[WeaponManager] Invalid weapon data at index {i}");
                    continue;
                }

                if(!TryGetKinemationBindingForData(data, out var kinemationBinding)) {
                    Debug.LogError($"[WeaponManager] Weapon '{data.weaponName}' is missing a KINEMATION binding.");
                    continue;
                }

                var kinemationCameraParent = _weaponCamera != null
                    ? _weaponCamera.transform
                    : _fpCamera != null ? _fpCamera.transform : null;
                if(kinemationCameraParent == null) {
                    Debug.LogError("[WeaponManager] Missing both WeaponCamera and FpCamera. Cannot spawn KINEMATION viewmodel.");
                    continue;
                }

                var kinemationSwayHolder = new GameObject("SwayHolder");
                var kinemationSway = kinemationSwayHolder.AddComponent<WeaponSway>();
                kinemationSwayHolder.transform.SetParent(kinemationCameraParent, false);
                kinemationSwayHolder.transform.localPosition = Vector3.zero;
                kinemationSwayHolder.transform.localEulerAngles = Vector3.zero;
                if(_fpCamera != null) {
                    kinemationSway.SetCameraTransform(_fpCamera.transform);
                }

                var kinemationBobHolder = new GameObject("BobHolder");
                kinemationBobHolder.transform.SetParent(kinemationSwayHolder.transform, false);
                kinemationBobHolder.transform.localPosition = Vector3.zero;
                kinemationBobHolder.transform.localEulerAngles = Vector3.zero;
                if(UseLegacyBobOnKinemationViewmodel) {
                    var legacyBob = kinemationBobHolder.AddComponent<WeaponBob>();
                    legacyBob.ConfigureFeatures(
                        LegacyKinemationMovementBob,
                        LegacyKinemationIdleBreathBob,
                        LegacyKinemationJumpFallBob,
                        LegacyKinemationLandingBob
                    );
                }

                var kinemationHolder = new GameObject("KinemationHolder");
                kinemationHolder.transform.SetParent(kinemationBobHolder.transform, false);
                ResolveKinemationViewmodelPose(kinemationBinding, out var localPosition, out var localEulerAngles);
                kinemationHolder.transform.localPosition = localPosition;
                kinemationHolder.transform.localEulerAngles = localEulerAngles;

                var disableWeaponSounds = DisableKinemationGlobalSounds || DisableKinemationWeaponSounds;
                var disablePlayerSounds = DisableKinemationGlobalSounds || DisableKinemationPlayerSounds;

                var kinemationDriver = kinemationHolder.AddComponent<KinemationFpWeaponDriver>();
                kinemationDriver.Configure(
                    kinemationFpsPlayerPrefab,
                    kinemationBinding.kinemationWeaponPrefab,
                    disableWeaponSounds,
                    disablePlayerSounds,
                    RouteKinemationWeaponSoundEventsToAudioService,
                    SyncKinemationLookPitchWithPlayer,
                    SyncKinemationAirborneState,
                    FreezeKinemationLocomotionInAir,
                    ForceKinemationWalkAnimationWhileSprinting,
                    kinemationSprintWalkGaitValue,
                    kinemationEquipUnlockNormalizedTime
                );

                var fpLayer = GetFpWeaponLayer();
                SetGameObjectAndChildrenLayer(kinemationHolder, fpLayer);
                kinemationDriver.InitializeIfNeeded(fpLayer);
                SetupFpWeaponSkinnedMeshRenderers(kinemationHolder);

                kinemationHolder.SetActive(false);
                _fpWeaponInstances.Add(kinemationHolder);
                _weaponAmmo[i] = ResolveWeaponCapacity(data);
            }
        }

        private int ResolveIndexForSlot(int slot) {
            if(weaponDataList == null || weaponDataList.Count == 0) return 0;

            for(var i = 0; i < weaponDataList.Count; i++) {
                var data = weaponDataList[i];
                if(data == null) continue;
                if(ResolveWeaponSlot(data) == slot) {
                    return i;
                }
            }

            return Mathf.Clamp(slot, 0, weaponDataList.Count - 1);
        }

        private void ResolveCurrentWorldWeaponReference() {
            if(_worldWeaponSocket == null) return;
            if(CurrentWeaponIndex < 0 || CurrentWeaponIndex >= weaponDataList.Count) return;

            var data = weaponDataList[CurrentWeaponIndex];
            if(data == null) return;

            var worldObj = ResolveWorldWeaponObject(data);
            if(worldObj != null) {
                CurrentWorldWeaponInstance = worldObj;
            }
        }

        /// <summary>
        /// Called when weapon index NetworkVariables change.
        /// </summary>
        private void OnWeaponIndexChanged(int oldValue, int newValue) {
            if(_suppressLoadoutRebuildCallbacks) return;

            var shouldDeferTpReveal = playerController != null &&
                                      (playerController.NetIsDead is { Value: true } ||
                                       (playerController.PlayerRagdoll != null &&
                                        playerController.PlayerRagdoll.IsRagdoll));

            RebuildEquippedWeapons(
                preserveCurrentSlot: !shouldDeferTpReveal,
                deferTpRevealUntilRespawn: shouldDeferTpReveal
            );
        }

        /// <summary>
        /// Disables all world weapons that aren't in the player's equipped weapon list.
        /// Ensures only selected weapons are visible on the player model.
        /// </summary>
        private void DisableUnequippedWorldWeapons() {
            if(_worldWeaponSocket == null) return;

            // Collect all equipped world weapon objects from equipped WeaponData entries.
            var equippedWorldWeapons = new HashSet<GameObject>();
            if(weaponDataList != null) {
                foreach(var weaponData in weaponDataList) {
                    if(weaponData == null) continue;
                    var worldWeapon = ResolveWorldWeaponObject(weaponData);
                    if(worldWeapon != null) {
                        equippedWorldWeapons.Add(worldWeapon);
                    }
                }
            }

            // Disable all world weapons that aren't in the equipped list
            foreach(Transform child in _worldWeaponSocket) {
                if(child == null) continue;

                // Check if this weapon is in the equipped list
                var isEquipped = equippedWorldWeapons.Contains(child.gameObject);
                
                // Also check if it's the current world weapon (should be active)
                var isCurrentWeapon = CurrentWorldWeaponInstance != null && 
                                      CurrentWorldWeaponInstance == child.gameObject;
                
                // Disable if not equipped and not current weapon
                if(!isEquipped && !isCurrentWeapon) {
                    child.gameObject.SetActive(false);
                }
            }
        }

        private bool ValidateStrictEquippedWeaponConfiguration() {
            if(kinemationFpsPlayerPrefab == null) {
                Debug.LogError("[WeaponManager] Missing KINEMATION FPS player prefab.");
                return false;
            }

            if(_worldWeaponSocket == null) {
                Debug.LogError("[WeaponManager] Missing WorldWeaponSocket. Strict mode requires explicit WorldWeaponBinding objects.");
                return false;
            }

            if(weaponDataList == null || weaponDataList.Count == 0) {
                return false;
            }

            var isValid = true;
            foreach(var data in weaponDataList) {
                if(data == null) {
                    Debug.LogError("[WeaponManager] Equipped weapon data is null.");
                    isValid = false;
                    continue;
                }

                if(ResolveWeaponSlot(data) < 0) {
                    Debug.LogError($"[WeaponManager] Weapon '{data.weaponName}' has invalid slot assignment.");
                    isValid = false;
                }

                if(!TryGetKinemationBindingForData(data, out var binding) || binding == null ||
                   binding.kinemationWeaponPrefab == null) {
                    Debug.LogError($"[WeaponManager] Weapon '{data.weaponName}' is missing a KINEMATION binding/prefab.");
                    isValid = false;
                    continue;
                }

                if(ResolveKinemationWeaponCapacity(binding.kinemationWeaponPrefab) <= 0) {
                    Debug.LogError(
                        $"[WeaponManager] Weapon '{data.weaponName}' has invalid KINEMATION ammo capacity. " +
                        "Set FPSWeaponSettings.ammo > 0.");
                    isValid = false;
                }

                if(ResolveWorldWeaponObject(data) == null) {
                    Debug.LogError(
                        $"[WeaponManager] Weapon '{data.weaponName}' missing WorldWeaponBinding under WorldWeaponSocket.");
                    isValid = false;
                }
            }

            return isValid;
        }

        private void BuildEquippedWeaponList() {
            BuildKinemationWeaponLookup();
            weaponDataList = new List<WeaponData>();

            if(playerController == null) {
                Debug.LogError("[WeaponManager] Missing PlayerController while building equipped weapon list.");
                return;
            }

            var primaryIndex = playerController.primaryWeaponIndex.Value;
            var secondaryIndex = playerController.secondaryWeaponIndex.Value;

            var primary = GetWeaponFromOptions(_primaryWeaponOptions, primaryIndex, "Primary");
            if(primary != null) {
                weaponDataList.Add(primary);
            }

            var secondary = GetWeaponFromOptions(_secondaryWeaponOptions, secondaryIndex, "Secondary");
            if(secondary != null) {
                weaponDataList.Add(secondary);
            }
        }

        private static WeaponData GetWeaponFromOptions(List<WeaponData> options, int storedIndex, string slotLabel) {
            if(options == null || options.Count == 0) {
                Debug.LogError($"[WeaponManager] No {slotLabel} weapon options assigned.");
                return null;
            }

            if(storedIndex < 0 || storedIndex >= options.Count) {
                Debug.LogError(
                    $"[WeaponManager] {slotLabel} weapon index {storedIndex} out of range [0..{options.Count - 1}].");
                return null;
            }

            var weaponData = options[storedIndex];
            if(weaponData == null) {
                Debug.LogError($"[WeaponManager] {slotLabel} weapon at index {storedIndex} is null.");
            }

            return weaponData;
        }

        private bool TryConsumeWeaponSwitchQuota() {
            var config = AntiCheatConfig.Instance;
            if(config == null) return true;
            if(RpcRateLimiter.TryConsume(OwnerClientId, RpcRateLimiter.Keys.WeaponSwitch, config.weaponSwitchLimit,
                    config.rpcWindowSeconds)) {
                return true;
            }

            AntiCheatLogger.LogRateLimit(OwnerClientId, RpcRateLimiter.Keys.WeaponSwitch);
            return false;
        }

        private ServerWeaponState GetOrCreateServerState(int weaponIndex) {
            if(_serverWeaponStates.TryGetValue(weaponIndex, out var state)) return state;
            state = new ServerWeaponState();
            var data = GetWeaponDataByIndex(weaponIndex);
            state.ServerAmmo = data != null ? ResolveWeaponCapacity(data) : 0;
            _serverWeaponStates[weaponIndex] = state;
            return state;
        }

        public bool ValidateServerShot(int weaponIndex, ulong shotId, out string reason) {
            reason = null;
            if(!IsServer) return true;
            var data = GetWeaponDataByIndex(weaponIndex);
            if(data == null) {
                reason = "unknown weapon";
                return false;
            }

            var state = GetOrCreateServerState(weaponIndex);
            if(shotId == state.LastShotId) {
                return true;
            }

            if(shotId < state.LastShotId) {
                reason = "shot id rewind";
                return false;
            }

            var config = AntiCheatConfig.Instance;
            var now = Time.time;
            var grace = config != null ? config.fireRateGraceSeconds : 0f;
            if(state.LastShotTime > 0f) {
                var minInterval = Mathf.Max(0.01f, data.fireRate - grace);
                if(now - state.LastShotTime < minInterval) {
                    reason = "firing too fast";
                    return false;
                }
            }

            if(state.ServerAmmo <= 0) {
                reason = "no ammo";
                return false;
            }

            state.ServerAmmo = Mathf.Max(0, state.ServerAmmo - 1);
            state.LastShotTime = now;
            state.LastShotId = shotId;
            return true;
        }

        public void ReportAmmoSync(int weaponIndex, int newAmmo) {
            if(!IsServer) {
                ReportAmmoSyncServerRpc(weaponIndex, newAmmo);
                return;
            }

            UpdateServerAmmo(weaponIndex, newAmmo);
        }

        [Rpc(SendTo.Server)]
        private void ReportAmmoSyncServerRpc(int weaponIndex, int newAmmo) {
            UpdateServerAmmo(weaponIndex, newAmmo);
        }

        private void UpdateServerAmmo(int weaponIndex, int ammo) {
            if(!IsServer) return;
            var data = GetWeaponDataByIndex(weaponIndex);
            if(data == null) return;
            var magCapacity = ResolveWeaponCapacity(data);
            var clamped = Mathf.Clamp(ammo, 0, magCapacity);
            var state = GetOrCreateServerState(weaponIndex);
            state.ServerAmmo = clamped;
        }
    }
}
