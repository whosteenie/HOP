using System.Collections.Generic;
using Diagnostics;
using Events;
using Game.Match;
using Game.Weapon.Core;
using Game.Weapon.Kinemation;
using KINEMATION.FPSAnimationPack.Scripts.Weapon;
using Network.Core;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Weapon.Manager {
    public class WeaponManager : NetworkBehaviour, IKinWeaponRuntimeContext {
        public enum AmmoSyncReason : byte {
            ReloadStarted,
            ReloadSingleRound,
            ReloadCompleted,
            ReloadCanceled,
            RefillCurrentWeapon
        }

        #region Serialized Fields

        [HideInInspector, SerializeField] private MonoBehaviour ownerContextSource;

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

        private static readonly NetworkVariable<int> MissingEquippedWeaponIndexState = new(-1);

        private const string FpLightRigRootName = "FpWeaponLightRig";
        private const string FpKeyLightName = "FpWeaponKeyLight";
        private const string FpFillLightName = "FpWeaponFillLight";

        internal const string FpLightRigRootNameConst = FpLightRigRootName;
        internal const string FpKeyLightNameConst = FpKeyLightName;
        internal const string FpFillLightNameConst = FpFillLightName;

        #endregion

        #region Subsystem Fields

        private WeaponAuthority _authority;
        private WeaponLoadout _loadout;
        private WeaponSwitch _switch;
        private WeaponFpPresentation _fpPresentation;
        private WeaponFpLighting _fpLighting;

        #endregion

        #region Runtime State

        private MatchPlayerStateProxy _cachedPlayerState;
        private MatchPlayerStateProxy _boundPlayerState;
        private bool _hasLoggedStrictStartupValidation;

        internal List<GameObject> FpWeaponInstancesRef { get; } = new();
        internal WeaponAmmoAuthority AmmoAuthorityRef { get; } = new();
        internal KinWeaponBindingCatalog CatalogRef { get; } = new();
        private WeaponWorldWeaponRegistry WorldWeaponRegistryRef { get; } = new();

        internal CinemachineCamera FpCameraRef { get; private set; }
        internal Camera WeaponCameraRef { get; private set; }
        internal Transform WorldWeaponSocketRef { get; private set; }
        internal Animator PlayerAnimatorRef { get; private set; }
        internal GameObject PendingTpWeapon { get; set; }
        internal GameObject CurrentWorldWeaponInstanceInternal { get; set; }
        internal GameObject PrimaryHolsterInternal { get; set; }
        internal GameObject SecondaryHolsterInternal { get; set; }
        internal int CurrentWeaponIndexInternal { get; set; } = -1;
        internal Weapon.Core.Weapon CurrentWeaponInternal { get; private set; }
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
        internal bool IsPostMatchFlowActive { get; private set; }

        internal Transform FpLightRigRoot { get; set; }
        internal Light FpKeyLight { get; set; }
        internal Light FpFillLight { get; set; }
        internal bool LoggedMissingWeaponLayer { get; set; }

        #endregion

        #region Public Properties

        public Weapon.Core.Weapon CurrentWeapon => CurrentWeaponInternal;
        public GameObject CurrentWorldWeaponInstance => CurrentWorldWeaponInstanceInternal;
        public int CurrentWeaponIndex => CurrentWeaponIndexInternal;
        public int WeaponCount => weaponDataList != null ? weaponDataList.Count : 0;
        public IReadOnlyList<WeaponData> PrimaryWeaponOptions => CatalogRef.PrimaryWeaponOptions;
        public IReadOnlyList<WeaponData> SecondaryWeaponOptions => CatalogRef.SecondaryWeaponOptions;
        public bool IsPullingOut => IsPullingOutInternal;
        public GameObject PrimaryHolster => PrimaryHolsterInternal;
        public GameObject SecondaryHolster => SecondaryHolsterInternal;

        #endregion

        #region Internal Properties

        internal IWeaponManagerOwnerContext OwnerContext { get; private set; }

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
        internal static int PullOutHashInternal { get; } = Animator.StringToHash("PullOut");

        internal static int WeaponIndexHashInternal { get; } = Animator.StringToHash("WeaponIndex");

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
            EventBus.Subscribe<PostMatchStartedEvent>(OnPostMatchStarted);
            EventBus.Subscribe<PostMatchBlackoutReadyEvent>(OnPostMatchBlackoutReady);
            EventBus.Subscribe<MatchStartedEvent>(OnMatchStarted);
            EventBus.Subscribe<PodiumVisualsSnappedEvent>(OnPodiumVisualsSnapped);
        }

        public override void OnNetworkDespawn() {
            base.OnNetworkDespawn();
            EventBus.Unsubscribe<PostMatchStartedEvent>(OnPostMatchStarted);
            EventBus.Unsubscribe<PostMatchBlackoutReadyEvent>(OnPostMatchBlackoutReady);
            EventBus.Unsubscribe<MatchStartedEvent>(OnMatchStarted);
            EventBus.Unsubscribe<PodiumVisualsSnappedEvent>(OnPodiumVisualsSnapped);
            UnbindPlayerStateSubscriptions();
            IsPostMatchFlowActive = false;
        }

        private void Update() {
            _switch?.UpdateKinemationEquipCompletionGate();
            if(IsOwner) {
                _fpLighting?.EnsureFpWeaponLightingRig();
            }
        }

        #endregion

        #region Public Facade

        public void InitializeWeapons() => _loadout.InitializeWeapons();
        public void ApplyTpWeaponStateOnRespawn() => _loadout.ApplyTpWeaponStateOnRespawn();
        public WeaponData GetWeaponDataByIndex(int index) =>
            index >= 0 && weaponDataList != null && index < weaponDataList.Count ? weaponDataList[index] : null;
        public string GetWeaponIdByIndex(int index) => GetWeaponDataByIndex(index) != null ? GetWeaponDataByIndex(index).weaponName : string.Empty;
        public void RefreshAmmoHud() => _authority.RefreshAmmoHud();
        public void ResetAllWeaponAmmo() => _authority.ResetAllWeaponAmmo();
        public void DrainCurrentWeaponAmmoForTag() => _authority.DrainCurrentWeaponAmmoForTag();
        public bool RegisterServerShot(int weaponIndex, ulong shotId, float clientShotTime, out string reason) =>
            _authority.RegisterServerShot(weaponIndex, shotId, clientShotTime, out reason);
        public bool ValidateServerHitClaim(int weaponIndex, ulong shotId, out string reason) =>
            _authority.ValidateServerHitClaim(weaponIndex, shotId, out reason);
        public bool TryComputeServerDamage(int weaponIndex, Vector3 hitPoint, out float damage, out string reason) =>
            _authority.TryComputeServerDamage(weaponIndex, hitPoint, out damage, out reason);
        public void ReportWeaponStateSync(int weaponIndex, AmmoSyncReason reason, int localAmmoAfterEvent) =>
            _authority.ReportWeaponStateSync(weaponIndex, reason, localAmmoAfterEvent);
        public void ReportShotFired(int weaponIndex, ulong shotId, float clientShotTime) =>
            _authority.ReportShotFired(weaponIndex, shotId, clientShotTime);
        public void RegisterServerShotAndLogOnAuthority(int weaponIndex, ulong shotId, float clientShotTime) =>
            _authority.RegisterServerShotAndLogOnAuthority(weaponIndex, shotId, clientShotTime);
        public bool ApplyOwnerLoadoutSelection(int primaryIndex, int secondaryIndex, bool deferTpRevealUntilRespawn = true) =>
            _loadout.ApplyOwnerLoadoutSelection(primaryIndex, secondaryIndex, deferTpRevealUntilRespawn);
        public int GetCurrentHolsterSlot() => _loadout.GetCurrentHolsterSlot();
        public void RefreshHolsterVisibility() => _loadout.RefreshHolsterVisibility();
        public void SwitchWeapon(int newIndex) => _switch.SwitchWeapon(newIndex);
        public void ShowTpWeapon() => _switch.ShowTpWeapon();
        public void HandlePullOutCompleted() => _switch.HandlePullOutCompleted();
        public void HandleThirdPersonPullOutCompleted() => _switch.HandleThirdPersonPullOutCompleted();
        public void HandleKinemationEquipCompleted() => _switch.HandleKinemationEquipCompleted();
        WeaponData IKinWeaponRuntimeContext.GetCurrentWeaponData() => CurrentWeaponInternal != null ? CurrentWeaponInternal.CurrentWeaponData : null;
        public void TriggerPullOutAnimation() => _switch.TriggerPullOutAnimation();
        public void CancelPendingPullOutForPostMatch() => _switch.CancelPendingPullOutForPostMatch();
        public void RestoreCurrentWeaponPresentationAfterHopballDrop() => _switch.RestoreCurrentWeaponPresentationAfterHopballDrop();
        public void ProcessWeaponSwitchRequest(int newIndex) => _switch.ProcessWeaponSwitchRequest(newIndex);
        public GameObject GetCurrentFpWeapon() => _fpPresentation != null ? _fpPresentation.GetCurrentFpWeapon() : null;
        public GameObject GetFpWeaponHolderRootForDisconnect() =>
            _fpPresentation.GetFpWeaponHolderRootForDisconnect();
        public void RefreshOwnerFpWeaponVisuals() {
            if(!IsOwner || OwnerContext == null) return;

            foreach(var fpWeaponInstance in FpWeaponInstancesRef) {
                if(fpWeaponInstance == null) continue;
                RequestOwnerFpWeaponVisualRefreshInternal(fpWeaponInstance);
            }
        }

        public static bool IsFriendlyFireServer(ulong shooterClientId, ulong victimClientId) {
            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings == null || !matchSettings.IsCurrentModeTeamBased()) return false;
            if(!MatchPlayerStateProxy.TryGetForPlayer(shooterClientId, out var shooterState)) return false;
            if(!MatchPlayerStateProxy.TryGetForPlayer(victimClientId, out var victimState)) return false;
            return shooterState.teamId.Value == victimState.teamId.Value;
        }

        #endregion

        #region Internal Facade

        internal void OnWeaponIndexChangedInternal(int oldValue, int newValue) => _loadout.OnWeaponIndexChanged();
        internal void UpdateServerWeaponState(int weaponIndex, AmmoSyncReason reason, int localAmmoAfterEvent) =>
            _authority.UpdateServerWeaponState(weaponIndex, reason, localAmmoAfterEvent);
        internal void ResetAllWeaponAmmoOnAuthority() => _authority.ResetAllWeaponAmmoOnAuthority();
        internal GameObject ActivateFpWeaponInternal(int weaponIndex, WeaponData data, bool triggerPullOutAnimation) =>
            _fpPresentation.ActivateFpWeapon(weaponIndex, data, triggerPullOutAnimation);
        internal void InstantiateFpWeaponInstancesInternal() => _fpPresentation.InstantiateFpWeaponInstances();
        internal void DestroyFpWeaponInstancesInternal() => _fpPresentation.DestroyFpWeaponInstances();
        internal static bool TryGetKinemationDriverInternal(GameObject fpWeaponRoot, out KinFpWeaponDriver driver) =>
            WeaponFpPresentation.TryGetKinemationDriver(fpWeaponRoot, out driver);
        internal void ApplyKinemationViewmodelPoseInternal(GameObject fpWeaponRoot, KinemationWeaponBinding binding) =>
            _fpPresentation.ApplyKinemationViewmodelPose(fpWeaponRoot, binding);
        internal int GetFpWeaponLayerInternal() => _fpPresentation.GetFpWeaponLayer();
        internal void SetupFpWeaponSkinnedMeshRenderersInternal(GameObject fpWeaponInstance) =>
            _fpPresentation.SetupFpWeaponSkinnedMeshRenderers(fpWeaponInstance);
        internal static void EnsureHierarchyActiveInternal(GameObject instanceRoot) => KinemationViewmodelUtility.EnsureHierarchyActive(instanceRoot);
        internal void EnsureFpWeaponLightingRigInternal() => _fpLighting.EnsureFpWeaponLightingRig();
        internal int GetSlotForIndexInternal(int index) => _loadout.GetSlotForIndexInternal(index);
        internal void ResolveCurrentWorldWeaponRefInternal() => _loadout.ResolveCurrentWorldWeaponRefInternal();
        internal void EquipInitialWeaponInternal(int index) => EquipInitialWeapon(index);
        internal void EnsureWorldWeaponShadowStateInternal() => _switch.EnsureWorldWeaponShadowStateInternal();
        internal void EnsureWeaponHierarchyActiveInternal() => _switch.EnsureWeaponHierarchyActiveInternal();
        internal void ApplyServerWeaponSwitch(int weaponIndex) => _authority.ApplyServerWeaponSwitch(weaponIndex);
        internal void ValidateComponentsForPublicUse() => ValidateComponents();

        #endregion

        #region Match Event Reactions

        private void OnPostMatchStarted(PostMatchStartedEvent _) {
            IsPostMatchFlowActive = true;
        }

        private void OnPostMatchBlackoutReady(PostMatchBlackoutReadyEvent _) {
            _switch.PrepareForPostMatchPresentation();
            if(!IsOwner) return;
            _authority.PrepareCurrentWeaponForPostMatchPodium();
        }

        private void OnMatchStarted(MatchStartedEvent _) {
            IsPostMatchFlowActive = false;
        }

        private void OnPodiumVisualsSnapped(PodiumVisualsSnappedEvent evt) {
            if(evt == null || OwnerContext?.NetworkObject == null) return;
            if(evt.PlayerNetworkObjectId != OwnerContext.NetworkObjectId) return;
            _switch.SetTpWeaponIndexForPodium();
        }

        #endregion

        #region RPC Entry Points

        [Rpc(SendTo.Owner)]
        internal void ApplyDrainedAmmoOwnerClientRpc(int weaponIndex, int ammo, int magSize) {
            _authority.ApplyDrainedAmmoOwnerClient(weaponIndex, ammo, magSize);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        internal void ReportWeaponStateSyncServerRpc(int weaponIndex, AmmoSyncReason reason, int localAmmoAfterEvent,
            RpcParams rpcParams = default) {
            _authority.ReportWeaponStateSyncServer(weaponIndex, reason, localAmmoAfterEvent, rpcParams);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        internal void ResetAllWeaponAmmoServerRpc(RpcParams rpcParams = default) {
            _authority.ResetAllWeaponAmmoServer(rpcParams);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        internal void ReportShotFiredServerRpc(int weaponIndex, ulong shotId, float clientShotTime, RpcParams rpcParams = default) {
            _authority.ReportShotFiredServer(weaponIndex, shotId, clientShotTime, rpcParams);
        }

        [Rpc(SendTo.Owner)]
        internal void RejectPredictedWeaponSwitchOwnerRpc(int approvedWeaponIndex) {
            _switch.RejectPredictedWeaponSwitchOwner(approvedWeaponIndex);
        }

        [Rpc(SendTo.Owner)]
        internal void ConfirmPredictedWeaponSwitchOwnerRpc(int approvedWeaponIndex) {
            _switch.ConfirmPredictedWeaponSwitchOwner(approvedWeaponIndex);
        }

        #endregion

        #region Private Root Helpers

        private void InitializeCoordinators() {
            _authority ??= new WeaponAuthority(this);
            _loadout ??= new WeaponLoadout(this);
            _switch ??= new WeaponSwitch(this);
            _fpPresentation ??= new WeaponFpPresentation(this);
            _fpLighting ??= new WeaponFpLighting(this);
        }

        private void ValidateComponents() {
            if(OwnerContext == null) {
                if(ownerContextSource != null) {
                    // ReSharper disable once UsePatternMatching
                    var ownerContext = ownerContextSource as IWeaponManagerOwnerContext;
                    if(ownerContext != null) {
                        OwnerContext = ownerContext;
                    }
                } else {
                    foreach(var candidate in GetComponentsInParent<MonoBehaviour>(true)) {
                        if(candidate == null) continue;
                        // ReSharper disable once UseNegatedPatternMatching
                        var resolvedContext = candidate as IWeaponManagerOwnerContext;
                        if(resolvedContext == null) continue;
                        ownerContextSource = candidate;
                        OwnerContext = resolvedContext;
                        break;
                    }
                }
            }

            if(CurrentWeaponInternal == null && OwnerContext != null) {
                CurrentWeaponInternal = OwnerContext.WeaponComponent;
            }

            if(FpCameraRef == null && OwnerContext != null) {
                FpCameraRef = OwnerContext.FpCamera;
            }

            if(WeaponCameraRef == null && OwnerContext != null) {
                WeaponCameraRef = OwnerContext.WeaponCamera;
            }

            if(WorldWeaponSocketRef == null && OwnerContext != null) {
                WorldWeaponSocketRef = OwnerContext.WorldWeaponSocket;
            }

            if(PlayerAnimatorRef == null && OwnerContext != null) {
                PlayerAnimatorRef = OwnerContext.PlayerAnimator;
            }

        }

        internal void BuildKinemationWeaponLookup() {
            CatalogRef.Rebuild(kinemationWeaponBindings, ResolveWeaponSlot, Debug.LogError);
        }

        internal bool TryGetKinemationBinding(WeaponData weaponData, out KinemationWeaponBinding binding) {
            return CatalogRef.TryGetBinding(kinemationFpsPlayerPrefab, weaponData, out binding);
        }

        internal static int ResolveKinemationCapacity(GameObject kinemationWeaponPrefab) {
            if(kinemationWeaponPrefab == null) return 0;
            var fpsWeapon = kinemationWeaponPrefab.GetComponentInChildren<FPSWeapon>(true);
            var settings = fpsWeapon != null ? fpsWeapon.weaponSettings : null;
            return settings != null ? Mathf.Max(0, settings.ammo) : 0;
        }

        internal int ResolveWeaponCapacity(WeaponData data) {
            if(data == null) return 0;
            if(!TryGetKinemationBinding(data, out var binding) || binding == null) return 0;
            return ResolveKinemationCapacity(binding.kinemationWeaponPrefab);
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
                DevLog.LogWarning("[WeaponManager] No equipped weapons configured during strict startup validation.");
            }
        }

        internal bool BuildWorldWeaponLookup() {
            return WorldWeaponRegistryRef.Rebuild(WorldWeaponSocketRef, Debug.LogError);
        }

        internal GameObject ResolveWorldWeaponObject(WeaponData weaponData) => WorldWeaponRegistryRef.Resolve(weaponData);
        internal GameObject ResolveHolsterWeaponObject(WeaponData weaponData) => WorldWeaponRegistryRef.ResolveHolster(weaponData);
        internal int ResolveRestoredAmmo(int weaponIndex, int magCapacity, bool seedWhenMissing) =>
            AmmoAuthorityRef.ResolveRestoredAmmo(weaponIndex, magCapacity, seedWhenMissing);

        internal void RefreshHolsterShadowState() {
            if(!IsOwner || OwnerContext?.NetworkObject == null) return;
            EventBus.Publish(new PlayerHolsterShadowRefreshRequestedEvent(OwnerContext.NetworkObjectId));
        }

        internal void RequestOwnerFpWeaponVisualRefreshInternal(GameObject fpWeaponInstance) {
            if(!IsOwner || OwnerContext?.NetworkObject == null || fpWeaponInstance == null) return;
            EventBus.Publish(new PlayerFpWeaponVisualRefreshRequestedEvent(OwnerContext.NetworkObjectId, fpWeaponInstance));
        }

        internal MatchPlayerStateProxy ResolvePlayerState() {
            if(_cachedPlayerState != null) {
                return _cachedPlayerState;
            }

            if(OwnerContext?.NetworkObject == null || !OwnerContext.NetworkObject.IsSpawned) {
                return null;
            }

            var ownerClientId = OwnerContext.OwnerClientId;
            if(!MatchPlayerStateProxy.TryGetForPlayer(ownerClientId, out var playerState)) return null;
            _cachedPlayerState = playerState;
            return playerState;
        }

        private void OnPlayerStateRegistered(ulong playerClientId, MatchPlayerStateProxy proxy) {
            if(OwnerContext?.NetworkObject == null || !OwnerContext.NetworkObject.IsSpawned) return;
            if(playerClientId != OwnerContext.OwnerClientId) return;
            _cachedPlayerState = proxy;
            TryBindPlayerStateSubscriptions();
        }

        private void OnPlayerStateUnregistered(ulong playerClientId, MatchPlayerStateProxy proxy) {
            if(_boundPlayerState != proxy) return;
            if(OwnerContext?.NetworkObject != null && OwnerContext.NetworkObject.IsSpawned &&
               playerClientId != OwnerContext.OwnerClientId) {
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

            if(_boundPlayerState == null) return;
            _boundPlayerState.equippedWeaponIndex.OnValueChanged -= OnReplicatedEquippedWeaponIndexChanged;
            _boundPlayerState = null;
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
                _switch.ApplyApprovedLocalWeaponSwitch(newValue, false);
            } else {
                _switch.ApplyRemoteWeaponSwitch(newValue);
            }
        }

        private void EquipInitialWeapon(int index) {
            CurrentWeaponIndexInternal = Mathf.Clamp(index, 0, Mathf.Max(0, weaponDataList.Count - 1));
            LastApprovedWeaponIndex = CurrentWeaponIndexInternal;
            PendingPredictedWeaponIndex = -1;

            if(ResolvePlayerState() != null) {
                ServerAuthoritativeWeaponIndex = CurrentWeaponIndexInternal;
                if(ReplicatedEquippedWeaponIndex.Value != CurrentWeaponIndexInternal) {
                    ReplicatedEquippedWeaponIndex.Value = CurrentWeaponIndexInternal;
                }
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
