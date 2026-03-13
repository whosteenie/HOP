using System;
using System.Collections.Generic;
using Game.Match;
using Game.Player;
using Game.Player.Core;
using Game.Weapons.World;
using KINEMATION.FPSAnimationPack.Scripts.Weapon;
using Network.Core;
using Network.Diagnostics;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Weapons.Manager {
    public class WeaponManager : NetworkBehaviour {
        public enum AmmoSyncReason : byte {
            ReloadStarted = 0,
            ReloadSingleRound = 1,
            ReloadCompleted = 2,
            ReloadCanceled = 3,
            RefillCurrentWeapon = 4
        }

        [Serializable]
        internal class KinemationWeaponBinding {
            public WeaponData weaponData;
            public GameObject kinemationWeaponPrefab;
            public bool useCustomViewmodelPose;
            public Vector3 viewmodelLocalPosition = Vector3.zero;
            public Vector3 viewmodelLocalEulerAngles = Vector3.zero;

            [Tooltip(
                "Optional. Per-weapon grapple animation clip (e.g. A_FP_DGL_Grapple). If unset, controller default is used.")]
            public AnimationClip grappleClip;
        }

        [SerializeField] private PlayerController playerController;

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

        [Header("FP Weapon Lighting")]
        [SerializeField] private bool enableFpWeaponLightRig = true;

        [SerializeField] private Vector3 fpKeyLightLocalPosition = new(0.08f, 0.06f, -0.04f);
        [SerializeField] private Vector3 fpKeyLightLocalEulerAngles = new(12f, -15f, 0f);
        [SerializeField, Min(0f)] private float fpKeyLightIntensity = 1f;
        [SerializeField, Min(0.1f)] private float fpKeyLightRange = 3.5f;
        [SerializeField, Range(1f, 179f)] private float fpKeyLightSpotAngle = 75f;
        [SerializeField] private Color fpKeyLightColor = new(1f, 0.97f, 0.92f, 1f);
        [SerializeField] private Vector3 fpFillLightLocalPosition = new(-0.08f, -0.04f, -0.02f);
        [SerializeField] private Vector3 fpFillLightLocalEulerAngles = new(16f, 18f, 0f);
        [SerializeField, Min(0f)] private float fpFillLightIntensity = 0.35f;
        [SerializeField, Min(0.1f)] private float fpFillLightRange = 3f;
        [SerializeField, Range(1f, 179f)] private float fpFillLightSpotAngle = 90f;
        [SerializeField] private Color fpFillLightColor = new(0.92f, 0.96f, 1f, 1f);

        private WeaponAuthorityCoordinator _authorityCoordinator;
        private WeaponLoadoutCoordinator _loadoutCoordinator;
        private WeaponSwitchCoordinator _switchCoordinator;
        private WeaponFpPresentationCoordinator _fpPresentationCoordinator;
        private WeaponFpLightingCoordinator _fpLightingCoordinator;

        public Weapon CurrentWeapon { get; private set; }
        public GameObject CurrentWorldWeaponInstance { get; private set; }

        public int CurrentWeaponIndex { get; private set; } = -1;

        public int WeaponCount => weaponDataList.Count;
        public IReadOnlyList<WeaponData> PrimaryWeaponOptions => KinemationCatalogRef.PrimaryWeaponOptions;
        public IReadOnlyList<WeaponData> SecondaryWeaponOptions => KinemationCatalogRef.SecondaryWeaponOptions;
        public bool IsPullingOut { get; private set; }

        private static readonly int PullOutHash = Animator.StringToHash("PullOut");
        private static readonly int WeaponIndexHash = Animator.StringToHash("WeaponIndex");
        internal int PullOutHashInternal => PullOutHash;
        internal int WeaponIndexHashInternal => WeaponIndexHash;
        public GameObject PrimaryHolster { get; private set; }

        public GameObject SecondaryHolster { get; private set; }

        private static readonly NetworkVariable<int> MissingEquippedWeaponIndexState = new(-1);
        private const string FpLightRigRootName = "FP_LightRig";
        private const string FpKeyLightName = "FP_Key";
        private const string FpFillLightName = "FP_Fill";
        internal const string FpLightRigRootNameConst = FpLightRigRootName;
        internal const string FpKeyLightNameConst = FpKeyLightName;
        internal const string FpFillLightNameConst = FpFillLightName;

        private void Awake() {
            InitializeCoordinators();
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
            WeaponsInitialized = false;
            base.OnNetworkDespawn();
        }

        private void ValidateComponents() {
            InitializeCoordinators();
            if(playerController == null) {
                playerController = this.GetComponentSafe<PlayerController>("WeaponManager.ValidateComponents");
            }

            if(playerController == null) {
                enabled = false;
                return;
            }

            if(CurrentWeapon == null) CurrentWeapon = playerController.WeaponComponent;
            if(FpCameraRef == null) FpCameraRef = playerController.FpCamera;
            if(WeaponCameraRef == null) WeaponCameraRef = playerController.WeaponCamera;
            if(WorldWeaponSocketRef == null) WorldWeaponSocketRef = playerController.WorldWeaponSocket;
            if(PlayerAnimatorRef == null) PlayerAnimatorRef = playerController.PlayerAnimator;

            // Validate PlayerRenderer (required for renderer operations)
            if(PlayerRendererRef == null) PlayerRendererRef = playerController.PlayerRenderer;
            if(PlayerRendererRef == null) {
                // PlayerRenderer not found - event already published by GetComponentSafe if used
                enabled = false;
                return;
            }

            BuildKinemationWeaponLookup();
        }

        private void Update() {
            _switchCoordinator.UpdateKinemationEquipCompletionGate();
            _fpLightingCoordinator.EnsureFpWeaponLightingRig();

            if((Time.frameCount & 7) == 0) {
                _switchCoordinator.ReconcileStableTpWeaponState();
            }
        }

        private void InitializeCoordinators() {
            _authorityCoordinator ??= new WeaponAuthorityCoordinator(this);
            _loadoutCoordinator ??= new WeaponLoadoutCoordinator(this);
            _switchCoordinator ??= new WeaponSwitchCoordinator(this);
            _fpPresentationCoordinator ??= new WeaponFpPresentationCoordinator(this);
            _fpLightingCoordinator ??= new WeaponFpLightingCoordinator(this);
        }

        internal void BuildKinemationWeaponLookup() {
            KinemationCatalogRef.Rebuild(
                kinemationWeaponBindings,
                ResolveWeaponSlot,
                Debug.LogError
            );
        }

        internal bool TryGetKinemationBindingForData(WeaponData data, out KinemationWeaponBinding kinemationBinding) {
            kinemationBinding = null;
            if(KinemationCatalogRef.IsEmpty) {
                BuildKinemationWeaponLookup();
            }

            return KinemationCatalogRef.TryGetBinding(kinemationFpsPlayerPrefab, data, out kinemationBinding);
        }

        internal static int ResolveKinemationWeaponCapacity(GameObject kinemationWeaponPrefab) {
            if(kinemationWeaponPrefab == null) return 0;
            var fpsWeapon = kinemationWeaponPrefab.GetComponentInChildren<FPSWeapon>(true);
            if(fpsWeapon == null || fpsWeapon.weaponSettings == null) return 0;
            return Mathf.Max(1, fpsWeapon.weaponSettings.ammo);
        }

        internal int ResolveWeaponCapacity(WeaponData data) {
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

        internal bool TryValidateSwitchTargetStrict(int index, out WeaponData data, out int magCapacity) {
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

            if(index >= FpWeaponInstancesRef.Count) {
                Debug.LogError(
                    $"[WeaponManager][KIN-Strict] Missing FP instance for '{data.weaponName}' at index {index}. " +
                    "Blocking switch.");
                return false;
            }

            var fpRoot = FpWeaponInstancesRef[index];
            if(fpRoot == null || !TryGetKinemationDriverInternal(fpRoot, out var kinemationDriver) ||
               kinemationDriver == null) {
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

        internal void LogStrictStartupValidationOnce() {
            if(HasLoggedStrictStartupValidation) return;
            HasLoggedStrictStartupValidation = true;

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

        internal bool BuildWorldWeaponLookup() {
            return WorldWeaponRegistryRef.Rebuild(WorldWeaponSocketRef, Debug.LogError);
        }

        internal GameObject ResolveWorldWeaponObject(WeaponData data) {
            return WorldWeaponRegistryRef.Resolve(data);
        }

        internal GameObject ResolveHolsterWeaponObject(WeaponData data) {
            return WorldWeaponRegistryRef.ResolveHolster(data);
        }

        internal int ResolveRestoredAmmo(int weaponIndex, int magCapacity, bool seedWhenMissing) {
            return AmmoAuthorityRef.ResolveRestoredAmmo(weaponIndex, magCapacity, seedWhenMissing);
        }

        internal void RefreshOwnerHolsterShadowState() {
            if(IsOwner && playerController != null && playerController.PlayerShadow != null) {
                playerController.PlayerShadow.UpdateHolsterShadowStateForOwner();
            }
        }

        internal NetworkVariable<int> ReplicatedEquippedWeaponIndex =>
            ResolvePlayerState().equippedWeaponIndex ?? MissingEquippedWeaponIndexState;

        private bool HasWeaponAuthority => NetworkAuthority.HasGlobalAuthority(this);

        public void InitializeWeapons() {
            _loadoutCoordinator.InitializeWeapons();
        }

        public void ApplyTpWeaponStateOnRespawn() {
            _loadoutCoordinator.ApplyTpWeaponStateOnRespawn();
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
            RequiresKinemationEquipCompleteForCurrentPullOut = false;

            var fp = ActivateFpWeaponInternal(index, data, triggerPullOutAnimation: false);
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
                ServerAuthoritativeWeaponIndex = index;
                ServerReloadWeaponIndex = -1;
                ServerPullOutBlockedUntilTime = 0f;
            }

            if(HasWeaponAuthority && ResolvePlayerState() != null) {
                ReplicatedEquippedWeaponIndex.Value = index;
            }

            PendingHolsterHideSlot = -1;
            RefreshHolsterVisibility();
            RefreshOwnerHolsterShadowState();
            LastApprovedWeaponIndex = index;
            PendingPredictedWeaponIndex = -1;
        }

        internal void EquipInitialWeaponInternal(int index) => EquipInitialWeapon(index);

        private static int ResolveWeaponSlot(WeaponData data) {
            if(data == null) return -1;
            var slot = data.WeaponSlotIndex;
            return slot is 0 or 1 ? slot : -1;
        }

        internal MatchPlayerStateProxy ResolvePlayerState() {
            if(playerController == null || playerController.OwnerClientId == ulong.MaxValue) {
                return null;
            }

            if(CachedPlayerState != null &&
               CachedPlayerState.NetworkObject != null &&
               CachedPlayerState.NetworkObject.IsSpawned &&
               CachedPlayerState.RepresentedClientId == playerController.OwnerClientId) {
                return CachedPlayerState;
            }

            CachedPlayerState = MatchPlayerStateProxy.GetForPlayer(playerController.OwnerClientId);
            return CachedPlayerState;
        }

        private void OnPlayerStateRegistered(ulong playerClientId, MatchPlayerStateProxy proxy) {
            if(playerController == null || playerController.OwnerClientId != playerClientId) {
                return;
            }

            CachedPlayerState = proxy;
            TryBindPlayerStateSubscriptions();
        }

        private void OnPlayerStateUnregistered(ulong playerClientId, MatchPlayerStateProxy proxy) {
            if(playerController == null || playerController.OwnerClientId != playerClientId) {
                return;
            }

            if(BoundPlayerState == proxy) {
                UnbindPlayerStateSubscriptions();
            }

            if(CachedPlayerState == proxy) {
                CachedPlayerState = null;
            }
        }

        private void TryBindPlayerStateSubscriptions() {
            var playerState = ResolvePlayerState();
            if(playerState == null || BoundPlayerState == playerState) {
                return;
            }

            UnbindPlayerStateSubscriptions();
            playerState.equippedWeaponIndex.OnValueChanged += OnReplicatedEquippedWeaponIndexChanged;
            BoundPlayerState = playerState;

            if(HasWeaponAuthority && CurrentWeaponIndex >= 0 &&
               playerState.equippedWeaponIndex.Value != CurrentWeaponIndex) {
                playerState.equippedWeaponIndex.Value = CurrentWeaponIndex;
            }
        }

        private void UnbindPlayerStateSubscriptions() {
            if(BoundPlayerState == null) {
                return;
            }

            BoundPlayerState.equippedWeaponIndex.OnValueChanged -= OnReplicatedEquippedWeaponIndexChanged;
            BoundPlayerState = null;
        }

        private void OnReplicatedEquippedWeaponIndexChanged(int previousValue, int newValue) {
            if(!WeaponsInitialized) return;
            if(newValue < 0 || newValue >= weaponDataList.Count) return;

            if(HasWeaponAuthority) {
                _authorityCoordinator.ApplyServerAuthoritativeWeaponSwitch(newValue);
            }

            if(IsOwner) {
                LastApprovedWeaponIndex = newValue;
                if(PendingPredictedWeaponIndex == newValue) {
                    PendingPredictedWeaponIndex = -1;
                }

                if(newValue == CurrentWeaponIndex) {
                    return;
                }

                _switchCoordinator.ApplyApprovedLocalWeaponSwitch(newValue);
                return;
            }

            if(newValue == CurrentWeaponIndex) return;

            _switchCoordinator.ApplyRemoteWeaponSwitch(newValue);
        }

        internal PlayerController PlayerControllerRef => playerController;
        internal CinemachineCamera FpCameraRef { get; private set; }

        internal Camera WeaponCameraRef { get; private set; }

        internal Transform WorldWeaponSocketRef { get; private set; }

        internal Animator PlayerAnimatorRef { get; private set; }

        internal PlayerRenderer PlayerRendererRef { get; private set; }

        internal List<WeaponData> WeaponDataListRef => weaponDataList;
        internal List<GameObject> FpWeaponInstancesRef { get; } = new();

        internal WeaponAmmoAuthority AmmoAuthorityRef { get; } = new();

        internal WeaponKinemationBindingCatalog KinemationCatalogRef { get; } = new();

        private WeaponWorldWeaponRegistry WorldWeaponRegistryRef { get; } = new();

        internal GameObject PendingTpWeapon { get; set; }

        internal int ServerAuthoritativeWeaponIndex { get; set; } = -1;
        internal int ServerReloadWeaponIndex { get; set; } = -1;
        internal float ServerPullOutBlockedUntilTime { get; set; }

        private MatchPlayerStateProxy CachedPlayerState { get; set; }

        private MatchPlayerStateProxy BoundPlayerState { get; set; }

        internal int LastApprovedWeaponIndex { get; set; } = -1;
        internal int PendingPredictedWeaponIndex { get; set; } = -1;
        internal int PendingHolsterHideSlot { get; set; } = -1;
        internal bool SuppressLoadoutRebuildCallbacks { get; set; }

        internal bool DeferTpRevealUntilRespawn { get; set; }

        internal GameObject DeferredRespawnWorldWeapon { get; set; }

        internal Coroutine KinemationPullOutCompletionCoroutine { get; set; }

        internal bool RequiresKinemationEquipCompleteForCurrentPullOut { get; set; }

        private bool HasLoggedStrictStartupValidation { get; set; }

        internal bool WeaponsInitialized { get; set; }

        internal GameObject KinemationFpsPlayerPrefabRef => kinemationFpsPlayerPrefab;
        internal float KinemationSprintWalkGaitValue => kinemationSprintWalkGaitValue;
        internal float KinemationEquipUnlockNormalizedTime => kinemationEquipUnlockNormalizedTime;
        internal bool AutoCompleteKinemationPullOut => autoCompleteKinemationPullOut;
        internal float KinemationPullOutCompleteDelay => kinemationPullOutCompleteDelay;
        internal float PostMatchPullOutFailSafeDelay => postMatchPullOutFailSafeDelay;
        internal Vector3 KinemationViewmodelLocalPosition => kinemationViewmodelLocalPosition;
        internal Vector3 KinemationViewmodelLocalEulerAngles => kinemationViewmodelLocalEulerAngles;

        internal GameObject PrimaryHolsterInternal {
            get => PrimaryHolster;
            set => PrimaryHolster = value;
        }

        internal GameObject SecondaryHolsterInternal {
            get => SecondaryHolster;
            set => SecondaryHolster = value;
        }

        internal int CurrentWeaponIndexInternal {
            get => CurrentWeaponIndex;
            set => CurrentWeaponIndex = value;
        }

        internal GameObject CurrentWorldWeaponInstanceInternal {
            get => CurrentWorldWeaponInstance;
            set => CurrentWorldWeaponInstance = value;
        }

        internal Weapon CurrentWeaponInternal {
            get => CurrentWeapon;
            set => CurrentWeapon = value;
        }

        internal bool IsPullingOutInternal {
            get => IsPullingOut;
            set => IsPullingOut = value;
        }

        internal bool EnableFpWeaponLightRig => enableFpWeaponLightRig;
        internal Vector3 FpKeyLightLocalPosition => fpKeyLightLocalPosition;
        internal Vector3 FpKeyLightLocalEulerAngles => fpKeyLightLocalEulerAngles;
        internal float FpKeyLightIntensity => fpKeyLightIntensity;
        internal float FpKeyLightRange => fpKeyLightRange;
        internal float FpKeyLightSpotAngle => fpKeyLightSpotAngle;
        internal Color FpKeyLightColor => fpKeyLightColor;
        internal Vector3 FpFillLightLocalPosition => fpFillLightLocalPosition;
        internal Vector3 FpFillLightLocalEulerAngles => fpFillLightLocalEulerAngles;
        internal float FpFillLightIntensity => fpFillLightIntensity;
        internal float FpFillLightRange => fpFillLightRange;
        internal float FpFillLightSpotAngle => fpFillLightSpotAngle;
        internal Color FpFillLightColor => fpFillLightColor;
        internal Transform FpLightRigRoot { get; set; }

        internal Light FpKeyLight { get; set; }

        internal Light FpFillLight { get; set; }

        internal bool LoggedMissingWeaponLayer { get; set; }

        public void RefreshOwnerAmmoHudFromCurrentWeapon() =>
            _authorityCoordinator.RefreshOwnerAmmoHudFromCurrentWeapon();

        public void ResetAllWeaponAmmo() => _authorityCoordinator.ResetAllWeaponAmmo();

        public void PrepareCurrentWeaponForPostMatchPodium() =>
            _authorityCoordinator.PrepareCurrentWeaponForPostMatchPodium();

        public void DrainCurrentWeaponAmmoForTag() => _authorityCoordinator.DrainCurrentWeaponAmmoForTag();

        public bool RegisterServerShot(int weaponIndex, ulong shotId, float clientShotTime, out string reason) =>
            _authorityCoordinator.RegisterServerShot(weaponIndex, shotId, clientShotTime, out reason);

        public bool ValidateServerHitClaim(int weaponIndex, ulong shotId, out string reason) =>
            _authorityCoordinator.ValidateServerHitClaim(weaponIndex, shotId, out reason);

        public string GetCombatAuthorityDebugSummary(int requestedWeaponIndex = -1) =>
            _authorityCoordinator.GetCombatAuthorityDebugSummary(requestedWeaponIndex);

        public bool TryComputeServerDamage(int weaponIndex, Vector3 hitPoint, out float damage, out string reason) =>
            _authorityCoordinator.TryComputeServerDamage(weaponIndex, hitPoint, out damage, out reason);

        public void ReportWeaponStateSync(int weaponIndex, AmmoSyncReason reason, int localAmmoAfterEvent) =>
            _authorityCoordinator.ReportWeaponStateSync(weaponIndex, reason, localAmmoAfterEvent);

        public void ReportShotFired(int weaponIndex, ulong shotId, float clientShotTime) =>
            _authorityCoordinator.ReportShotFired(weaponIndex, shotId, clientShotTime);

        public void RegisterServerShotAndLogOnAuthority(int weaponIndex, ulong shotId, float clientShotTime) =>
            _authorityCoordinator.RegisterServerShotAndLogOnAuthority(weaponIndex, shotId, clientShotTime);

        public void UpdateServerWeaponStateOnAuthority(int weaponIndex, AmmoSyncReason reason,
            int localAmmoAfterEvent) =>
            _authorityCoordinator.UpdateServerWeaponStateOnAuthority(weaponIndex, reason, localAmmoAfterEvent);

        public void ResetAllWeaponAmmoOnAuthority() => _authorityCoordinator.ResetAllWeaponAmmoOnAuthority();

        public static bool IsFriendlyFireServer(PlayerController shooter, PlayerController victim) {
            if(shooter == null || victim == null) return false;

            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings == null || !MatchSettingsManager.IsTeamBasedMode(matchSettings.selectedGameModeId)) {
                return false;
            }

            var shooterTeamManager = shooter.TeamManager;
            var victimTeamManager = victim.TeamManager;
            if(shooterTeamManager == null || victimTeamManager == null) {
                return false;
            }

            return shooterTeamManager.netTeam.Value == victimTeamManager.netTeam.Value;
        }

        public int GetPrimarySelectionIndex() => _loadoutCoordinator.GetPrimarySelectionIndex();
        public int GetSecondarySelectionIndex() => _loadoutCoordinator.GetSecondarySelectionIndex();

        public bool ApplyOwnerLoadoutSelection(int primaryIndex, int secondaryIndex,
            bool deferTpRevealUntilRespawn = true) =>
            _loadoutCoordinator.ApplyOwnerLoadoutSelection(primaryIndex, secondaryIndex, deferTpRevealUntilRespawn);

        public int GetCurrentHolsterSlot() => _loadoutCoordinator.GetCurrentHolsterSlot();
        public void RefreshHolsterVisibility() => _loadoutCoordinator.RefreshHolsterVisibility();

        public void SwitchWeapon(int newIndex) => _switchCoordinator.SwitchWeapon(newIndex);
        public void ShowTpWeapon() => _switchCoordinator.ShowTpWeapon();
        public void HandlePullOutCompleted() => _switchCoordinator.HandlePullOutCompleted();
        public void HandleThirdPersonPullOutCompleted() => _switchCoordinator.HandleThirdPersonPullOutCompleted();
        public void HandleKinemationEquipCompleted() => _switchCoordinator.HandleKinemationEquipCompleted();
        public void TriggerPullOutAnimation() => _switchCoordinator.TriggerPullOutAnimation();
        public void CancelPendingPullOutForPostMatch() => _switchCoordinator.CancelPendingPullOutForPostMatch();
        public void SetTpWeaponIndexForPodium() => _switchCoordinator.SetTpWeaponIndexForPodium();

        public void ProcessWeaponSwitchAuthorityRequest(int newIndex) =>
            _switchCoordinator.ProcessWeaponSwitchAuthorityRequest(newIndex);

        public GameObject GetCurrentFpWeapon() => _fpPresentationCoordinator.GetCurrentFpWeapon();

        public GameObject GetCurrentFpWeaponHolderRootForDisconnectDuplicate() =>
            _fpPresentationCoordinator.GetCurrentFpWeaponHolderRootForDisconnectDuplicate();

        public void UpdateAllFpArmTagGlow(bool isTagged) => _fpPresentationCoordinator.UpdateAllFpArmTagGlow(isTagged);

        public void SetCurrentFpWeaponVisible(bool visible) =>
            _fpPresentationCoordinator.SetCurrentFpWeaponVisible(visible);

        public void HideFpVisualsForDisconnectTransition() =>
            _fpPresentationCoordinator.HideFpVisualsForDisconnectTransition();

        public void OffsetCurrentFpWeapon(Vector3 localPosition, Vector3 localEulerAngles) =>
            _fpPresentationCoordinator.OffsetCurrentFpWeapon(localPosition, localEulerAngles);

        internal void OnWeaponIndexChangedInternal(int oldValue, int newValue) =>
            _loadoutCoordinator.OnWeaponIndexChanged(oldValue, newValue);

        internal void ApplyDrainedAmmoOwnerClient(int weaponIndex, int ammo, int magSize) =>
            _authorityCoordinator.ApplyDrainedAmmoOwnerClient(weaponIndex, ammo, magSize);

        internal void ReportWeaponStateSyncServer(int weaponIndex, AmmoSyncReason reason, int localAmmoAfterEvent,
            RpcParams rpcParams) =>
            _authorityCoordinator.ReportWeaponStateSyncServer(weaponIndex, reason, localAmmoAfterEvent, rpcParams);

        internal void ResetAllWeaponAmmoServer(RpcParams rpcParams) =>
            _authorityCoordinator.ResetAllWeaponAmmoServer(rpcParams);

        internal void ReportShotFiredServer(int weaponIndex, ulong shotId, float clientShotTime, RpcParams rpcParams) =>
            _authorityCoordinator.ReportShotFiredServer(weaponIndex, shotId, clientShotTime, rpcParams);

        internal GameObject ActivateFpWeaponInternal(int weaponIndex, WeaponData data, bool triggerPullOutAnimation) =>
            _fpPresentationCoordinator.ActivateFpWeapon(weaponIndex, data, triggerPullOutAnimation);

        internal void InstantiateFpWeaponInstancesInternal() =>
            _fpPresentationCoordinator.InstantiateFpWeaponInstances();

        internal void DestroyFpWeaponInstancesInternal() => _fpPresentationCoordinator.DestroyFpWeaponInstances();

        internal bool TryGetKinemationDriverInternal(GameObject fpWeaponRoot, out KinemationFpWeaponDriver driver) =>
            _fpPresentationCoordinator.TryGetKinemationDriver(fpWeaponRoot, out driver);

        internal void ApplyResolvedKinemationViewmodelPoseInternal(GameObject fpWeaponRoot,
            KinemationWeaponBinding binding) =>
            _fpPresentationCoordinator.ApplyResolvedKinemationViewmodelPose(fpWeaponRoot, binding);

        internal int GetFpWeaponLayerInternal() => _fpPresentationCoordinator.GetFpWeaponLayer();

        internal void SetupFpWeaponSkinnedMeshRenderersInternal(GameObject fpWeaponInstance) =>
            _fpPresentationCoordinator.SetupFpWeaponSkinnedMeshRenderers(fpWeaponInstance);

        internal void EnsureHierarchyActiveInternal(GameObject instanceRoot) =>
            _fpPresentationCoordinator.EnsureHierarchyActive(instanceRoot);

        internal void EnsureFpWeaponLightingRigInternal() => _fpLightingCoordinator.EnsureFpWeaponLightingRig();
        internal int GetSlotForIndexInternal(int index) => _loadoutCoordinator.GetSlotForIndexInternal(index);

        internal void ResolveCurrentWorldWeaponReferenceInternal() =>
            _loadoutCoordinator.ResolveCurrentWorldWeaponReferenceInternal();

        internal void EnsureWorldWeaponShadowStateInternal() =>
            _switchCoordinator.EnsureWorldWeaponShadowStateInternal();

        internal void EnsureWeaponHierarchyActiveInternal() => _switchCoordinator.EnsureWeaponHierarchyActiveInternal();

        internal void ApplyServerAuthoritativeWeaponSwitch(int weaponIndex) =>
            _authorityCoordinator.ApplyServerAuthoritativeWeaponSwitch(weaponIndex);

        internal void ValidateComponentsForPublicUse() => ValidateComponents();

        [Rpc(SendTo.Owner)]
        internal void ApplyDrainedAmmoOwnerClientRpc(int weaponIndex, int ammo, int magSize) {
            _authorityCoordinator.ApplyDrainedAmmoOwnerClient(weaponIndex, ammo, magSize);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        internal void ReportWeaponStateSyncServerRpc(int weaponIndex, AmmoSyncReason reason, int localAmmoAfterEvent,
            RpcParams rpcParams = default) {
            _authorityCoordinator.ReportWeaponStateSyncServer(weaponIndex, reason, localAmmoAfterEvent, rpcParams);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        internal void ResetAllWeaponAmmoServerRpc(RpcParams rpcParams = default) {
            _authorityCoordinator.ResetAllWeaponAmmoServer(rpcParams);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        internal void ReportShotFiredServerRpc(int weaponIndex, ulong shotId, float clientShotTime,
            RpcParams rpcParams = default) {
            _authorityCoordinator.ReportShotFiredServer(weaponIndex, shotId, clientShotTime, rpcParams);
        }

        [Rpc(SendTo.Owner)]
        internal void RejectPredictedWeaponSwitchOwnerRpc(int approvedWeaponIndex) {
            _switchCoordinator.RejectPredictedWeaponSwitchOwner(approvedWeaponIndex);
        }
    }
}