using System;
using System.Collections;
using System.Collections.Generic;
using Game.Player;
using Game.Menu;
using Game.Settings;
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
            public bool useCustomWristCorrectionWeight;
            [Range(0f, 1f)] public float wristCorrectionWeight = 0.25f;
        }

        [Serializable]
        private struct WristDebugEulerTuning {
            [Range(-90f, 90f)] public float x;
            [Range(-90f, 90f)] public float y;
            [Range(-90f, 90f)] public float z;

            public WristDebugEulerTuning(float x, float y, float z) {
                this.x = x;
                this.y = y;
                this.z = z;
            }

            public Vector3 ToVector3() {
                return new Vector3(x, y, z);
            }
        }

        [Serializable]
        private struct WristDebugPositionTuning {
            [Range(-0.25f, 0.25f)] public float x;
            [Range(-0.25f, 0.25f)] public float y;
            [Range(-0.25f, 0.25f)] public float z;

            public WristDebugPositionTuning(float x, float y, float z) {
                this.x = x;
                this.y = y;
                this.z = z;
            }

            public Vector3 ToVector3() {
                return new Vector3(x, y, z);
            }
        }

        [SerializeField] private PlayerController playerController;
        private CinemachineCamera _fpCamera;
        private Camera _weaponCamera;
        private Transform _worldWeaponSocket;
        private Animator _playerAnimator;
        private PlayerRenderer _playerRenderer;

        [Header("Loadout Weapon Pools")]
        [SerializeField] private List<WeaponData> primaryWeaponOptions = new();

        [SerializeField] private List<WeaponData> secondaryWeaponOptions = new();

        [Header("Weapon System")]
        [SerializeField, HideInInspector] private List<WeaponData> weaponDataList = new();
        [Header("Holstered Weapon Models")]
        [Tooltip("Explicit holstered primary weapon objects. Required for primary holster display.")]
        [SerializeField] private List<GameObject> primaryHolsteredWeapons = new();
        [Tooltip("Explicit holstered secondary weapon objects. Required for secondary holster display.")]
        [SerializeField] private List<GameObject> secondaryHolsteredWeapons = new();

        [Header("KINEMATION FP Integration")]
        [SerializeField] private GameObject kinemationFpsPlayerPrefab;
        [SerializeField] private List<KinemationWeaponBinding> kinemationWeaponBindings = new();
        [SerializeField] private bool disableKinemationSounds = true;
        [SerializeField] private bool disableKinemationWeaponSounds;
        [SerializeField] private bool disableKinemationPlayerSounds = true;
        [SerializeField] private bool routeKinemationWeaponSoundEventsToAudioService = true;
        [SerializeField] private bool syncKinemationLookPitchWithPlayer;
        [SerializeField] private bool syncKinemationAirborneState;
        [SerializeField] private bool freezeKinemationLocomotionInAir = true;
        [SerializeField] private bool forceKinemationWalkAnimationWhileSprinting = true;
        [SerializeField, Range(0f, 1.99f)] private float kinemationSprintWalkGaitValue = 1.2f;
        [SerializeField] private bool useLegacyBobOnKinemationViewmodel = true;
        [SerializeField] private bool legacyKinemationMovementBob;
        [SerializeField] private bool legacyKinemationIdleBreathBob;
        [SerializeField] private bool legacyKinemationJumpFallBob = true;
        [SerializeField] private bool legacyKinemationLandingBob = true;
        [SerializeField] private bool tagKinemationArmsForLegacyHooks;
        [SerializeField] private bool requireKinemationEquipCompleteEvent = true;
        [SerializeField, Range(0f, 1f)] private float kinemationEquipUnlockNormalizedTime = 0.82f;
        [SerializeField] private bool autoCompleteKinemationPullOut = true;
        [SerializeField, Min(0f)] private float kinemationPullOutCompleteDelay = 0.12f;
        [SerializeField] private bool enableKinemationWristCorrectionLayer;
        [SerializeField] private string kinemationWristCorrectionLayerName = "WristCorrection";
        [SerializeField, Range(0f, 1f)] private float kinemationWristCorrectionLayerWeight = 0.25f;
        [SerializeField] private bool logMissingKinemationWristCorrectionLayer = true;
        [SerializeField] private bool enableKinemationRuntimeWristDebugOverride;
        [SerializeField, Range(0f, 1f)] private float kinemationRuntimeWristDebugWeight = 1f;
        [SerializeField] private WristDebugEulerTuning kinemationRuntimeWristDebugUpperarmLeftEuler =
            new WristDebugEulerTuning(0f, 0f, 0f);
        [SerializeField] private WristDebugEulerTuning kinemationRuntimeWristDebugUpperarmRightEuler =
            new WristDebugEulerTuning(0f, 0f, 0f);
        [SerializeField] private WristDebugPositionTuning kinemationRuntimeWristDebugUpperarmLeftPosition =
            new WristDebugPositionTuning(0f, 0f, 0f);
        [SerializeField] private WristDebugPositionTuning kinemationRuntimeWristDebugUpperarmRightPosition =
            new WristDebugPositionTuning(0f, 0f, 0f);
        [SerializeField] private WristDebugEulerTuning kinemationRuntimeWristDebugLowerarmLeftEuler =
            new WristDebugEulerTuning(-35f, 0f, 0f);
        [SerializeField] private WristDebugEulerTuning kinemationRuntimeWristDebugLowerarmRightEuler =
            new WristDebugEulerTuning(-35f, 0f, 0f);
        [SerializeField] private WristDebugPositionTuning kinemationRuntimeWristDebugLowerarmLeftPosition =
            new WristDebugPositionTuning(0f, 0f, 0f);
        [SerializeField] private WristDebugPositionTuning kinemationRuntimeWristDebugLowerarmRightPosition =
            new WristDebugPositionTuning(0f, 0f, 0f);
        [SerializeField] private WristDebugEulerTuning kinemationRuntimeWristDebugTwistLeftEuler =
            new WristDebugEulerTuning(-20f, 0f, 0f);
        [SerializeField] private WristDebugEulerTuning kinemationRuntimeWristDebugTwistRightEuler =
            new WristDebugEulerTuning(-20f, 0f, 0f);
        [SerializeField] private WristDebugPositionTuning kinemationRuntimeWristDebugTwistLeftPosition =
            new WristDebugPositionTuning(0f, 0f, 0f);
        [SerializeField] private WristDebugPositionTuning kinemationRuntimeWristDebugTwistRightPosition =
            new WristDebugPositionTuning(0f, 0f, 0f);
        [SerializeField] private WristDebugEulerTuning kinemationRuntimeWristDebugHandLeftEuler =
            new WristDebugEulerTuning(-10f, 0f, 0f);
        [SerializeField] private WristDebugEulerTuning kinemationRuntimeWristDebugHandRightEuler =
            new WristDebugEulerTuning(-10f, 0f, 0f);
        [SerializeField] private WristDebugPositionTuning kinemationRuntimeWristDebugHandLeftPosition =
            new WristDebugPositionTuning(0f, 0f, 0f);
        [SerializeField] private WristDebugPositionTuning kinemationRuntimeWristDebugHandRightPosition =
            new WristDebugPositionTuning(0f, 0f, 0f);
        [SerializeField] private bool kinemationRuntimeWristDebugPreserveHandGrip = true;
        [SerializeField] private bool kinemationRuntimeWristDebugApplyHandOffsetWhenPreservingGrip;
        [SerializeField] private bool logMissingKinemationRuntimeWristBones = true;
        [SerializeField] private Vector3 kinemationViewmodelLocalPosition = Vector3.zero;
        [SerializeField] private Vector3 kinemationViewmodelLocalEulerAngles = Vector3.zero;

        private readonly List<GameObject> _fpWeaponInstances = new();
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
        public IReadOnlyList<WeaponData> PrimaryWeaponOptions => primaryWeaponOptions;
        public IReadOnlyList<WeaponData> SecondaryWeaponOptions => secondaryWeaponOptions;
        public bool IsPullingOut { get; private set; }

        private static readonly int PullOutHash = Animator.StringToHash("PullOut");
        private static readonly int WeaponIndexHash = Animator.StringToHash("WeaponIndex");
        private readonly Dictionary<string, GameObject> _primaryHolsterLookup = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, GameObject> _secondaryHolsterLookup = new(StringComparer.OrdinalIgnoreCase);
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
            if(_playerRenderer != null) return;
            // PlayerRenderer not found - event already published by GetComponentSafe if used
            enabled = false;
        }

        private void Update() {
            UpdateKinemationEquipCompletionGate();
            SyncKinemationRuntimeWristDebugSettings();
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

        private void SyncKinemationRuntimeWristDebugSettings() {
            if(_fpWeaponInstances.Count == 0) return;

            var upperarmLeft = kinemationRuntimeWristDebugUpperarmLeftEuler.ToVector3();
            var upperarmRight = kinemationRuntimeWristDebugUpperarmRightEuler.ToVector3();
            var upperarmLeftPosition = kinemationRuntimeWristDebugUpperarmLeftPosition.ToVector3();
            var upperarmRightPosition = kinemationRuntimeWristDebugUpperarmRightPosition.ToVector3();
            var lowerarmLeft = kinemationRuntimeWristDebugLowerarmLeftEuler.ToVector3();
            var lowerarmRight = kinemationRuntimeWristDebugLowerarmRightEuler.ToVector3();
            var lowerarmLeftPosition = kinemationRuntimeWristDebugLowerarmLeftPosition.ToVector3();
            var lowerarmRightPosition = kinemationRuntimeWristDebugLowerarmRightPosition.ToVector3();
            var twistLeft = kinemationRuntimeWristDebugTwistLeftEuler.ToVector3();
            var twistRight = kinemationRuntimeWristDebugTwistRightEuler.ToVector3();
            var twistLeftPosition = kinemationRuntimeWristDebugTwistLeftPosition.ToVector3();
            var twistRightPosition = kinemationRuntimeWristDebugTwistRightPosition.ToVector3();
            var handLeft = kinemationRuntimeWristDebugHandLeftEuler.ToVector3();
            var handRight = kinemationRuntimeWristDebugHandRightEuler.ToVector3();
            var handLeftPosition = kinemationRuntimeWristDebugHandLeftPosition.ToVector3();
            var handRightPosition = kinemationRuntimeWristDebugHandRightPosition.ToVector3();

            for(var i = 0; i < _fpWeaponInstances.Count; i++) {
                var fpWeaponRoot = _fpWeaponInstances[i];
                if(!TryGetKinemationDriver(fpWeaponRoot, out var kinemationDriver) || kinemationDriver == null) {
                    continue;
                }

                kinemationDriver.ApplyRuntimeWristDebugSettings(
                    enableKinemationRuntimeWristDebugOverride,
                    kinemationRuntimeWristDebugWeight,
                    upperarmLeft,
                    upperarmRight,
                    upperarmLeftPosition,
                    upperarmRightPosition,
                    lowerarmLeft,
                    lowerarmRight,
                    lowerarmLeftPosition,
                    lowerarmRightPosition,
                    twistLeft,
                    twistRight,
                    twistLeftPosition,
                    twistRightPosition,
                    handLeft,
                    handRight,
                    handLeftPosition,
                    handRightPosition,
                    kinemationRuntimeWristDebugPreserveHandGrip,
                    kinemationRuntimeWristDebugApplyHandOffsetWhenPreservingGrip,
                    logMissingKinemationRuntimeWristBones
                );
            }
        }

        private void BuildKinemationWeaponLookup() {
            _kinemationWeaponLookup.Clear();
            if(kinemationWeaponBindings == null || kinemationWeaponBindings.Count == 0) return;

            foreach(var binding in kinemationWeaponBindings) {
                if(binding == null || binding.weaponData == null || binding.kinemationWeaponPrefab == null) continue;
                _kinemationWeaponLookup[binding.weaponData] = binding;
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

            var legacyCapacity = Mathf.Max(1, data.magSize);
            if(!TryGetKinemationBindingForData(data, out var kinemationBinding)) {
                return legacyCapacity;
            }

            var kinemationCapacity = ResolveKinemationWeaponCapacity(kinemationBinding.kinemationWeaponPrefab);
            return kinemationCapacity > 0 ? kinemationCapacity : legacyCapacity;
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

            // Fallback: if KIN world objects don't have explicit WorldWeaponBinding,
            // resolve by matching child names against current loadout weapon identifiers.
            if(weaponDataList == null || weaponDataList.Count == 0) return;

            foreach(var data in weaponDataList) {
                if(data == null || _worldWeaponByData.ContainsKey(data)) continue;
                if(TryResolveWorldWeaponByName(data, out var worldWeaponObject)) {
                    _worldWeaponByData[data] = worldWeaponObject;
                }
            }
        }

        private bool TryResolveWorldWeaponByName(WeaponData data, out GameObject worldWeaponObject) {
            worldWeaponObject = null;
            if(data == null || _worldWeaponSocket == null) return false;

            var candidateNames = BuildWeaponNameCandidates(data);
            if(candidateNames.Count == 0) return false;

            foreach(Transform child in _worldWeaponSocket) {
                if(child == null) continue;
                var childName = NormalizeHolsterKey(child.name);
                if(string.IsNullOrEmpty(childName)) continue;

                foreach(var candidateName in candidateNames) {
                    if(childName != candidateName) continue;
                    worldWeaponObject = child.gameObject;
                    return true;
                }
            }

            return false;
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
            
            // For non-owners, check if NetworkVariables are still at default (might not be synced yet)
            if(!IsOwner && playerController != null) {
                var primaryIndex = playerController.primaryWeaponIndex.Value;
                var secondaryIndex = playerController.secondaryWeaponIndex.Value;
                
                // If both are 0, and we have weapon options, might be unsynced - wait for sync
                if(primaryIndex == 0 && secondaryIndex == 0 && 
                   primaryWeaponOptions is { Count: > 0 }) {
                    // Don't initialize yet - wait for NetworkVariables to sync
                    // OnWeaponIndexChanged will handle initialization when values arrive
                    return;
                }
            }
            
            SetupHolsteredWeaponModels();
            BuildWorldWeaponLookup();
            BuildKinemationWeaponLookup();
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
                requireKinemationEquipCompleteEvent &&
                fp != null &&
                TryGetKinemationDriver(fp, out _);

            // Prepare new 3P weapon but DON'T show it yet - wait for animation event
            QueuePendingTpWeapon(data);

            // Restore ammo (fallback to mag size if somehow missing)
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
            if(playerController == null || primaryWeaponOptions == null || primaryWeaponOptions.Count == 0) {
                return 0;
            }

            return Mathf.Clamp(playerController.primaryWeaponIndex.Value, 0, primaryWeaponOptions.Count - 1);
        }

        public int GetSecondarySelectionIndex() {
            if(playerController == null || secondaryWeaponOptions == null || secondaryWeaponOptions.Count == 0) {
                return 0;
            }

            return Mathf.Clamp(playerController.secondaryWeaponIndex.Value, 0, secondaryWeaponOptions.Count - 1);
        }

        public bool ApplyOwnerLoadoutSelection(int primaryIndex, int secondaryIndex,
            bool deferTpRevealUntilRespawn = true) {
            if(!IsOwner || playerController == null) return false;

            var clampedPrimary = ClampOptionIndex(primaryWeaponOptions, primaryIndex);
            var clampedSecondary = ClampOptionIndex(secondaryWeaponOptions, secondaryIndex);

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
            _primaryHolsterLookup.Clear();
            _secondaryHolsterLookup.Clear();

            BuildHolsterLookup(primaryHolsteredWeapons, _primaryHolsterLookup);
            BuildHolsterLookup(secondaryHolsteredWeapons, _secondaryHolsterLookup);

            PrimaryHolster = ResolveHolsterForSlot(0, _primaryHolsterLookup);
            SecondaryHolster = ResolveHolsterForSlot(1, _secondaryHolsterLookup);

            if(PrimaryHolster == null) {
                PrimaryHolster = ResolveHolsterForSlotFallback(0);
                RegisterHolsterObject(_primaryHolsterLookup, PrimaryHolster);
            }

            if(SecondaryHolster == null) {
                SecondaryHolster = ResolveHolsterForSlotFallback(1);
                RegisterHolsterObject(_secondaryHolsterLookup, SecondaryHolster);
            }

            DisableHolster(PrimaryHolster);
            DisableHolster(SecondaryHolster);
        }

        private static void BuildHolsterLookup(IEnumerable<GameObject> overrides, Dictionary<string, GameObject> lookup) {
            if(overrides == null) return;

            foreach(var go in overrides) {
                RegisterHolsterObject(lookup, go);
            }
        }

        private static void RegisterHolsterObject(IDictionary<string, GameObject> lookup, GameObject go) {
            if(go == null) return;
            var key = NormalizeHolsterKey(go.name);
            if(string.IsNullOrEmpty(key)) return;

            if(go.activeSelf) {
                go.SetActive(false);
            }

            lookup[key] = go;
        }

        private GameObject ResolveHolsterForSlot(int slot, Dictionary<string, GameObject> lookup) {
            var weaponData = GetWeaponDataForSlot(slot);
            return ResolveHolsterObject(weaponData, lookup);
        }

        private WeaponData GetWeaponDataForSlot(int slot) {
            if(weaponDataList == null || weaponDataList.Count == 0) return null;
            for(var i = 0; i < weaponDataList.Count; i++) {
                var data = weaponDataList[i];
                if(data == null) continue;
                var weaponSlot = ResolveWeaponSlot(data, i);
                if(weaponSlot == slot) {
                    return data;
                }
            }

            return slot switch {
                0 when weaponDataList.Count > 0 => weaponDataList[0],
                1 when weaponDataList.Count > 1 => weaponDataList[1],
                _ => null
            };
        }

        private static int ResolveWeaponSlot(WeaponData data, int fallback) {
            if(data == null) return fallback;
            var slot = data.WeaponSlotIndex;
            return slot >= 0 ? slot : fallback;
        }

        private GameObject ResolveHolsterObject(WeaponData data, Dictionary<string, GameObject> lookup) {
            if(data == null || lookup == null || lookup.Count == 0) return null;

            var names = BuildWeaponNameCandidates(data);
            if(names.Count == 0) return null;

            foreach(var candidate in names) {
                if(lookup.TryGetValue(candidate, out var go)) {
                    return go;
                }
            }

            return null;
        }

        private GameObject ResolveHolsterForSlotFallback(int slot) {
            var weaponData = GetWeaponDataForSlot(slot);
            if(weaponData == null || playerController == null) return null;

            var candidateNames = BuildWeaponNameCandidates(weaponData);
            if(candidateNames.Count == 0) return null;

            var playerRoot = playerController.transform;
            if(playerRoot == null) return null;

            var currentWorldWeapon = ResolveWorldWeaponObject(weaponData);
            var allTransforms = playerRoot.GetComponentsInChildren<Transform>(true);
            foreach(var t in allTransforms) {
                if(t == null) continue;
                if(t == playerRoot) continue;
                if(_worldWeaponSocket != null && t.IsChildOf(_worldWeaponSocket)) continue;
                if(_fpCamera != null && t.IsChildOf(_fpCamera.transform)) continue;
                if(_weaponCamera != null && t.IsChildOf(_weaponCamera.transform)) continue;

                var go = t.gameObject;
                if(go == null) continue;
                if(currentWorldWeapon != null && go == currentWorldWeapon) continue;
                if(go.GetComponentInChildren<Renderer>(true) == null) continue;

                var normalizedName = NormalizeHolsterKey(go.name);
                if(string.IsNullOrEmpty(normalizedName)) continue;

                foreach(var candidateName in candidateNames) {
                    if(normalizedName != candidateName) continue;
                    return go;
                }
            }

            return null;
        }

        private List<string> BuildWeaponNameCandidates(WeaponData data) {
            var names = new List<string>(3);
            if(data == null) return names;

            var resolvedWorld = _worldWeaponByData.TryGetValue(data, out var worldWeapon) ? worldWeapon : null;
            if(resolvedWorld != null && !string.IsNullOrEmpty(resolvedWorld.name)) {
                var key = NormalizeHolsterKey(resolvedWorld.name);
                if(!string.IsNullOrEmpty(key)) {
                    names.Add(key);
                }
            }

            if(TryGetKinemationBindingForData(data, out var kinemationBinding) &&
               kinemationBinding != null &&
               kinemationBinding.kinemationWeaponPrefab != null &&
               !string.IsNullOrEmpty(kinemationBinding.kinemationWeaponPrefab.name)) {
                var key = NormalizeHolsterKey(kinemationBinding.kinemationWeaponPrefab.name);
                if(!string.IsNullOrEmpty(key) && !names.Contains(key)) {
                    names.Add(key);
                }
            }

            if(!string.IsNullOrEmpty(data.weaponName)) {
                var key = NormalizeHolsterKey(data.weaponName);
                if(!string.IsNullOrEmpty(key) && !names.Contains(key)) {
                    names.Add(key);
                }
            }

            return names;
        }

        private static string NormalizeHolsterKey(string value) {
            return string.IsNullOrEmpty(value) ? null : value.Replace("(Clone)", "").Trim().ToLowerInvariant();
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
            return ResolveWeaponSlot(data, index);
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
            SetupHolsteredWeaponModels();
            BuildWorldWeaponLookup();
            BuildKinemationWeaponLookup();
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
                if(useLegacyBobOnKinemationViewmodel) {
                    var legacyBob = kinemationBobHolder.AddComponent<WeaponBob>();
                    legacyBob.ConfigureFeatures(
                        legacyKinemationMovementBob,
                        legacyKinemationIdleBreathBob,
                        legacyKinemationJumpFallBob,
                        legacyKinemationLandingBob
                    );
                }

                var kinemationHolder = new GameObject("KinemationHolder");
                kinemationHolder.transform.SetParent(kinemationBobHolder.transform, false);
                ResolveKinemationViewmodelPose(kinemationBinding, out var localPosition, out var localEulerAngles);
                kinemationHolder.transform.localPosition = localPosition;
                kinemationHolder.transform.localEulerAngles = localEulerAngles;

                var disableWeaponSounds = disableKinemationSounds || disableKinemationWeaponSounds;
                var disablePlayerSounds = disableKinemationSounds || disableKinemationPlayerSounds;
                var wristCorrectionWeight = kinemationBinding.useCustomWristCorrectionWeight
                    ? kinemationBinding.wristCorrectionWeight
                    : kinemationWristCorrectionLayerWeight;

                var kinemationDriver = kinemationHolder.AddComponent<KinemationFpWeaponDriver>();
                kinemationDriver.Configure(
                    kinemationFpsPlayerPrefab,
                    kinemationBinding.kinemationWeaponPrefab,
                    disableWeaponSounds,
                    disablePlayerSounds,
                    routeKinemationWeaponSoundEventsToAudioService,
                    tagKinemationArmsForLegacyHooks,
                    syncKinemationLookPitchWithPlayer,
                    syncKinemationAirborneState,
                    freezeKinemationLocomotionInAir,
                    forceKinemationWalkAnimationWhileSprinting,
                    kinemationSprintWalkGaitValue,
                    kinemationEquipUnlockNormalizedTime,
                    enableKinemationWristCorrectionLayer,
                    kinemationWristCorrectionLayerName,
                    wristCorrectionWeight,
                    logMissingKinemationWristCorrectionLayer,
                    enableKinemationRuntimeWristDebugOverride,
                    kinemationRuntimeWristDebugWeight,
                    kinemationRuntimeWristDebugUpperarmLeftEuler.ToVector3(),
                    kinemationRuntimeWristDebugUpperarmRightEuler.ToVector3(),
                    kinemationRuntimeWristDebugUpperarmLeftPosition.ToVector3(),
                    kinemationRuntimeWristDebugUpperarmRightPosition.ToVector3(),
                    kinemationRuntimeWristDebugLowerarmLeftEuler.ToVector3(),
                    kinemationRuntimeWristDebugLowerarmRightEuler.ToVector3(),
                    kinemationRuntimeWristDebugLowerarmLeftPosition.ToVector3(),
                    kinemationRuntimeWristDebugLowerarmRightPosition.ToVector3(),
                    kinemationRuntimeWristDebugTwistLeftEuler.ToVector3(),
                    kinemationRuntimeWristDebugTwistRightEuler.ToVector3(),
                    kinemationRuntimeWristDebugTwistLeftPosition.ToVector3(),
                    kinemationRuntimeWristDebugTwistRightPosition.ToVector3(),
                    kinemationRuntimeWristDebugHandLeftEuler.ToVector3(),
                    kinemationRuntimeWristDebugHandRightEuler.ToVector3(),
                    kinemationRuntimeWristDebugHandLeftPosition.ToVector3(),
                    kinemationRuntimeWristDebugHandRightPosition.ToVector3(),
                    kinemationRuntimeWristDebugPreserveHandGrip,
                    kinemationRuntimeWristDebugApplyHandOffsetWhenPreservingGrip,
                    logMissingKinemationRuntimeWristBones
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
                if(ResolveWeaponSlot(data, i) == slot) {
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

            // Also include holstered weapon names (for legacy holster objects living under socket).
            var equippedWorldWeaponNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if(PrimaryHolster != null) {
                equippedWorldWeaponNames.Add(PrimaryHolster.name);
            }
            if(SecondaryHolster != null) {
                equippedWorldWeaponNames.Add(SecondaryHolster.name);
            }
            
            // Disable all world weapons that aren't in the equipped list
            foreach(Transform child in _worldWeaponSocket) {
                if(child == null) continue;
                
                var weaponName = child.name;
                var normalizedName = NormalizeHolsterKey(weaponName);
                
                // Check if this weapon is in the equipped list
                var isEquipped = equippedWorldWeapons.Contains(child.gameObject) ||
                                 equippedWorldWeaponNames.Contains(weaponName) ||
                                 (!string.IsNullOrEmpty(normalizedName) && equippedWorldWeaponNames.Contains(normalizedName));
                
                // Also check if it's the current world weapon (should be active)
                var isCurrentWeapon = CurrentWorldWeaponInstance != null && 
                                      CurrentWorldWeaponInstance == child.gameObject;
                
                // Disable if not equipped and not current weapon
                if(!isEquipped && !isCurrentWeapon) {
                    child.gameObject.SetActive(false);
                }
            }
        }

        private void BuildEquippedWeaponList() {
            weaponDataList = new List<WeaponData>();

            // Get weapon indices from NetworkVariables (synced across all clients)
            // For owner, these are set from PlayerPrefs in OnNetworkSpawn
            // For non-owners, these come from the NetworkVariables
            int primaryIndex;
            int secondaryIndex;
            
            if(playerController != null) {
                primaryIndex = playerController.primaryWeaponIndex.Value;
                secondaryIndex = playerController.secondaryWeaponIndex.Value;
            } else {
                // Use local settings if PlayerController not available (shouldn't happen)
                var p = GameSettings.Data.player;
                primaryIndex = p.primaryWeaponIndex;
                secondaryIndex = p.secondaryWeaponIndex;
            }

            var primary = GetWeaponFromOptions(primaryWeaponOptions, primaryIndex, "Primary");
            if(primary != null) {
                weaponDataList.Add(primary);
            }

            var secondary = GetWeaponFromOptions(secondaryWeaponOptions, secondaryIndex, "Secondary");
            if(secondary != null) {
                weaponDataList.Add(secondary);
            }
        }

        private static WeaponData GetWeaponFromOptions(List<WeaponData> options, int storedIndex, string slotLabel) {
            if(options == null || options.Count == 0) {
                Debug.LogWarning($"[WeaponManager] No {slotLabel} weapon options assigned.");
                return null;
            }

            var clampedIndex = Mathf.Clamp(storedIndex, 0, options.Count - 1);
            if(clampedIndex != storedIndex) {
                Debug.LogWarning(
                    $"[WeaponManager] {slotLabel} weapon index {storedIndex} out of range. Using {clampedIndex} instead.");
                var p = GameSettings.Data.player;
                switch(slotLabel) {
                    case "Primary":
                        p.primaryWeaponIndex = clampedIndex;
                        GameSettings.Save();
                        break;
                    case "Secondary":
                        p.secondaryWeaponIndex = clampedIndex;
                        GameSettings.Save();
                        break;
                }
            }

            var weaponData = options[clampedIndex];
            if(weaponData == null) {
                Debug.LogWarning($"[WeaponManager] {slotLabel} weapon at index {clampedIndex} == null.");
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
