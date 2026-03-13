using System;
using System.Collections.Generic;
using Game.Match;
using Game.Player;
using Game.Player.Core;
using Game.Weapons.World;
using KINEMATION.FPSAnimationPack.Scripts.Weapon;
using Network.Core;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Weapons.Manager {
    public class WeaponManager : NetworkBehaviour {
        #region Types

        public enum AmmoSyncReason : byte {
            ReloadStarted,
            ReloadSingleRound,
            ReloadCompleted,
            ReloadCanceled,
            RefillCurrentWeapon
        }

        [Serializable]
        internal class KinemationWeaponBinding {
            public WeaponData weaponData;
            public GameObject kinemationWeaponPrefab;
            public bool useCustomViewmodelPose;
            public Vector3 viewmodelLocalPosition;
            public Vector3 viewmodelLocalEulerAngles;
            [Tooltip("Optional grapple clip override for this weapon.")]
            public AnimationClip grappleClip;
        }

        #endregion

        #region Serialized Fields

        [SerializeField] private PlayerController playerController;

        [Header("Weapon System")]
        [SerializeField, HideInInspector] private List<WeaponData> weaponDataList = new();

        [Header("KINEMATION FP Integration")]
        [SerializeField] private GameObject kinemationFpsPlayerPrefab;
        [SerializeField] private List<KinemationWeaponBinding> kinemationWeaponBindings = new();
        [SerializeField] private float kinemationSprintWalkGaitValue = 1.2f;
        [SerializeField] private float kinemationEquipUnlockNormalizedTime = 0.82f;
        [SerializeField] private bool autoCompleteKinemationPullOut = true;
        [SerializeField] private float kinemationPullOutCompleteDelay = 0.12f;
        [SerializeField] private float postMatchPullOutFailSafeDelay = 0.65f;
        [SerializeField] private Vector3 kinemationViewmodelLocalPosition = Vector3.zero;
        [SerializeField] private Vector3 kinemationViewmodelLocalEulerAngles = Vector3.zero;

        [Header("FP Weapon Lighting")]
        [SerializeField] private bool enableFpWeaponLightRig = true;
        [SerializeField] private Vector3 fpKeyLightLocalPosition = new(0.08f, 0.06f, -0.04f);
        [SerializeField] private Vector3 fpKeyLightLocalEulerAngles = new(12f, -15f, 0f);
        [SerializeField] private float fpKeyLightIntensity = 1f;
        [SerializeField] private float fpKeyLightRange = 3.5f;
        [SerializeField] private float fpKeyLightSpotAngle = 75f;
        [SerializeField] private Color fpKeyLightColor = new(1f, 0.97f, 0.92f, 1f);
        [SerializeField] private Vector3 fpFillLightLocalPosition = new(-0.08f, -0.04f, -0.02f);
        [SerializeField] private Vector3 fpFillLightLocalEulerAngles = new(16f, 18f, 0f);
        [SerializeField] private float fpFillLightIntensity = 0.35f;
        [SerializeField] private float fpFillLightRange = 3f;
        [SerializeField] private float fpFillLightSpotAngle = 90f;
        [SerializeField] private Color fpFillLightColor = new(0.92f, 0.96f, 1f, 1f);

        #endregion

        #region Static State

        private static readonly int PullOutHash = Animator.StringToHash("PullOut");
        private static readonly int WeaponIndexHash = Animator.StringToHash("WeaponIndex");
        private static readonly NetworkVariable<int> MissingEquippedWeaponIndexState = new(-1);

        private const string FpLightRigRootName = "FpWeaponLightRig";
        private const string FpKeyLightName = "FpWeaponKeyLight";
        private const string FpFillLightName = "FpWeaponFillLight";

        internal const string FpLightRigRootNameConst = FpLightRigRootName;
        internal const string FpKeyLightNameConst = FpKeyLightName;
        internal const string FpFillLightNameConst = FpFillLightName;

        #endregion

        #region Coordinator Fields

        private WeaponAuthorityCoordinator _authorityCoordinator;
        private WeaponLoadoutCoordinator _loadoutCoordinator;
        private WeaponSwitchCoordinator _switchCoordinator;
        private WeaponFpPresentationCoordinator _fpPresentationCoordinator;
        private WeaponFpLightingCoordinator _fpLightingCoordinator;

        #endregion

        #region Runtime State

        private MatchPlayerStateProxy _cachedPlayerState;
        private MatchPlayerStateProxy _boundPlayerState;
        private bool _hasLoggedStrictStartupValidation;

        internal List<GameObject> FpWeaponInstancesRef { get; } = new();
        internal WeaponAmmoAuthority AmmoAuthorityRef { get; } = new();
        internal WeaponKinemationBindingCatalog KinemationCatalogRef { get; } = new();
        private WeaponWorldWeaponRegistry WorldWeaponRegistryRef { get; } = new();

        internal CinemachineCamera FpCameraRef { get; private set; }
        internal Camera WeaponCameraRef { get; private set; }
        internal Transform WorldWeaponSocketRef { get; private set; }
        internal Animator PlayerAnimatorRef { get; private set; }
        internal PlayerRenderer PlayerRendererRef { get; private set; }

        internal GameObject PendingTpWeapon { get; set; }
        internal GameObject CurrentWorldWeaponInstanceInternal { get; set; }
        internal GameObject PrimaryHolsterInternal { get; set; }
        internal GameObject SecondaryHolsterInternal { get; set; }
        internal int CurrentWeaponIndexInternal { get; set; } = -1;
        internal Weapon CurrentWeaponInternal { get; private set; }
        internal bool IsPullingOutInternal { get; set; }

        internal int ServerAuthoritativeWeaponIndex { get; set; } = -1;
        internal int ServerReloadWeaponIndex { get; set; } = -1;
        internal float ServerPullOutBlockedUntilTime { get; set; }
        internal int LastApprovedWeaponIndex { get; set; } = -1;
        internal int PendingPredictedWeaponIndex { get; set; } = -1;
        internal int PendingHolsterHideSlot { get; set; } = -1;
        internal bool SuppressLoadoutRebuildCallbacks { get; set; }
        internal bool DeferTpRevealUntilRespawn { get; set; }
        internal GameObject DeferredRespawnWorldWeapon { get; set; }
        internal Coroutine KinemationPullOutCompletionCoroutine { get; set; }
        internal bool RequiresKinemationEquipCompleteForCurrentPullOut { get; set; }

        internal Transform FpLightRigRoot { get; set; }
        internal Light FpKeyLight { get; set; }
        internal Light FpFillLight { get; set; }
        internal bool LoggedMissingWeaponLayer { get; set; }

        #endregion

        #region Public Properties

        public Weapon CurrentWeapon => CurrentWeaponInternal;
        public GameObject CurrentWorldWeaponInstance => CurrentWorldWeaponInstanceInternal;
        public int CurrentWeaponIndex => CurrentWeaponIndexInternal;
        public int WeaponCount => weaponDataList != null ? weaponDataList.Count : 0;
        public IReadOnlyList<WeaponData> PrimaryWeaponOptions => KinemationCatalogRef.PrimaryWeaponOptions;
        public IReadOnlyList<WeaponData> SecondaryWeaponOptions => KinemationCatalogRef.SecondaryWeaponOptions;
        public bool IsPullingOut => IsPullingOutInternal;
        public GameObject PrimaryHolster => PrimaryHolsterInternal;
        public GameObject SecondaryHolster => SecondaryHolsterInternal;

        #endregion

        #region Internal Properties

        internal PlayerController PlayerControllerRef => playerController;
        internal List<WeaponData> WeaponDataListRef => weaponDataList;
        internal GameObject KinemationFpsPlayerPrefabRef => kinemationFpsPlayerPrefab;
        internal float KinemationSprintWalkGaitValue => kinemationSprintWalkGaitValue;
        internal float KinemationEquipUnlockNormalizedTime => kinemationEquipUnlockNormalizedTime;
        internal bool AutoCompleteKinemationPullOut => autoCompleteKinemationPullOut;
        internal float KinemationPullOutCompleteDelay => kinemationPullOutCompleteDelay;
        internal float PostMatchPullOutFailSafeDelay => postMatchPullOutFailSafeDelay;
        internal Vector3 KinemationViewmodelLocalPosition => kinemationViewmodelLocalPosition;
        internal Vector3 KinemationViewmodelLocalEulerAngles => kinemationViewmodelLocalEulerAngles;
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
        internal int PullOutHashInternal => PullOutHash;
        internal int WeaponIndexHashInternal => WeaponIndexHash;
        internal NetworkVariable<int> ReplicatedEquippedWeaponIndex =>
            ResolvePlayerState() != null ? ResolvePlayerState().equippedWeaponIndex : MissingEquippedWeaponIndexState;

        private bool HasWeaponAuthority => NetworkAuthority.HasGlobalAuthority(this);

        #endregion

        #region Unity Lifecycle

        private void Awake() {
            InitializeCoordinators();
            ValidateComponents();
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            InitializeCoordinators();
            ValidateComponents();
            TryBindPlayerStateSubscriptions();
        }

        public override void OnNetworkDespawn() {
            base.OnNetworkDespawn();
            UnbindPlayerStateSubscriptions();
        }

        private void Update() {
            _switchCoordinator?.UpdateKinemationEquipCompletionGate();
            if(IsOwner) {
                _fpLightingCoordinator?.EnsureFpWeaponLightingRig();
            }
        }

        #endregion

        #region Public Facade

        public void InitializeWeapons() => _loadoutCoordinator.InitializeWeapons();
        public void ApplyTpWeaponStateOnRespawn() => _loadoutCoordinator.ApplyTpWeaponStateOnRespawn();
        public WeaponData GetWeaponDataByIndex(int index) =>
            index >= 0 && weaponDataList != null && index < weaponDataList.Count ? weaponDataList[index] : null;
        public string GetWeaponIdByIndex(int index) => GetWeaponDataByIndex(index) != null ? GetWeaponDataByIndex(index).weaponName : string.Empty;
        public void RefreshOwnerAmmoHudFromCurrentWeapon() => _authorityCoordinator.RefreshOwnerAmmoHudFromCurrentWeapon();
        public void ResetAllWeaponAmmo() => _authorityCoordinator.ResetAllWeaponAmmo();
        public void PrepareCurrentWeaponForPostMatchPodium() => _authorityCoordinator.PrepareCurrentWeaponForPostMatchPodium();
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
        public int GetPrimarySelectionIndex() => _loadoutCoordinator.GetPrimarySelectionIndex();
        public int GetSecondarySelectionIndex() => _loadoutCoordinator.GetSecondarySelectionIndex();
        public bool ApplyOwnerLoadoutSelection(int primaryIndex, int secondaryIndex, bool deferTpRevealUntilRespawn = true) =>
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
        public void ProcessWeaponSwitchAuthorityRequest(int newIndex) => _switchCoordinator.ProcessWeaponSwitchAuthorityRequest(newIndex);
        public GameObject GetCurrentFpWeapon() => _fpPresentationCoordinator.GetCurrentFpWeapon();
        public GameObject GetCurrentFpWeaponHolderRootForDisconnectDuplicate() =>
            _fpPresentationCoordinator.GetCurrentFpWeaponHolderRootForDisconnectDuplicate();
        public void UpdateAllFpArmTagGlow(bool isTagged) => _fpPresentationCoordinator.UpdateAllFpArmTagGlow(isTagged);
        public void SetCurrentFpWeaponVisible(bool visible) => _fpPresentationCoordinator.SetCurrentFpWeaponVisible(visible);
        public void HideFpVisualsForDisconnectTransition() => _fpPresentationCoordinator.HideFpVisualsForDisconnectTransition();
        public void OffsetCurrentFpWeapon(Vector3 localPosition, Vector3 localEulerAngles) =>
            _fpPresentationCoordinator.OffsetCurrentFpWeapon(localPosition, localEulerAngles);

        public static bool IsFriendlyFireServer(PlayerController shooter, PlayerController victim) {
            if(shooter == null || victim == null) return false;
            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings == null || !matchSettings.IsCurrentModeTeamBased()) return false;
            if(shooter.TeamManager == null || victim.TeamManager == null) return false;
            return shooter.TeamManager.netTeam.Value == victim.TeamManager.netTeam.Value;
        }

        #endregion

        #region Internal Facade

        internal void OnWeaponIndexChangedInternal(int oldValue, int newValue) => _loadoutCoordinator.OnWeaponIndexChanged(oldValue, newValue);
        internal void ApplyDrainedAmmoOwnerClient(int weaponIndex, int ammo, int magSize) => _authorityCoordinator.ApplyDrainedAmmoOwnerClient(weaponIndex, ammo, magSize);
        internal void ReportWeaponStateSyncServer(int weaponIndex, AmmoSyncReason reason, int localAmmoAfterEvent, RpcParams rpcParams) =>
            _authorityCoordinator.ReportWeaponStateSyncServer(weaponIndex, reason, localAmmoAfterEvent, rpcParams);
        internal void ResetAllWeaponAmmoServer(RpcParams rpcParams) => _authorityCoordinator.ResetAllWeaponAmmoServer(rpcParams);
        internal void ReportShotFiredServer(int weaponIndex, ulong shotId, float clientShotTime, RpcParams rpcParams) =>
            _authorityCoordinator.ReportShotFiredServer(weaponIndex, shotId, clientShotTime, rpcParams);
        internal void UpdateServerWeaponStateOnAuthority(int weaponIndex, AmmoSyncReason reason, int localAmmoAfterEvent) =>
            _authorityCoordinator.UpdateServerWeaponStateOnAuthority(weaponIndex, reason, localAmmoAfterEvent);
        internal void ResetAllWeaponAmmoOnAuthority() => _authorityCoordinator.ResetAllWeaponAmmoOnAuthority();
        internal GameObject ActivateFpWeaponInternal(int weaponIndex, WeaponData data, bool triggerPullOutAnimation) =>
            _fpPresentationCoordinator.ActivateFpWeapon(weaponIndex, data, triggerPullOutAnimation);
        internal void InstantiateFpWeaponInstancesInternal() => _fpPresentationCoordinator.InstantiateFpWeaponInstances();
        internal void DestroyFpWeaponInstancesInternal() => _fpPresentationCoordinator.DestroyFpWeaponInstances();
        internal bool TryGetKinemationDriverInternal(GameObject fpWeaponRoot, out KinemationFpWeaponDriver driver) =>
            _fpPresentationCoordinator.TryGetKinemationDriver(fpWeaponRoot, out driver);
        internal void ApplyResolvedKinemationViewmodelPoseInternal(GameObject fpWeaponRoot, KinemationWeaponBinding binding) =>
            _fpPresentationCoordinator.ApplyResolvedKinemationViewmodelPose(fpWeaponRoot, binding);
        internal int GetFpWeaponLayerInternal() => _fpPresentationCoordinator.GetFpWeaponLayer();
        internal void SetupFpWeaponSkinnedMeshRenderersInternal(GameObject fpWeaponInstance) =>
            _fpPresentationCoordinator.SetupFpWeaponSkinnedMeshRenderers(fpWeaponInstance);
        internal void EnsureHierarchyActiveInternal(GameObject instanceRoot) => _fpPresentationCoordinator.EnsureHierarchyActive(instanceRoot);
        internal void EnsureFpWeaponLightingRigInternal() => _fpLightingCoordinator.EnsureFpWeaponLightingRig();
        internal int GetSlotForIndexInternal(int index) => _loadoutCoordinator.GetSlotForIndexInternal(index);
        internal void ResolveCurrentWorldWeaponReferenceInternal() => _loadoutCoordinator.ResolveCurrentWorldWeaponReferenceInternal();
        internal void EquipInitialWeaponInternal(int index) => EquipInitialWeapon(index);
        internal void EnsureWorldWeaponShadowStateInternal() => _switchCoordinator.EnsureWorldWeaponShadowStateInternal();
        internal void EnsureWeaponHierarchyActiveInternal() => _switchCoordinator.EnsureWeaponHierarchyActiveInternal();
        internal void ApplyServerAuthoritativeWeaponSwitch(int weaponIndex) => _authorityCoordinator.ApplyServerAuthoritativeWeaponSwitch(weaponIndex);
        internal void ValidateComponentsForPublicUse() => ValidateComponents();

        #endregion

        #region RPC Entry Points

        [Rpc(SendTo.Owner)]
        internal void ApplyDrainedAmmoOwnerClientRpc(int weaponIndex, int ammo, int magSize) {
            _authorityCoordinator.ApplyDrainedAmmoOwnerClient(weaponIndex, ammo, magSize);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        internal void ReportWeaponStateSyncServerRpc(int weaponIndex, AmmoSyncReason reason, int localAmmoAfterEvent,
            RpcParams rpcParams = default) {
            _authorityCoordinator.ReportWeaponStateSyncServer(weaponIndex, reason, localAmmoAfterEvent, rpcParams);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        internal void ResetAllWeaponAmmoServerRpc(RpcParams rpcParams = default) {
            _authorityCoordinator.ResetAllWeaponAmmoServer(rpcParams);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        internal void ReportShotFiredServerRpc(int weaponIndex, ulong shotId, float clientShotTime, RpcParams rpcParams = default) {
            _authorityCoordinator.ReportShotFiredServer(weaponIndex, shotId, clientShotTime, rpcParams);
        }

        [Rpc(SendTo.Owner)]
        internal void RejectPredictedWeaponSwitchOwnerRpc(int approvedWeaponIndex) {
            _switchCoordinator.RejectPredictedWeaponSwitchOwner(approvedWeaponIndex);
        }

        #endregion

        #region Private Root Helpers

        private void InitializeCoordinators() {
            _authorityCoordinator ??= new WeaponAuthorityCoordinator(this);
            _loadoutCoordinator ??= new WeaponLoadoutCoordinator(this);
            _switchCoordinator ??= new WeaponSwitchCoordinator(this);
            _fpPresentationCoordinator ??= new WeaponFpPresentationCoordinator(this);
            _fpLightingCoordinator ??= new WeaponFpLightingCoordinator(this);
        }

        private void ValidateComponents() {
            if(playerController == null) {
                playerController = GetComponentInParent<PlayerController>();
            }

            if(CurrentWeaponInternal == null) {
                CurrentWeaponInternal = GetComponent<Weapon>();
            }

            if(FpCameraRef == null && playerController != null) {
                FpCameraRef = playerController.FpCamera;
            }

            if(WeaponCameraRef == null && FpCameraRef != null) {
                WeaponCameraRef = FpCameraRef.GetComponentInChildren<Camera>(true);
            }

            if(WorldWeaponSocketRef == null && playerController != null) {
                WorldWeaponSocketRef = playerController.WorldWeaponSocket;
            }

            if(PlayerAnimatorRef == null && playerController != null) {
                PlayerAnimatorRef = playerController.PlayerAnimator;
            }

            if(PlayerRendererRef == null && playerController != null) {
                PlayerRendererRef = playerController.PlayerRenderer;
            }
        }

        internal void BuildKinemationWeaponLookup() {
            KinemationCatalogRef.Rebuild(kinemationWeaponBindings, ResolveWeaponSlot, Debug.LogError);
        }

        internal bool TryGetKinemationBindingForData(WeaponData weaponData, out KinemationWeaponBinding binding) {
            return KinemationCatalogRef.TryGetBinding(kinemationFpsPlayerPrefab, weaponData, out binding);
        }

        internal static int ResolveKinemationWeaponCapacity(GameObject kinemationWeaponPrefab) {
            if(kinemationWeaponPrefab == null) return 0;
            var settings = kinemationWeaponPrefab.GetComponentInChildren<FPSWeaponSettings>(true);
            return settings != null ? Mathf.Max(0, settings.ammo) : 0;
        }

        internal int ResolveWeaponCapacity(WeaponData data) {
            if(data == null) return 0;
            if(!TryGetKinemationBindingForData(data, out var binding) || binding == null) return 0;
            return ResolveKinemationWeaponCapacity(binding.kinemationWeaponPrefab);
        }

        internal bool TryValidateSwitchTargetStrict(int newIndex, out WeaponData data, out int magCapacity) {
            magCapacity = 0;
            data = GetWeaponDataByIndex(newIndex);
            if(data == null) return false;
            magCapacity = ResolveWeaponCapacity(data);
            return magCapacity > 0;
        }

        internal void LogStrictStartupValidationOnce() {
            if(_hasLoggedStrictStartupValidation) return;
            _hasLoggedStrictStartupValidation = true;
            if(weaponDataList == null || weaponDataList.Count == 0) {
                Debug.LogWarning("[WeaponManager] No equipped weapons configured during strict startup validation.");
            }
        }

        internal bool BuildWorldWeaponLookup() {
            return WorldWeaponRegistryRef.Rebuild(WorldWeaponSocketRef, Debug.LogError);
        }

        internal GameObject ResolveWorldWeaponObject(WeaponData weaponData) => WorldWeaponRegistryRef.Resolve(weaponData);
        internal GameObject ResolveHolsterWeaponObject(WeaponData weaponData) => WorldWeaponRegistryRef.ResolveHolster(weaponData);
        internal int ResolveRestoredAmmo(int weaponIndex, int magCapacity, bool seedWhenMissing) =>
            AmmoAuthorityRef.ResolveRestoredAmmo(weaponIndex, magCapacity, seedWhenMissing);

        internal void RefreshOwnerHolsterShadowState() {
            if(!IsOwner || playerController == null || playerController.PlayerShadow == null) return;
            playerController.PlayerShadow.SetWorldWeaponRenderersShadowMode(UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly);
        }

        internal MatchPlayerStateProxy ResolvePlayerState() {
            if(_cachedPlayerState != null) {
                return _cachedPlayerState;
            }

            if(playerController == null || playerController.NetworkObject == null || !playerController.NetworkObject.IsSpawned) {
                return null;
            }

            var ownerClientId = playerController.OwnerClientId;
            if(MatchPlayerStateProxy.TryGetForPlayer(ownerClientId, out var playerState)) {
                _cachedPlayerState = playerState;
                return playerState;
            }

            return null;
        }

        private void OnPlayerStateRegistered(ulong playerClientId, MatchPlayerStateProxy proxy) {
            if(playerController == null || playerController.NetworkObject == null || !playerController.NetworkObject.IsSpawned) return;
            if(playerClientId != playerController.OwnerClientId) return;
            _cachedPlayerState = proxy;
            TryBindPlayerStateSubscriptions();
        }

        private void OnPlayerStateUnregistered(ulong playerClientId, MatchPlayerStateProxy proxy) {
            if(_boundPlayerState != proxy) return;
            if(playerController != null && playerController.NetworkObject != null && playerController.NetworkObject.IsSpawned &&
               playerClientId != playerController.OwnerClientId) {
                return;
            }

            UnbindPlayerStateSubscriptions();
            _cachedPlayerState = null;
        }

        private void TryBindPlayerStateSubscriptions() {
            MatchPlayerStateProxy.StateRegistered -= OnPlayerStateRegistered;
            MatchPlayerStateProxy.StateRegistered += OnPlayerStateRegistered;
            MatchPlayerStateProxy.StateUnregistered -= OnPlayerStateUnregistered;
            MatchPlayerStateProxy.StateUnregistered += OnPlayerStateUnregistered;

            var playerState = ResolvePlayerState();
            if(playerState == null || _boundPlayerState == playerState) return;

            UnbindPlayerStateSubscriptions();
            playerState.equippedWeaponIndex.OnValueChanged += OnReplicatedEquippedWeaponIndexChanged;
            _boundPlayerState = playerState;
        }

        private void UnbindPlayerStateSubscriptions() {
            MatchPlayerStateProxy.StateRegistered -= OnPlayerStateRegistered;
            MatchPlayerStateProxy.StateUnregistered -= OnPlayerStateUnregistered;

            if(_boundPlayerState != null) {
                _boundPlayerState.equippedWeaponIndex.OnValueChanged -= OnReplicatedEquippedWeaponIndexChanged;
                _boundPlayerState = null;
            }
        }

        private void OnReplicatedEquippedWeaponIndexChanged(int oldValue, int newValue) {
            if(newValue < 0 || newValue >= weaponDataList.Count) {
                return;
            }

            LastApprovedWeaponIndex = newValue;
            PendingPredictedWeaponIndex = -1;

            if(HasWeaponAuthority) {
                ServerAuthoritativeWeaponIndex = newValue;
            }

            if(IsOwner) {
                _switchCoordinator.ApplyApprovedLocalWeaponSwitch(newValue, false);
            } else {
                _switchCoordinator.ApplyRemoteWeaponSwitch(newValue);
            }
        }

        private void EquipInitialWeapon(int index) {
            CurrentWeaponIndexInternal = Mathf.Clamp(index, 0, Mathf.Max(0, weaponDataList.Count - 1));
            LastApprovedWeaponIndex = CurrentWeaponIndexInternal;
            PendingPredictedWeaponIndex = -1;

            if(ResolvePlayerState() != null) {
                ServerAuthoritativeWeaponIndex = CurrentWeaponIndexInternal;
            }

            var data = GetWeaponDataByIndex(CurrentWeaponIndexInternal);
            if(data == null || CurrentWeaponInternal == null) return;

            var fpWeapon = ActivateFpWeaponInternal(CurrentWeaponIndexInternal, data, false);
            var magCapacity = ResolveWeaponCapacity(data);
            var restoredAmmo = ResolveRestoredAmmo(CurrentWeaponIndexInternal, magCapacity, true);

            CurrentWorldWeaponInstanceInternal = ResolveWorldWeaponObject(data);
            if(CurrentWorldWeaponInstanceInternal != null) {
                CurrentWorldWeaponInstanceInternal.SetActive(true);
            }

            CurrentWeaponInternal.SwitchToWeapon(data, fpWeapon, CurrentWorldWeaponInstanceInternal, restoredAmmo, magCapacity);
            EnsureWorldWeaponShadowStateInternal();
            EnsureWeaponHierarchyActiveInternal();
        }

        private static int ResolveWeaponSlot(WeaponData data) {
            if(data == null) return -1;
            var slot = data.WeaponSlotIndex;
            return slot is 0 or 1 ? slot : -1;
        }

        #endregion
    }
}
