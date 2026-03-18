using System.Collections.Generic;
using System.Linq;
using Events;
using Game.Weapon.Core;
using KINEMATION.FPSAnimationPack.Scripts.Player;
using UnityEngine;

namespace Game.Weapon.Kinemation {
    [DisallowMultipleComponent]
    public sealed class KinFpWeaponDriver : MonoBehaviour, IKinDriverResolverContext {
        #region Serialized config

        [Header("KINEMATION")]
        [SerializeField] private GameObject fpsPlayerPrefab;

        [SerializeField] private GameObject weaponPrefab;
        [SerializeField] private bool disableKinemationWeaponSounds;
        [SerializeField] private bool disableKinemationPlayerSounds = true;
        [SerializeField] private bool routeWeaponSoundEventsToAudioService = true;
        [SerializeField] private bool disableKinemationInternalMuzzleFx = true;
        [SerializeField] private bool syncLookPitchWithPlayer;
        [SerializeField] private bool syncAirborneState;
        [SerializeField] private bool freezeLocomotionInAir = true;
        [SerializeField] private bool forceWalkAnimationWhileSprinting = true;
        [SerializeField, Range(0f, 1.99f)] private float sprintWalkGaitValue = 1.2f;
        [SerializeField, Range(0f, 1f)] private float equipUnlockNormalizedTime = 0.82f;

        [Header("Grapple")]
        [SerializeField] private bool enableRuntimeGrappleClavicleOffset;

        #endregion

        #region Runtime state (set by Bootstrap / resolver)

        private FPSPlayer _fpsPlayer;
        private Animator _fpsAnimator;
        private int _renderLayer = -1;
        private IKinWeaponRuntimeContext _weaponRuntimeContext;

        #endregion

        #region Subsystems

        private KinActiveWeaponResolver _resolver;
        private KinDriverAudio _audio;
        private KinDriverSoundEvents _soundEvents;
        private KinReloadEquipTracker _tracker;
        private KinDrakeKarVisuals _drakeKar;
        private KinDriverWristBones _wristBones;
        private KinLocomotionSync _locomotionSync;
        private KinGrappleClavicle _grappleClavicle;
        private KinDriverBootstrap _bootstrap;
        private KinEquipReloadPlayback _playback;

        #endregion

        #region IKINDriverResolverContext

        GameObject IKinDriverResolverContext.PlayerInstance => PlayerInstance;
        Transform IKinDriverResolverContext.DriverTransform => transform;
        FPSPlayer IKinDriverResolverContext.FpsPlayer => _fpsPlayer;
        Animator IKinDriverResolverContext.FpsAnimator => _fpsAnimator;
        int IKinDriverResolverContext.RenderLayer => _renderLayer;

        IKinWeaponRuntimeContext IKinDriverResolverContext.WeaponRuntimeContext =>
            _weaponRuntimeContext = ResolveWeaponRuntimeContext();

        bool IKinDriverResolverContext.WeaponSoundPlaybackDisabled =>
            disableKinemationWeaponSounds || routeWeaponSoundEventsToAudioService;

        bool IKinDriverResolverContext.DisableKinemationPlayerSounds => disableKinemationPlayerSounds;
        bool IKinDriverResolverContext.RouteWeaponSoundEventsToAudioService => routeWeaponSoundEventsToAudioService;
        bool IKinDriverResolverContext.DisableKinemationInternalMuzzleFx => disableKinemationInternalMuzzleFx;
        KinFpWeaponDriver IKinDriverResolverContext.DriverForRelays => this;

        bool IKinDriverResolverContext.TryGetWeaponCameraTransform(out Transform cameraTransform) {
            cameraTransform = null;
            var cam = GetComponentInParent<Camera>();
            if(cam == null) return false;
            cameraTransform = cam.transform;
            return true;
        }

        #endregion

        #region Public API (facade)

        public GameObject PlayerInstance { get; private set; }

        public void Configure(GameObject playerPrefab, GameObject fpWeaponPrefab, bool disableWeaponSounds,
            bool disablePlayerSounds, bool routeWeaponSoundEvents, bool syncLookPitch, bool syncInAirState,
            bool freezeAirLocomotion, bool forceWalkWhileSprinting, float sprintGaitValue,
            float equipUnlockNormalizedProgress) {
            fpsPlayerPrefab = playerPrefab;
            weaponPrefab = fpWeaponPrefab;
            disableKinemationWeaponSounds = disableWeaponSounds;
            disableKinemationPlayerSounds = disablePlayerSounds;
            routeWeaponSoundEventsToAudioService = routeWeaponSoundEvents;
            syncLookPitchWithPlayer = syncLookPitch;
            syncAirborneState = syncInAirState;
            freezeLocomotionInAir = freezeAirLocomotion;
            forceWalkAnimationWhileSprinting = forceWalkWhileSprinting;
            sprintWalkGaitValue = Mathf.Clamp(sprintGaitValue, 0f, 1.99f);
            equipUnlockNormalizedTime = Mathf.Clamp01(equipUnlockNormalizedProgress);
        }

        public void InitializeIfNeeded(int renderLayer) {
            _renderLayer = renderLayer;
            _weaponRuntimeContext = ResolveWeaponRuntimeContext();
            EnsureSubsystems();
            if(PlayerInstance != null) {
                KinViewmodelUtility.SetLayerRecursive(PlayerInstance, _renderLayer);
                TryCacheActiveWeapon();
                return;
            }

            var weaponSoundPlaybackDisabled = disableKinemationWeaponSounds || routeWeaponSoundEventsToAudioService;
            _bootstrap.InitializeIfNeeded(renderLayer, fpsPlayerPrefab, weaponPrefab,
                weaponSoundPlaybackDisabled, disableKinemationPlayerSounds, SetPlayerInstance);
            TryCacheActiveWeapon();
        }

        public Transform GetMuzzleTransform() {
            TryCacheActiveWeapon();
            return _resolver?.MuzzleTransform;
        }

        public Transform GetGrappleOriginFpTransform() => _wristBones?.GetGrappleOriginFpTransform();

        public void PlayEquipAnimation(bool immediate) => _playback?.PlayEquipAnimation(immediate);

        public void PlayFireAnimation(int authoritativeAmmoBeforeShot = -1) =>
            _playback?.PlayFireAnimation(authoritativeAmmoBeforeShot, () => _playback.IsAnyReloadClipActive());

        public void PlayReloadAnimation() => _playback?.PlayReloadAnimation();

        public static void PlayReloadCompleteAnimation() {
        }

        public void SyncLocomotion(Vector2 moveInput, bool sprinting, bool tacticalSprinting, bool isGrounded,
            float lookPitchDegrees) =>
            _locomotionSync?.SyncLocomotion(moveInput, sprinting, tacticalSprinting, isGrounded, lookPitchDegrees);

        public void SyncActiveAmmo(int authoritativeAmmo) => _playback?.SyncActiveAmmo(authoritativeAmmo);

        public void AbortReloadAndSyncAmmo(int authoritativeAmmo) {
            _playback?.AbortReloadAndSyncAmmo(authoritativeAmmo);
            _soundEvents?.ClearPendingWeaponSoundEvents();
        }

        public bool IsReloadSequenceInProgress() => _tracker != null &&
                                                    _tracker.IsReloadSequenceInProgress(_playback != null &&
                                                        _playback.IsAnyReloadClipActive());

        public bool IsEquipSequenceInProgress() {
            if(_tracker == null || _playback == null) return false;
            var equipActiveNow = _playback.TryGetEquipStateProgress(out var progress);
            return _tracker.IsEquipSequenceInProgress(equipActiveNow, progress, equipUnlockNormalizedTime);
        }

        public int ConsumeReloadSingleEventCount() => _tracker?.ConsumeReloadSingleEventCount() ?? 0;
        public bool ConsumeReloadCompleteEvent() => _tracker?.ConsumeReloadCompleteEvent() ?? false;
        public void ResetReloadTracking() => _tracker?.ResetReloadTracking();

        public void NotifyReloadSingleEvent() {
            _tracker?.NotifyReloadSingleEvent(Time.frameCount, _tracker.IsTrackingReload);
            if(_resolver != null && _resolver.GetActiveWeaponHandling() ==
               WeaponData.KinemationSpecialHandling.KarLoopBullet)
                _drakeKar?.HideKarLoopForReload();
        }

        public void NotifyAmmoEjectEvent() {
            _tracker?.NotifyAmmoEjectForDrake();
            _drakeKar?.OnAmmoEjectEvent();
        }

        public void NotifyShellShowEvent() {
            _tracker?.NotifyShellShowClearDrake();
            _drakeKar?.OnShellShowEvent();
        }

        public void NotifyReloadCompleteEvent() {
            _tracker?.NotifyReloadCompleteEvent();
            _tracker?.NotifyReloadCompleteClearDrake();
            _drakeKar?.OnReloadCompleteEvent();
        }

        public void NotifyWeaponEventSoundEvent(int clipIndex) => _soundEvents?.NotifyWeaponEventSoundEvent(clipIndex,
            () => _soundEvents.IsKinemationSoundRoutingEnabled(TryCacheActiveWeapon));

        public void NotifyEquipCompleteEvent() => _tracker?.NotifyEquipCompleteEvent(() => {
            _weaponRuntimeContext?.HandleKinemationEquipCompleted();
        });

        public void ArmTopShellEjectSuppressionOnNextReload() => _tracker?.MarkReloadCanceledByShot();
        public void NotifyReloadCanceledByShot() => _tracker?.MarkReloadCanceledByShot();

        public bool IsKinemationSoundRoutingEnabled() => _soundEvents != null &&
                                                         _soundEvents.IsKinemationSoundRoutingEnabled(
                                                             TryCacheActiveWeapon);

        public int GetKinemationSoundClipCount() =>
            _soundEvents?.GetKinemationSoundClipCount(TryCacheActiveWeapon) ?? 0;

        public bool IsLikelyReloadEventSoundClip(int clipIndex) => _soundEvents != null &&
                                                                   _soundEvents.IsLikelyReloadEventSoundClip(clipIndex,
                                                                       TryCacheActiveWeapon);

        public string GetKinemationFireSoundId() => TryCacheActiveWeapon() ? _audio?.ActiveWeaponFireSoundId ?? "" : "";
        public bool HasKinemationFireSound() => !string.IsNullOrWhiteSpace(GetKinemationFireSoundId());

        public bool HasAnyKinemationEventSound() {
            if(!TryCacheActiveWeapon() || _resolver?.ActiveWeapon == null ||
               _resolver.ActiveWeapon.weaponSettings == null ||
               _resolver.ActiveWeapon.weaponSettings.weaponEventSounds == null) return false;
            foreach(var c in _resolver.ActiveWeapon.weaponSettings.weaponEventSounds)
                if(c != null)
                    return true;
            return false;
        }

        public void ClearPendingWeaponSoundEvents() => _soundEvents?.ClearPendingWeaponSoundEvents();

        public void ConsumeWeaponEventSoundIndices(List<int> destination) =>
            _soundEvents?.ConsumeWeaponEventSoundIndices(destination);

        public bool TryGetKinemationSoundId(int clipIndex, out string soundId) {
            soundId = "";
            return _soundEvents != null && _soundEvents.TryGetKinemationSoundId(clipIndex, out soundId);
        }

        public bool AreKinemationSoundsEnabled() {
            if(disableKinemationWeaponSounds || routeWeaponSoundEventsToAudioService) return false;
            if(!TryCacheActiveWeapon() || _resolver?.ActiveWeapon == null) return false;
            var sounds = _resolver.GetActiveWeaponSounds();
            if(sounds == null) return false;
            foreach(var ws in sounds)
                if(ws != null && ws.enabled)
                    return true;
            return false;
        }

        public bool HasActiveWeapon() => TryCacheActiveWeapon();

        #endregion

        #region Internal setters (Bootstrap)

        private void SetPlayerInstance(GameObject instance, FPSPlayer fpsPlayer, Animator fpsAnimator) {
            PlayerInstance = instance;
            _fpsPlayer = fpsPlayer;
            _fpsAnimator = fpsAnimator;
        }

        private IKinWeaponRuntimeContext ResolveWeaponRuntimeContext() {
            if(_weaponRuntimeContext != null) return _weaponRuntimeContext;
            _weaponRuntimeContext = GetComponentsInParent<MonoBehaviour>(true)
                .OfType<IKinWeaponRuntimeContext>()
                .FirstOrDefault();
            return _weaponRuntimeContext;
        }

        #endregion

        #region Unity lifecycle

        private void Awake() => EnsureSubsystems();

        private void OnEnable() {
            EventBus.Subscribe<GrappleStartedEvent>(OnGrappleStarted);
            EventBus.Subscribe<GrappleAnimFirstFrameEvent>(OnGrappleAnimFirstFrame);
            EventBus.Subscribe<GrappleAnimHideEvent>(OnGrappleAnimHide);
            EventBus.Subscribe<GrappleEndedEvent>(OnGrappleEnded);
        }

        private void OnDisable() {
            EventBus.Unsubscribe<GrappleStartedEvent>(OnGrappleStarted);
            EventBus.Unsubscribe<GrappleAnimFirstFrameEvent>(OnGrappleAnimFirstFrame);
            EventBus.Unsubscribe<GrappleAnimHideEvent>(OnGrappleAnimHide);
            EventBus.Unsubscribe<GrappleEndedEvent>(OnGrappleEnded);
            _grappleClavicle?.Clear();
        }

        private void OnGrappleStarted(GrappleStartedEvent evt) => _grappleClavicle?.OnGrappleStarted(evt);

        private void OnGrappleAnimFirstFrame(GrappleAnimFirstFrameEvent evt) =>
            _grappleClavicle?.OnGrappleAnimFirstFrame(evt);

        private void OnGrappleAnimHide(GrappleAnimHideEvent evt) => _grappleClavicle?.OnGrappleAnimHide(evt);
        private void OnGrappleEnded(GrappleEndedEvent evt) => _grappleClavicle?.OnGrappleEnded(evt);

        private void Update() {
            if(PlayerInstance == null) return;
            if(_resolver?.ActiveWeapon == null) TryCacheActiveWeapon();
        }

        private void LateUpdate() {
            _grappleClavicle?.ApplyRuntimeGrappleClavicleOffset();
            _wristBones?.ApplyFixedWristOffsets();
            _drakeKar?.ApplySuppressedTopShellPose();
            _drakeKar?.ApplySuppressedBottomShellPose();
            _drakeKar?.ApplyHiddenKarLoopPose();
        }

        private void OnDestroy() {
            _drakeKar?.RestoreTopShellImmediate();
            _drakeKar?.RestoreBottomShellImmediate();
            _drakeKar?.RestoreKarLoopImmediate();
            _bootstrap?.CleanupRuntimeSettings();
        }

        #endregion

        #region Helpers

        private void EnsureSubsystems() {
            if(_resolver != null) return;
            _resolver = new KinActiveWeaponResolver(this);
            _audio = new KinDriverAudio(this, _resolver);
            _soundEvents = new KinDriverSoundEvents(this, _resolver, _audio);
            _tracker = new KinReloadEquipTracker();
            _drakeKar = new KinDrakeKarVisuals(_resolver);
            _wristBones = new KinDriverWristBones(this);
            _locomotionSync = new KinLocomotionSync(this, freezeLocomotionInAir, forceWalkAnimationWhileSprinting,
                sprintWalkGaitValue, syncLookPitchWithPlayer, syncAirborneState);
            _grappleClavicle = new KinGrappleClavicle(this, _resolver, _wristBones, enableRuntimeGrappleClavicleOffset);
            _bootstrap = new KinDriverBootstrap(this, _audio);
            _playback = new KinEquipReloadPlayback(this, _resolver, _tracker, _drakeKar, _audio, _grappleClavicle,
                TryCacheActiveWeapon);
        }

        private bool TryCacheActiveWeapon() {
            EnsureSubsystems();
            return _resolver.TryCacheActiveWeapon(
                _audio.ApplyActiveWeaponSoundToggles,
                w => _audio.RefreshWeaponSoundMetadata(w, () => _grappleClavicle.ApplyGrappleWeaponIndex()),
                w => _resolver.SuppressInternalMuzzleFx(w, disableKinemationInternalMuzzleFx));
        }

        #endregion
    }
}