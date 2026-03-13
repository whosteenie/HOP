using System.Collections;
using System.Collections.Generic;
using Audio.Networking;
using Game.Player.Core;
using Game.UI;
using Game.Weapons.Manager;
using Game.Weapons.World;
using Network.Core;
using Network.Events;
using Network.Rpc;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.VFX;

namespace Game.Weapons.Core {
    public class Weapon : NetworkBehaviour {
        public const float MaxDamageMultiplier = 3f;

        [Header("References")]
        [SerializeField] private PlayerController playerController;

        private CinemachineCamera _fpCamera;
        private Animator _playerAnimator;
        private WeaponMountCoordinator _mountCoordinator;
        private WeaponCombatCoordinator _combatCoordinator;
        private WeaponReloadCoordinator _reloadCoordinator;
        private WeaponEffectsCoordinator _effectsCoordinator;

        private int _currentMagCapacity = 1;

        [Header("Runtime State")]
        public int currentAmmo;

        private bool IsReloading { get; set; }
        public bool IsReloadInProgress => IsReloading;

        private static readonly NetworkVariable<float> MissingDamageMultiplierState = new(1f);

        public NetworkVariable<float> NetCurrentDamageMultiplier =>
            playerController != null ? playerController.PlayerState != null ? playerController.PlayerState.replicatedDamageMultiplier ?? MissingDamageMultiplierState : MissingDamageMultiplierState : MissingDamageMultiplierState;

        private float CurrentDamageMultiplier {
            get => IsOwner ? _localDamageMultiplier : NetCurrentDamageMultiplier.Value;
            set {
                if(!IsOwner) return;
                // Throttle network updates - only send if enough time has passed or value changed significantly
                // At 90Hz: 5 ticks = ~55ms
                const float damageMultiplierUpdateInterval = 0.055f;
                const float changeThreshold = 0.05f; // 5% change threshold

                var shouldUpdate = LastDamageMultiplierUpdateTime == 0f ||
                                   Time.time - LastDamageMultiplierUpdateTime >= damageMultiplierUpdateInterval ||
                                   Mathf.Abs(_localDamageMultiplier - value) > changeThreshold;

                if(!shouldUpdate) return;
                _localDamageMultiplier = value;
                LastDamageMultiplierUpdateTime = Time.time;
                EventBus.Publish(new UpdateMultiplierEvent(_localDamageMultiplier, MaxDamageMultiplier));
            }
        }

        // Throttling for damage multiplier updates

        [Header("Speed Damage Scaling")]
        internal const float MinSpeedThreshold = 15f;

        internal const float MaxSpeedThreshold = 28f;

        internal const float MultiplierDecayRate = 4.5f;
        internal const float MultiplierGainRate = 2f;

        internal const float MultiplierGracePeriod = 1f;

        [Header("Visual Settings")]
        internal const float BulletSpeed = 500f;

        internal const float MuzzleLightTime = 5f;

        #region Private Fields

        private float _localDamageMultiplier = 1f;

        internal const float ReloadRecoveryCooldownSeconds = 0.5f;
        internal const float KinemationReloadFallbackSeconds = 5f;
        internal const float TracerPerpendicularVelocityInheritanceScale = 1f;
        internal const float TracerPerpendicularVelocityInheritanceMax = 24f;
        internal const float TracerPerpendicularVelocityFadeExponent = 1f;

        // Bullet trail pooling
        internal const int TrailPoolSize = 30;

        #endregion

        #region Animation Hashes

        private static readonly int RecoilHash = Animator.StringToHash("Recoil");
        private static readonly int ReloadHash = Animator.StringToHash("Reload");

        #endregion

        #region Internal Facade Properties

        internal PlayerController PlayerController => playerController;
        internal WeaponManager Manager { get; private set; }

        private NetworkDamageRelay DamageRelay { get; set; }

        internal NetworkFxRelay FxRelay { get; private set; }

        internal NetworkAudioRelay AudioRelay { get; private set; }

        internal KinemationFpWeaponDriver KinemationDriver { get; set; }

        internal GameObject CurrentFpWeaponInstance { get; set; }

        internal GameObject CurrentWorldWeaponInstance { get; set; }

        internal WorldWeaponBinding CurrentWorldWeaponBinding { get; set; }

        internal Transform FpMuzzleTransform { get; set; }

        internal Transform WorldMuzzleTransform { get; set; }

        internal GameObject FpMuzzleLight { get; set; }

        internal GameObject WorldMuzzleLight { get; set; }

        internal GameObject KinemationLocalMuzzleFxInstance { get; set; }

        internal VisualEffect KinemationLocalMuzzleVfx { get; set; }

        internal GameObject KinemationLocalMuzzleSourcePrefab { get; set; }

        internal int CurrentMagCapacity {
            set => _currentMagCapacity = Mathf.Max(1, value);
        }
        internal int CurrentAmmo {
            get => currentAmmo;
            set => currentAmmo = value;
        }
        internal bool Reloading {
            get => IsReloading;
            set => IsReloading = value;
        }
        internal float LastFireTime { get; set; }

        internal float CurrentDamageMultiplierValue {
            get => CurrentDamageMultiplier;
            set => CurrentDamageMultiplier = value;
        }
        internal float PeakDamageMultiplier { get; set; } = 1f;

        internal float LastPeakTime { get; set; }

        internal bool AutoReloadArmed { get; set; }

        internal float ReloadExpectedCompleteTime { get; set; }

        internal float NextReloadRecoveryAllowedTime { get; set; }

        internal List<int> KinemationWeaponSoundEventBuffer { get; } = new();

        internal float KinemationReloadFallbackDeadline { get; set; }

        private float LastDamageMultiplierUpdateTime { get; set; }

        internal float AuthoritativeDamageMultiplier { get; set; } = 1f;

        internal float AuthoritativePeakDamageMultiplier { get; set; } = 1f;

        internal float AuthoritativeLastPeakTime { get; set; }

        internal Vector3 LastAuthorityObservedPosition { get; set; }

        internal float LastAuthorityObservedTime { get; set; }

        internal bool HasAuthorityObservedPosition { get; set; }

        internal Queue<TrailRenderer> TrailPool { get; } = new();

        internal bool HasPrewarmedKinemationMuzzleForCurrentWeapon { get; set; }

        internal bool HasLocalMuzzleFlashSpawnPositionForShot { get; set; }

        internal Vector3 LocalMuzzleFlashSpawnPositionForShot { get; set; }

        internal float FpLightOffTime { get; set; }

        internal float WorldLightOffTime { get; set; }

        internal LayerMask EnemyLayerMask { get; private set; }

        internal LayerMask WorldLayerMask { get; private set; }

        #endregion

        #region Unity Lifecycle

        private void Awake() {
            ValidateComponents();
            _mountCoordinator = new WeaponMountCoordinator(this);
            _combatCoordinator = new WeaponCombatCoordinator(this);
            _reloadCoordinator = new WeaponReloadCoordinator(this);
            _effectsCoordinator = new WeaponEffectsCoordinator(this);
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            _combatCoordinator.ResetAuthorityObservedMotionBaseline();
        }

        private void ValidateComponents() {
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(playerController == null) {
                Debug.LogError("[Weapon] PlayerController not found!");
                enabled = false;
                return;
            }

            if(_fpCamera == null) _fpCamera = playerController.FpCamera;
            if(_playerAnimator == null) _playerAnimator = playerController.PlayerAnimator;
            EnemyLayerMask = playerController.EnemyLayer;
            WorldLayerMask = playerController.WorldLayer;
            if(DamageRelay == null) DamageRelay = playerController.DamageRelay;
            if(FxRelay == null) FxRelay = playerController.FxRelay;
            if(AudioRelay == null) AudioRelay = playerController.AudioRelay;
            if(Manager == null) Manager = playerController.WeaponManager;

            LastFireTime = Time.time;

            if(DamageRelay == null) return;
            DamageRelay.OnHitConfirm -= OnHitConfirm;
            DamageRelay.OnHitConfirm += OnHitConfirm;
        }

        private void LateUpdate() {
            _combatCoordinator.UpdateLocalDamageMultiplier();
            _combatCoordinator.UpdateAuthoritativeDamageMultiplier();
            _reloadCoordinator.UpdateKinemationReloadState();
            _effectsCoordinator.ProcessKinemationSoundEvents();
            _reloadCoordinator.RunReloadWatchdog();

            if(FpMuzzleLight != null && FpMuzzleLight.activeSelf && Time.time >= FpLightOffTime) {
                FpMuzzleLight.SetActive(false);
            }

            // Turn off 3P light when time is up
            if(WorldMuzzleLight != null && WorldMuzzleLight.activeSelf && Time.time >= WorldLightOffTime) {
                WorldMuzzleLight.SetActive(false);
            }
        }

        private void Update() {
            _mountCoordinator.TryPrewarmKinemationMuzzleIfNeeded();
            _mountCoordinator.SyncKinemationLocomotion();
        }

        public override void OnDestroy() {
            if(DamageRelay != null) {
                DamageRelay.OnHitConfirm -= OnHitConfirm;
            }

            ClearKinemationLocalMuzzleFxInstance();
            base.OnDestroy();
        }

        private static void OnHitConfirm(bool wasKill) {
            if(Audio2.AudioService.Instance == null) return;

            var soundId = wasKill ? "ui.hit.hitmarker.kill" : "ui.hit.hitmarker.hit";
            Audio2.AudioService.Instance.Play(soundId, Vector3.zero);
        }

        #endregion

        #region Weapon Switching

        /// <summary>
        /// Called from FP weapon animation event when pull out animation completes.
        /// Releases control by clearing IsPullingOut flag.
        /// </summary>
        public void OnPullOutCompleted() {
            if(Manager != null)
                Manager.HandlePullOutCompleted();
        }

        /// <summary>
        /// Switch to a new weapon by loading its data
        /// </summary>
        public void SwitchToWeapon(WeaponData newWeaponData, GameObject fpWeaponInstance,
            GameObject worldWeaponInstance, int restoredAmmo, int magCapacity) {
            _mountCoordinator.SwitchToWeapon(newWeaponData, fpWeaponInstance, worldWeaponInstance, restoredAmmo,
                magCapacity);
        }

        public bool TryGetRemoteWorldMuzzlePosition(out Vector3 muzzlePosition) {
            return _mountCoordinator.TryGetRemoteWorldMuzzlePosition(out muzzlePosition);
        }

        #endregion

        #region Public Methods

        public void Shoot() {
            if(!_combatCoordinator.CanFire()) {
                _combatCoordinator.HandleCannotFire();
                return;
            }

            _combatCoordinator.PerformShot();
            _effectsCoordinator.PlayFireSound();
        }

        public bool TryAutoReloadFromEmptyClick() {
            return _reloadCoordinator.TryAutoReloadFromEmptyClick();
        }

        public void StartReload() {
            _reloadCoordinator.StartReload();
        }

        public void CancelReloadForWeaponSwitch() {
            _reloadCoordinator.CancelReloadForWeaponSwitch();
        }

        private void SyncServerWeaponState(WeaponManager.AmmoSyncReason reason) {
            if(Manager != null) Manager.ReportWeaponStateSync(Manager.CurrentWeaponIndex, reason, currentAmmo);
        }

        private void PublishOwnerAmmoToHud(int maxAmmoOverride = -1) {
            if(playerController == null || !playerController.IsOwner) return;
            if(HUDManager.Instance == null) return;
            var maxAmmo = maxAmmoOverride > 0 ? maxAmmoOverride : GetCurrentMagCapacity();
            EventBus.Publish(new UpdateAmmoEvent(currentAmmo, maxAmmo));
        }

        public void ResetWeapon() {
            _reloadCoordinator.ResetWeapon();
        }

        public void ResetAuthoritativeDamageMultiplierImmediate() {
            if(NetworkAuthority.HasGlobalAuthority(this)) {
                AuthoritativeDamageMultiplier = 1f;
                AuthoritativePeakDamageMultiplier = 1f;
                AuthoritativeLastPeakTime = 0f;
                ResetAuthorityObservedMotionBaseline();
                if(playerController != null && playerController.PlayerState != null) {
                    playerController.PlayerState.replicatedDamageMultiplier.Value = 1f;
                }
            }

            if(!IsOwner) return;
            _localDamageMultiplier = 1f;
            LastDamageMultiplierUpdateTime = Time.time;
            PeakDamageMultiplier = 1f;
            LastPeakTime = 0f;
            EventBus.Publish(new UpdateMultiplierEvent(_localDamageMultiplier, MaxDamageMultiplier));
        }

        public float GetAuthoritativeDamageMultiplier() {
            return Mathf.Clamp(AuthoritativeDamageMultiplier, 1f, MaxDamageMultiplier);
        }

        private void ResetAuthorityObservedMotionBaseline() {
            _combatCoordinator.ResetAuthorityObservedMotionBaseline();
        }

        public void PrepareForPostMatchPodium() {
            _reloadCoordinator.PrepareForPostMatchPodium();
        }

        #endregion

        #region Getters

        [field: Header("Current Weapon State")]
        public WeaponData CurrentWeaponData { get; set; }

        private int GetCurrentMagCapacity() {
            return Mathf.Max(1, _currentMagCapacity);
        }

        public int GetMagSize() {
            return GetCurrentMagCapacity();
        }

        public GameObject GetWeaponPrefab() => CurrentFpWeaponInstance;

        #endregion

        #region Internal Coordinator Facade

        internal int GetCurrentMagCapacityInternal() {
            return GetCurrentMagCapacity();
        }

        internal void SyncServerWeaponStateInternal(WeaponManager.AmmoSyncReason reason) {
            SyncServerWeaponState(reason);
        }

        internal void PublishOwnerAmmoToHudInternal(int maxAmmoOverride = -1) {
            PublishOwnerAmmoToHud(maxAmmoOverride);
        }

        internal void PlayLocalMuzzleFlashInternal(int authoritativeAmmoBeforeShot) {
            _effectsCoordinator.PlayLocalMuzzleFlash(authoritativeAmmoBeforeShot);
        }

        internal void PlayDryFireSoundInternal() {
            _effectsCoordinator.PlayDryFireSound();
        }

        internal void PlayReloadEffectsInternal() {
            _effectsCoordinator.PlayReloadEffects();
        }

        internal void ExitReloadAnimationInternal() {
            _effectsCoordinator.ExitReloadAnimation();
        }

        internal bool UseKinemationInternalSoundsInternal() {
            return _effectsCoordinator.UseKinemationInternalSounds();
        }

        internal bool ShouldSuppressLegacyReloadSoundInternal() {
            return _effectsCoordinator.ShouldSuppressLegacyReloadSound();
        }

        internal void StopKinemationEventSoundsForCurrentWeaponInternal() {
            _effectsCoordinator.StopKinemationEventSoundsForCurrentWeapon();
        }

        internal void ClearKinemationLocalMuzzleFxInstanceInternal() {
            _effectsCoordinator.ClearKinemationLocalMuzzleFxInstance();
        }

        internal void PrewarmKinemationLocalMuzzleFxInstanceInternal() {
            _effectsCoordinator.PrewarmKinemationLocalMuzzleFxInstance();
        }

        internal void SpawnTracerLocalInternal(Vector3 start, Vector3 end, Vector3 hitNormal, bool madeImpact,
            bool hitPlayer, NetworkObjectReference hitPlayerRef = default, Vector3 shooterVelocity = default) {
            _effectsCoordinator.SpawnTracerLocal(start, end, hitNormal, madeImpact, hitPlayer, hitPlayerRef,
                shooterVelocity);
        }

        internal IEnumerator SpawnOwnerTracerLocalAfterViewUpdateInternal(Vector3 fallbackStart, Vector3 end,
            Vector3 hitNormal, bool madeImpact, bool hitPlayer, NetworkObjectReference hitPlayerRef,
            Vector3 shooterVelocity) {
            return _effectsCoordinator.SpawnOwnerTracerLocalAfterViewUpdate(fallbackStart, end, hitNormal, madeImpact,
                hitPlayer, hitPlayerRef, shooterVelocity);
        }

        internal bool TryGetStrictWorldMuzzleTransformInternal(out Transform muzzleTransform, string context,
            bool allowOwnerInstance = false, bool logErrors = true) {
            return _mountCoordinator.TryGetStrictWorldMuzzleTransform(out muzzleTransform, context, allowOwnerInstance,
                logErrors);
        }

        internal bool TryGetRequiredOwnerMuzzleTransformInternal(out Transform muzzleTransform, string context,
            bool logErrors = true) {
            return _mountCoordinator.TryGetRequiredOwnerMuzzleTransform(out muzzleTransform, context, logErrors);
        }

        internal bool TryGetOwnerTracerStartPositionInternal(out Vector3 tracerStartPosition) {
            return _mountCoordinator.TryGetOwnerTracerStartPosition(out tracerStartPosition);
        }

        internal void InitializeTrailPoolInternal() {
            _effectsCoordinator.InitializeTrailPoolFacade();
        }

        internal void CancelReloadInternal() {
            _reloadCoordinator.CancelReload();
        }

        #endregion

        #region Private Methods - Effects

        private void ClearKinemationLocalMuzzleFxInstance() => _effectsCoordinator.ClearKinemationLocalMuzzleFxInstance();

        [Rpc(SendTo.Everyone)]
        internal void PlayShootAnimationServerRpc() {
            if(_playerAnimator != null) {
                _playerAnimator.SetTrigger(RecoilHash);
            }
        }

        /// <summary>
        /// Play muzzle flash from network (non-owners only, 3P)
        /// Called via NetworkFxRelay RPC
        /// Muzzle flash tracks the weapon muzzle each frame to avoid drift while moving fast.
        /// </summary>
        public void PlayNetworkedMuzzleFlash(Vector3 endPoint) {
            _effectsCoordinator.PlayNetworkedMuzzleFlash(endPoint);
        }

        public void SpawnTracerLocal(Vector3 start, Vector3 end, Vector3 hitNormal, bool madeImpact, bool hitPlayer,
            NetworkObjectReference hitPlayerRef = default, Vector3 shooterVelocity = default) {
            _effectsCoordinator.SpawnTracerLocal(start, end, hitNormal, madeImpact, hitPlayer, hitPlayerRef,
                shooterVelocity);
        }

        [Rpc(SendTo.Everyone)]
        internal void PlayReloadAnimationServerRpc() {
            _playerAnimator.SetTrigger(ReloadHash);
        }

        #endregion
    }
}
