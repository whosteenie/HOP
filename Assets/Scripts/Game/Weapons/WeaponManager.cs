using System;
using System.Collections.Generic;
using Game.Player;
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
        private readonly WeaponKinemationBindingCatalog _kinemationCatalog = new();
        private readonly WeaponAmmoAuthority _ammoAuthority = new();
        private readonly WeaponWorldWeaponRegistry _worldWeaponRegistry = new();
        private GameObject _pendingTpWeapon; // Track pending TP weapon to show via animation event

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

        private bool BuildWorldWeaponLookup() {
            return _worldWeaponRegistry.Rebuild(_worldWeaponSocket, Debug.LogError);
        }

        private GameObject ResolveWorldWeaponObject(WeaponData data) {
            return _worldWeaponRegistry.Resolve(data);
        }

        private int ResolveRestoredAmmo(int weaponIndex, int magCapacity, bool seedWhenMissing) {
            return _ammoAuthority.ResolveRestoredAmmo(weaponIndex, magCapacity, seedWhenMissing);
        }

        private void RefreshOwnerHolsterShadowState() {
            if(IsOwner && playerController != null && playerController.PlayerShadow != null) {
                playerController.PlayerShadow.UpdateHolsterShadowStateForOwner();
            }
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
            if(!BuildWorldWeaponLookup()) return;
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

    }
}
