using System.Collections;
using System.Collections.Generic;
using Diagnostics;
using Events;
using Game.Audio.System;
using Game.Weapon.Kinemation;
using Game.Weapon.Manager;
using Network.Core;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.VFX;

namespace Game.Weapon.Core {
    public class Weapon : NetworkBehaviour {
        public const float MaxDamageMultiplier = 3f;

        [Header("References")]
        [SerializeField] private MonoBehaviour ownerContextSource;

        private CinemachineCamera _fpCamera;
        private Animator _playerAnimator;
        private WeaponMount _mount;
        private WeaponCombat _combat;
        private WeaponReload _reload;
        private WeaponEffects _effects;

        private int _currentMagCapacity = 1;
        private IWeaponOwnerContext _ownerContext;

        [Header("Runtime State")]
        public int currentAmmo;

        private bool IsReloading { get; set; }
        public bool IsReloadInProgress => IsReloading;

        private static readonly NetworkVariable<float> MissingDamageMultiplierState = new(1f);

        public NetworkVariable<float> NetCurrentDamageMultiplier =>
            _ownerContext != null ? _ownerContext.ReplicatedDamageMultiplierState ?? MissingDamageMultiplierState : MissingDamageMultiplierState;

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

        internal IWeaponOwnerContext OwnerContext => _ownerContext;
        internal WeaponManager Manager { get; private set; }

        private WeaponDamageRelay DamageRelay { get; set; }

        internal NetworkAudioRelay AudioRelay { get; private set; }

        internal KinFpWeaponDriver KinDriver { get; set; }

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
            _mount = new WeaponMount(this);
            _combat = new WeaponCombat(this);
            _reload = new WeaponReload(this);
            _effects = new WeaponEffects(this);
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            _combat.ResetMotionBaseline();
        }

        private void ValidateComponents() {
            if(_ownerContext == null) {
                if(ownerContextSource is IWeaponOwnerContext ownerContext) {
                    _ownerContext = ownerContext;
                } else {
                    foreach(var candidate in GetComponentsInParent<MonoBehaviour>(true)) {
                        if(candidate is IWeaponOwnerContext resolvedContext) {
                            ownerContextSource = candidate;
                            _ownerContext = resolvedContext;
                            break;
                        }
                    }
                }
            }

            if(_ownerContext == null) {
                DevLog.LogError("[Weapon] IWeaponOwnerContext not found!");
                enabled = false;
                return;
            }

            if(_fpCamera == null && _ownerContext.FpCameraTransform != null) {
                _fpCamera = _ownerContext.FpCameraTransform.GetComponent<CinemachineCamera>();
            }
            if(_playerAnimator == null) _playerAnimator = _ownerContext.PlayerAnimator;
            EnemyLayerMask = _ownerContext.EnemyLayer;
            WorldLayerMask = _ownerContext.WorldLayer;
            if(DamageRelay == null) DamageRelay = _ownerContext.DamageRelay;
            if(AudioRelay == null) AudioRelay = _ownerContext.AudioRelay;
            if(Manager == null) Manager = _ownerContext.WeaponManager;

            LastFireTime = Time.time;

            if(DamageRelay == null) return;
            DamageRelay.OnHitConfirm -= OnHitConfirm;
            DamageRelay.OnHitConfirm += OnHitConfirm;
        }

        private void LateUpdate() {
            _combat.UpdateLocalDamageMultiplier();
            _combat.UpdateDamageMultiplier();
            _reload.UpdateKinemationReloadState();
            _effects.ProcessKinemationSoundEvents();
            _reload.RunReloadWatchdog();

            if(FpMuzzleLight != null && FpMuzzleLight.activeSelf && Time.time >= FpLightOffTime) {
                FpMuzzleLight.SetActive(false);
            }

            // Turn off 3P light when time is up
            if(WorldMuzzleLight != null && WorldMuzzleLight.activeSelf && Time.time >= WorldLightOffTime) {
                WorldMuzzleLight.SetActive(false);
            }
        }

        private void Update() {
            _mount.TryPrewarmKinemationMuzzleIfNeeded();
            _mount.SyncKinemationLocomotion();
        }

        public override void OnDestroy() {
            if(DamageRelay != null) {
                DamageRelay.OnHitConfirm -= OnHitConfirm;
            }

            ClearKinemationMuzzleFx();
            base.OnDestroy();
        }

        private static void OnHitConfirm(bool wasKill) {
            if(AudioService.Instance == null) return;

            var soundId = wasKill ? "ui.hit.hitmarker.kill" : "ui.hit.hitmarker.hit";
            AudioService.Instance.Play(soundId, Vector3.zero);
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
            _mount.SwitchToWeapon(newWeaponData, fpWeaponInstance, worldWeaponInstance, restoredAmmo,
                magCapacity);
        }

        public bool TryGetRemoteWorldMuzzlePosition(out Vector3 muzzlePosition) {
            return _mount.TryGetRemoteWorldMuzzlePosition(out muzzlePosition);
        }

        #endregion

        #region Public Methods

        public void Shoot() {
            if(!_combat.CanFire()) {
                _combat.HandleCannotFire();
                return;
            }

            _combat.PerformShot();
            _effects.PlayFireSound();
        }

        public bool TryAutoReloadFromEmptyClick() {
            return _reload.TryAutoReloadFromEmptyClick();
        }

        public void StartReload() {
            _reload.StartReload();
        }

        public void CancelReloadForWeaponSwitch() {
            _reload.CancelReloadForWeaponSwitch();
        }

        private void SyncServerWeaponState(WeaponManager.AmmoSyncReason reason) {
            if(Manager != null) Manager.ReportWeaponStateSync(Manager.CurrentWeaponIndex, reason, currentAmmo);
        }

        private void PublishOwnerAmmoToHud(int maxAmmoOverride = -1) {
            if(_ownerContext == null || !_ownerContext.IsOwner) return;
            var maxAmmo = maxAmmoOverride > 0 ? maxAmmoOverride : GetCurrentMagCapacity();
            EventBus.Publish(new UpdateAmmoEvent(currentAmmo, maxAmmo));
        }

        public void ResetWeapon() {
            _reload.ResetWeapon();
        }

        public void ResetDamageMultiplierImmediate() {
            if(NetworkAuthority.HasGlobalAuthority(this)) {
                AuthoritativeDamageMultiplier = 1f;
                AuthoritativePeakDamageMultiplier = 1f;
                AuthoritativeLastPeakTime = 0f;
                ResetMotionBaseline();
                if(_ownerContext != null) {
                    _ownerContext.ReplicatedDamageMultiplierState.Value = 1f;
                }
            }

            if(!IsOwner) return;
            _localDamageMultiplier = 1f;
            LastDamageMultiplierUpdateTime = Time.time;
            PeakDamageMultiplier = 1f;
            LastPeakTime = 0f;
            EventBus.Publish(new UpdateMultiplierEvent(_localDamageMultiplier, MaxDamageMultiplier));
        }

        public float GetDamageMultiplier() {
            return Mathf.Clamp(AuthoritativeDamageMultiplier, 1f, MaxDamageMultiplier);
        }

        private void ResetMotionBaseline() {
            _combat.ResetMotionBaseline();
        }

        public void PrepareForPostMatchPodium() {
            _reload.PrepareForPostMatchPodium();
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

        #region Internal Subsystem Facade

        internal int GetMagCapacityInternal() {
            return GetCurrentMagCapacity();
        }

        internal void SyncServerWeaponStateInternal(WeaponManager.AmmoSyncReason reason) {
            SyncServerWeaponState(reason);
        }

        internal void PublishAmmoToHudInternal(int maxAmmoOverride = -1) {
            PublishOwnerAmmoToHud(maxAmmoOverride);
        }

        internal void PlayLocalMuzzleFlashInternal(int authoritativeAmmoBeforeShot) {
            _effects.PlayLocalMuzzleFlash(authoritativeAmmoBeforeShot);
        }

        internal void PlayDryFireSoundInternal() {
            _effects.PlayDryFireSound();
        }

        internal void PlayReloadEffectsInternal() {
            _effects.PlayReloadEffects();
        }

        internal void ExitReloadAnimationInternal() {
            _effects.ExitReloadAnimation();
        }

        internal bool UseKinemationInternalSoundsInternal() {
            return _effects.UseKinemationInternalSounds();
        }

        internal bool ShouldSuppressLegacyReloadSoundInternal() {
            return _effects.ShouldSuppressLegacyReloadSound();
        }

        internal void StopKinemationEventSoundsInternal() {
            _effects.StopKinemationEventSounds();
        }

        internal void ClearKinemationMuzzleFxInternal() {
            _effects.ClearKinemationMuzzleFx();
        }

        internal void PrewarmKinemationMuzzleFxInternal() {
            _effects.PrewarmKinemationMuzzleFx();
        }

        internal void SpawnTracerLocalInternal(Vector3 start, Vector3 end, Vector3 hitNormal, bool madeImpact,
            bool hitPlayer, NetworkObjectReference hitPlayerRef = default, Vector3 shooterVelocity = default) {
            _effects.SpawnTracerLocal(start, end, hitNormal, madeImpact, hitPlayer, hitPlayerRef,
                shooterVelocity);
        }

        internal IEnumerator SpawnOwnerTracerAfterViewUpdateInternal(Vector3 fallbackStart, Vector3 end,
            Vector3 hitNormal, bool madeImpact, bool hitPlayer, NetworkObjectReference hitPlayerRef,
            Vector3 shooterVelocity) {
            return _effects.SpawnOwnerTracerLocalAfterViewUpdate(fallbackStart, end, hitNormal, madeImpact,
                hitPlayer, hitPlayerRef, shooterVelocity);
        }

        internal bool TryGetStrictWorldMuzzleTransformInternal(out Transform muzzleTransform, string context,
            bool allowOwnerInstance = false, bool logErrors = true) {
            return _mount.TryGetStrictWorldMuzzleTransform(out muzzleTransform, context, allowOwnerInstance,
                logErrors);
        }

        internal bool TryGetRequiredOwnerMuzzleTransformInternal(out Transform muzzleTransform, string context,
            bool logErrors = true) {
            return _mount.TryGetRequiredOwnerMuzzleTransform(out muzzleTransform, context, logErrors);
        }

        internal bool TryGetOwnerTracerStartPositionInternal(out Vector3 tracerStartPosition) {
            return _mount.TryGetOwnerTracerStartPosition(out tracerStartPosition);
        }

        internal void InitializeTrailPoolInternal() {
            _effects.InitializeTrailPoolFacade();
        }

        internal void CancelReloadInternal() {
            _reload.CancelReload();
        }

        internal void InterruptReloadForShotInternal() {
            _reload.InterruptReloadForShot();
        }

        #endregion

        #region Private Methods - Effects

        private void ClearKinemationMuzzleFx() => _effects.ClearKinemationMuzzleFx();

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
            _effects.PlayNetworkedMuzzleFlash(endPoint);
        }

        public void SpawnTracerLocal(Vector3 start, Vector3 end, Vector3 hitNormal, bool madeImpact, bool hitPlayer,
            NetworkObjectReference hitPlayerRef = default, Vector3 shooterVelocity = default) {
            _effects.SpawnTracerLocal(start, end, hitNormal, madeImpact, hitPlayer, hitPlayerRef,
                shooterVelocity);
        }

        [Rpc(SendTo.Everyone)]
        internal void PlayReloadAnimationServerRpc() {
            _playerAnimator.SetTrigger(ReloadHash);
        }

        #endregion
    }
}
