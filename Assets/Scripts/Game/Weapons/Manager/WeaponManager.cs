using System;
using System.Collections.Generic;
using Game.Match;
using Game.Player;
using Game.Player.Core;
using KINEMATION.FPSAnimationPack.Scripts.Weapon;
using Network.Diagnostics;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Weapons {
    public partial class WeaponManager : NetworkBehaviour {
        [Serializable]
        internal class KinemationWeaponBinding {
            public WeaponData weaponData;
            public GameObject kinemationWeaponPrefab;
            public bool useCustomViewmodelPose;
            public Vector3 viewmodelLocalPosition = Vector3.zero;
            public Vector3 viewmodelLocalEulerAngles = Vector3.zero;
            [Tooltip("Optional. Per-weapon grapple animation clip (e.g. A_FP_DGL_Grapple). If unset, controller default is used.")]
            public AnimationClip grappleClip;
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
        [SerializeField, Min(0f)] private float postMatchPullOutFailSafeDelay = 0.65f;
        [SerializeField] private Vector3 kinemationViewmodelLocalPosition = Vector3.zero;
        [SerializeField] private Vector3 kinemationViewmodelLocalEulerAngles = Vector3.zero;

        private readonly List<GameObject> _fpWeaponInstances = new();
        private readonly WeaponKinemationBindingCatalog _kinemationCatalog = new();
        private readonly WeaponAmmoAuthority _ammoAuthority = new();
        private readonly WeaponWorldWeaponRegistry _worldWeaponRegistry = new();
        private GameObject _pendingTpWeapon; // Track pending TP weapon to show via animation event
        private int _serverAuthoritativeWeaponIndex = -1;
        private int _serverReloadWeaponIndex = -1;
        private float _serverPullOutBlockedUntilTime;
        private MatchPlayerStateProxy _cachedPlayerState;
        private MatchPlayerStateProxy _boundPlayerState;
        private int _lastApprovedWeaponIndex = -1;
        private int _pendingPredictedWeaponIndex = -1;

        public Weapon CurrentWeapon { get; private set; }
        public GameObject CurrentWorldWeaponInstance { get; private set; }

        public int CurrentWeaponIndex { get; private set; } = -1;

        public int WeaponCount => weaponDataList.Count;
        public IReadOnlyList<WeaponData> PrimaryWeaponOptions => _kinemationCatalog.PrimaryWeaponOptions;
        public IReadOnlyList<WeaponData> SecondaryWeaponOptions => _kinemationCatalog.SecondaryWeaponOptions;
        public bool IsPullingOut { get; private set; }

        private static readonly int PullOutHash = Animator.StringToHash("PullOut");
        private static readonly int WeaponIndexHash = Animator.StringToHash("WeaponIndex");
        public GameObject PrimaryHolster { get; private set; }

        public GameObject SecondaryHolster { get; private set; }

        private int _pendingHolsterHideSlot = -1;
        private bool _suppressLoadoutRebuildCallbacks;
        private bool _deferTpRevealUntilRespawn;
        private GameObject _deferredRespawnWorldWeapon;
        private Coroutine _kinemationPullOutCompletionCoroutine;
        private bool _requiresKinemationEquipCompleteForCurrentPullOut;
        private bool _hasLoggedStrictStartupValidation;
        private bool _weaponsInitialized;
        private static readonly NetworkVariable<int> MissingEquippedWeaponIndexState = new(-1);

        private void Awake() {
            ValidateComponents();
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            MatchPlayerStateProxy.StateRegistered -= OnPlayerStateRegistered;
            MatchPlayerStateProxy.StateRegistered += OnPlayerStateRegistered;
            MatchPlayerStateProxy.StateUnregistered -= OnPlayerStateUnregistered;
            MatchPlayerStateProxy.StateUnregistered += OnPlayerStateUnregistered;
            TryBindPlayerStateSubscriptions();
        }

        public override void OnNetworkDespawn() {
            UnbindPlayerStateSubscriptions();
            MatchPlayerStateProxy.StateRegistered -= OnPlayerStateRegistered;
            MatchPlayerStateProxy.StateUnregistered -= OnPlayerStateUnregistered;
            _weaponsInitialized = false;
            base.OnNetworkDespawn();
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
            EnsureFpWeaponLightingRig();

            if((Time.frameCount & 7) == 0) {
                ReconcileStableTpWeaponState();
            }
        }

        private void BuildKinemationWeaponLookup() {
            _kinemationCatalog.Rebuild(
                kinemationWeaponBindings,
                ResolveWeaponSlot,
                Debug.LogError
            );
        }

        private bool TryGetKinemationBindingForData(WeaponData data, out KinemationWeaponBinding kinemationBinding) {
            kinemationBinding = null;
            if(_kinemationCatalog.IsEmpty) {
                BuildKinemationWeaponLookup();
            }

            return _kinemationCatalog.TryGetBinding(kinemationFpsPlayerPrefab, data, out kinemationBinding);
        }

        private static int ResolveKinemationWeaponCapacity(GameObject kinemationWeaponPrefab) {
            if(kinemationWeaponPrefab == null) return 0;
            var fpsWeapon = kinemationWeaponPrefab.GetComponentInChildren<FPSWeapon>(true);
            if(fpsWeapon == null || fpsWeapon.weaponSettings == null) return 0;
            return Mathf.Max(1, fpsWeapon.weaponSettings.ammo);
        }

        private int ResolveWeaponCapacity(WeaponData data) {
            if(data == null) return 0;

            if(!TryGetKinemationBindingForData(data, out var kinemationBinding)) {
                Debug.LogError(
                    $"[WeaponManager] Missing KINEMATION binding for '{data.weaponName}'. " +
                    "Strict mode requires a KIN binding for every equipped weapon.");
                return 0;
            }

            var kinemationCapacity = ResolveKinemationWeaponCapacity(kinemationBinding.kinemationWeaponPrefab);
            if(kinemationCapacity > 0) return kinemationCapacity;
            Debug.LogError(
                $"[WeaponManager] Invalid KINEMATION ammo capacity for '{data.weaponName}'. " +
                "Strict mode requires FPSWeaponSettings.ammo > 0.");
            return 0;

        }

        private bool TryValidateSwitchTargetStrict(int index, out WeaponData data, out int magCapacity) {
            data = null;
            magCapacity = 0;

            if(index < 0 || index >= weaponDataList.Count) {
                Debug.LogError($"[WeaponManager][KIN-Strict] Invalid weapon index {index}.");
                return false;
            }

            data = weaponDataList[index];
            if(data == null) {
                Debug.LogError($"[WeaponManager][KIN-Strict] WeaponData is null at index {index}.");
                return false;
            }

            if(index >= _fpWeaponInstances.Count) {
                Debug.LogError(
                    $"[WeaponManager][KIN-Strict] Missing FP instance for '{data.weaponName}' at index {index}. " +
                    "Blocking switch.");
                return false;
            }

            var fpRoot = _fpWeaponInstances[index];
            if(fpRoot == null || !TryGetKinemationDriver(fpRoot, out var kinemationDriver) || kinemationDriver == null) {
                Debug.LogError(
                    $"[WeaponManager][KIN-Strict] Missing KinemationFpWeaponDriver for '{data.weaponName}'. " +
                    "Blocking switch.");
                return false;
            }

            magCapacity = ResolveWeaponCapacity(data);
            if(magCapacity <= 0) {
                Debug.LogError(
                    $"[WeaponManager][KIN-Strict] Invalid KIN ammo capacity for '{data.weaponName}'. " +
                    "Blocking switch.");
                return false;
            }

            var worldWeapon = ResolveWorldWeaponObject(data);
            if(worldWeapon == null) {
                Debug.LogError(
                    $"[WeaponManager][KIN-Strict] Missing world weapon binding for '{data.weaponName}'. " +
                    "Blocking switch.");
                return false;
            }

            var worldBinding = worldWeapon.GetComponent<WorldWeaponBinding>();
            if(worldBinding == null) {
                Debug.LogError(
                    $"[WeaponManager][KIN-Strict] Missing WorldWeaponBinding component on '{worldWeapon.name}' " +
                    $"for '{data.weaponName}'. Blocking switch.");
                return false;
            }

            if(worldBinding.TryGetRuntimeReferences(out _, out _)) return true;
            Debug.LogError(
                $"[WeaponManager][KIN-Strict] Missing assigned muzzle reference on world weapon '{worldWeapon.name}' " +
                $"for '{data.weaponName}'. Blocking switch.");
            return false;

        }

        private void LogStrictStartupValidationOnce() {
            if(_hasLoggedStrictStartupValidation) return;
            _hasLoggedStrictStartupValidation = true;

            if(weaponDataList == null || weaponDataList.Count == 0) {
                Debug.LogError("[WeaponManager][KIN-Strict] Startup validation: equipped weapon list is empty.");
                return;
            }

            for(var i = 0; i < weaponDataList.Count; i++) {
                var data = weaponDataList[i];
                if(data == null) {
                    Debug.LogError($"[WeaponManager][KIN-Strict] Startup validation: WeaponData is null at index {i}.");
                    continue;
                }

                if(!TryGetKinemationBindingForData(data, out var binding) || binding == null ||
                   binding.kinemationWeaponPrefab == null) {
                    Debug.LogError(
                        $"[WeaponManager][KIN-Strict] Startup validation: missing KIN binding/prefab for '{data.weaponName}'.");
                    continue;
                }

                var capacity = ResolveKinemationWeaponCapacity(binding.kinemationWeaponPrefab);
                if(capacity <= 0) {
                    Debug.LogError(
                        $"[WeaponManager][KIN-Strict] Startup validation: invalid KIN ammo capacity for '{data.weaponName}'.");
                }

                var worldWeapon = ResolveWorldWeaponObject(data);
                if(worldWeapon == null) {
                    Debug.LogError(
                        $"[WeaponManager][KIN-Strict] Startup validation: missing world weapon binding for '{data.weaponName}'.");
                    continue;
                }

                var worldBinding = worldWeapon.GetComponent<WorldWeaponBinding>();
                if(worldBinding == null) {
                    Debug.LogError(
                        $"[WeaponManager][KIN-Strict] Startup validation: missing WorldWeaponBinding on '{worldWeapon.name}' " +
                        $"for '{data.weaponName}'.");
                    continue;
                }

                if(!worldBinding.TryGetRuntimeReferences(out _, out _)) {
                    Debug.LogError(
                        $"[WeaponManager][KIN-Strict] Startup validation: missing assigned muzzle reference on world weapon '{worldWeapon.name}' " +
                        $"for '{data.weaponName}'.");
                }
            }
        }

        private bool BuildWorldWeaponLookup() {
            return _worldWeaponRegistry.Rebuild(_worldWeaponSocket, Debug.LogError);
        }

        private GameObject ResolveWorldWeaponObject(WeaponData data) {
            return _worldWeaponRegistry.Resolve(data);
        }

        private GameObject ResolveHolsterWeaponObject(WeaponData data) {
            return _worldWeaponRegistry.ResolveHolster(data);
        }

        private int ResolveRestoredAmmo(int weaponIndex, int magCapacity, bool seedWhenMissing) {
            return _ammoAuthority.ResolveRestoredAmmo(weaponIndex, magCapacity, seedWhenMissing);
        }

        private void RefreshOwnerHolsterShadowState() {
            if(IsOwner && playerController != null && playerController.PlayerShadow != null) {
                playerController.PlayerShadow.UpdateHolsterShadowStateForOwner();
            }
        }

        private NetworkVariable<int> ReplicatedEquippedWeaponIndex =>
            ResolvePlayerState()?.equippedWeaponIndex ?? MissingEquippedWeaponIndexState;

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
            if(!BuildWorldWeaponLookup()) return;
            LogStrictStartupValidationOnce();
            if(!ValidateStrictEquippedWeaponConfiguration()) return;
            SetupHolsteredWeaponModels();
            DisableUnequippedWorldWeapons();

            if(weaponDataList == null || weaponDataList.Count == 0) {
                Debug.LogError("[WeaponManager] weaponDataList is empty!");
                return;
            }

            HideAllWorldWeapons();
            InstantiateFpWeaponInstances();

            if(_fpWeaponInstances.Count != weaponDataList.Count) {
                Debug.LogError(
                    $"[WeaponManager][KIN-Strict] FP instance count mismatch. expected={weaponDataList.Count} actual={_fpWeaponInstances.Count}");
            }

            // Switch to the replicated equipped weapon if available. This is required for
            // late join/backfill so remote players don't reconstruct everyone at slot 0.
            if(_fpWeaponInstances.Count > 0) {
                EquipInitialWeapon(ResolveInitialEquippedWeaponIndex());
                _weaponsInitialized = true;
            } else {
                Debug.LogError("[WeaponManager] No weapons instantiated!");
            }

            UpdateHolsterVisibility();

            if(IsOwner) {
                RefreshOwnerAmmoHudFromCurrentWeapon();
            }

            EnsureFpWeaponLightingRig();
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

            if(!TryValidateSwitchTargetStrict(index, out var data, out var magCapacity)) {
                return;
            }

            CurrentWeaponIndex = index;
            IsPullingOut = false;
            _requiresKinemationEquipCompleteForCurrentPullOut = false;

            var fp = ActivateFpWeapon(index, data, triggerPullOutAnimation: false);
            if(fp == null) {
                Debug.LogError(
                    $"[WeaponManager][KIN-Strict] Failed to activate FP KIN weapon for '{data.weaponName}'.");
                return;
            }

            // ---- 3P WORLD WEAPON ----
            var worldWeaponInstance = ResolveWorldWeaponObject(data);
            if(worldWeaponInstance != null) {
                worldWeaponInstance.SetActive(true);
                CurrentWorldWeaponInstance = worldWeaponInstance;
            } else {
                Debug.LogError(
                    $"[WeaponManager][KIN-Strict] Missing world weapon for '{data.weaponName}'.");
                return;
            }

            // ---- AMMO ----
            var restoredAmmo = ResolveRestoredAmmo(index, magCapacity, seedWhenMissing: true);

            // This sets weapon data, ammo, HUD, muzzle lights, etc.
            CurrentWeapon.SwitchToWeapon(
                data,
                fp,
                worldWeaponInstance,
                restoredAmmo,
                magCapacity
            );

            if(HasWeaponAuthority) {
                _serverAuthoritativeWeaponIndex = index;
                _serverReloadWeaponIndex = -1;
                _serverPullOutBlockedUntilTime = 0f;
            }

            if(HasWeaponAuthority && ResolvePlayerState() != null) {
                ReplicatedEquippedWeaponIndex.Value = index;
            }

            _pendingHolsterHideSlot = -1;
            UpdateHolsterVisibility();
            RefreshOwnerHolsterShadowState();
            _lastApprovedWeaponIndex = index;
            _pendingPredictedWeaponIndex = -1;
        }

        private int ResolveInitialEquippedWeaponIndex() {
            var replicatedIndex = ReplicatedEquippedWeaponIndex.Value;
            if(replicatedIndex >= 0 && replicatedIndex < weaponDataList.Count) {
                return replicatedIndex;
            }

            return 0;
        }

        private MatchPlayerStateProxy ResolvePlayerState() {
            if(playerController == null || playerController.OwnerClientId == ulong.MaxValue) {
                return null;
            }

            if(_cachedPlayerState != null &&
               _cachedPlayerState.NetworkObject != null &&
               _cachedPlayerState.NetworkObject.IsSpawned &&
               _cachedPlayerState.RepresentedClientId == playerController.OwnerClientId) {
                return _cachedPlayerState;
            }

            _cachedPlayerState = MatchPlayerStateProxy.GetForPlayer(playerController.OwnerClientId);
            return _cachedPlayerState;
        }

        private void OnPlayerStateRegistered(ulong playerClientId, MatchPlayerStateProxy proxy) {
            if(playerController == null || playerController.OwnerClientId != playerClientId) {
                return;
            }

            _cachedPlayerState = proxy;
            TryBindPlayerStateSubscriptions();
        }

        private void OnPlayerStateUnregistered(ulong playerClientId, MatchPlayerStateProxy proxy) {
            if(playerController == null || playerController.OwnerClientId != playerClientId) {
                return;
            }

            if(_boundPlayerState == proxy) {
                UnbindPlayerStateSubscriptions();
            }

            if(_cachedPlayerState == proxy) {
                _cachedPlayerState = null;
            }
        }

        private void TryBindPlayerStateSubscriptions() {
            var playerState = ResolvePlayerState();
            if(playerState == null || _boundPlayerState == playerState) {
                return;
            }

            UnbindPlayerStateSubscriptions();
            playerState.equippedWeaponIndex.OnValueChanged += OnReplicatedEquippedWeaponIndexChanged;
            _boundPlayerState = playerState;

            if(HasWeaponAuthority && CurrentWeaponIndex >= 0 && playerState.equippedWeaponIndex.Value != CurrentWeaponIndex) {
                playerState.equippedWeaponIndex.Value = CurrentWeaponIndex;
            }
        }

        private void UnbindPlayerStateSubscriptions() {
            if(_boundPlayerState == null) {
                return;
            }

            _boundPlayerState.equippedWeaponIndex.OnValueChanged -= OnReplicatedEquippedWeaponIndexChanged;
            _boundPlayerState = null;
        }

        private void OnReplicatedEquippedWeaponIndexChanged(int previousValue, int newValue) {
            if(!_weaponsInitialized) return;
            if(newValue < 0 || newValue >= weaponDataList.Count) return;

            if(HasWeaponAuthority) {
                ApplyServerAuthoritativeWeaponSwitch(newValue);
            }

            if(IsOwner) {
                _lastApprovedWeaponIndex = newValue;
                if(_pendingPredictedWeaponIndex == newValue) {
                    _pendingPredictedWeaponIndex = -1;
                }

                if(newValue == CurrentWeaponIndex) {
                    return;
                }

                ApplyApprovedLocalWeaponSwitch(newValue);
                return;
            }

            if(newValue == CurrentWeaponIndex) return;

            ApplyRemoteWeaponSwitch(newValue);
        }

    }
}
