using System.Collections;
using Game.Player;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.VFX;

namespace Game.Weapons {
    public partial class Weapon {
        #region Private Methods - Reloading

        private bool CanReload() {
            if(!CurrentWeaponData || _weaponManager.IsPullingOut) return false;
            if(_kinemationFpWeaponDriver == null) return false;
            return currentAmmo < GetCurrentMagCapacity() && !IsReloading;
        }

        private void CompleteReload() {
            if(!CurrentWeaponData) return;
            currentAmmo = GetCurrentMagCapacity();
            IsReloading = false;
            _autoReloadArmed = false;
            _reloadExpectedCompleteTime = float.PositiveInfinity;
            _kinemationReloadFallbackDeadline = float.PositiveInfinity;
            if(_kinemationFpWeaponDriver != null) _kinemationFpWeaponDriver.ResetReloadTracking();

            // Trigger reload complete animation (mag-style reloads)
            ExitReloadAnimation();

            PublishOwnerAmmoToHud();

            SyncServerAmmo(WeaponManager.AmmoSyncReason.Reload);
        }

        private void HandleKinemationReloadSingleRound() {
            if(!IsReloading || CurrentWeaponData == null) return;
            if(CurrentWeaponData.useMagReload) return;
            var magCapacity = GetCurrentMagCapacity();
            if(currentAmmo >= magCapacity) return;

            currentAmmo = Mathf.Min(currentAmmo + 1, magCapacity);

            PublishOwnerAmmoToHud(magCapacity);

            SyncServerAmmo(WeaponManager.AmmoSyncReason.Reload);
        }

        private void CompleteKinemationPartialReloadWithoutFilling() {
            IsReloading = false;
            _autoReloadArmed = false;
            _reloadExpectedCompleteTime = float.PositiveInfinity;
            _kinemationReloadFallbackDeadline = float.PositiveInfinity;
            if(_kinemationFpWeaponDriver != null) _kinemationFpWeaponDriver.ResetReloadTracking();

            ExitReloadAnimation();
            SyncServerAmmo(WeaponManager.AmmoSyncReason.Reload);

            if(CurrentWeaponData != null) {
                PublishOwnerAmmoToHud();
            }
        }

        #endregion

        #region Private Methods - Effects

        private void PlayFireAnimationForCurrentWeapon(int authoritativeAmmoBeforeShot) {
            if(_kinemationFpWeaponDriver == null) return;
            _kinemationFpWeaponDriver.PlayFireAnimation(authoritativeAmmoBeforeShot);
        }

        private void PlayReloadAnimationForCurrentWeapon() {
            if(_kinemationFpWeaponDriver != null) _kinemationFpWeaponDriver.PlayReloadAnimation();
        }

        private bool UseKinemationInternalSounds() {
            return _kinemationFpWeaponDriver != null && _kinemationFpWeaponDriver.AreKinemationSoundsEnabled();
        }

        private bool UseKinemationEventSoundRouting() {
            return _kinemationFpWeaponDriver != null &&
                   _kinemationFpWeaponDriver.IsKinemationSoundEventRoutingEnabled();
        }

        private bool ShouldSuppressLegacyReloadSound() {
            return UseKinemationEventSoundRouting() &&
                   _kinemationFpWeaponDriver != null &&
                   _kinemationFpWeaponDriver.HasAnyKinemationEventSound();
        }

        private Quaternion ResolveKinemationMuzzleFxRotation(Transform muzzleTransform, Vector3 preferredDirection) {
            var direction = preferredDirection;
            if(direction.sqrMagnitude <= 0.0001f && muzzleTransform != null) {
                direction = muzzleTransform.forward;
            }

            if(direction.sqrMagnitude <= 0.0001f) {
                direction = transform.forward;
            }

            direction.Normalize();

            var up = Vector3.up;
            var cameraTransform = playerController != null ? playerController.FpCameraTransform : null;
            if(cameraTransform != null) {
                up = cameraTransform.up;
            } else if(muzzleTransform != null) {
                up = muzzleTransform.up;
            }

            if(Mathf.Abs(Vector3.Dot(up, direction)) > 0.98f) {
                up = Vector3.right;
            }

            return Quaternion.LookRotation(direction, up);
        }

        private GameObject EnsureKinemationLocalMuzzleFxInstance(Transform muzzleTransform, Quaternion spawnRotation) {
            if(CurrentWeaponData == null || CurrentWeaponData.muzzleFlashPrefab == null || muzzleTransform == null) {
                return null;
            }

            var sourcePrefab = CurrentWeaponData.muzzleFlashPrefab;
            var needsRecreate = _kinemationLocalMuzzleFxInstance == null ||
                                _kinemationLocalMuzzleSourcePrefab != sourcePrefab;
            if(needsRecreate) {
                if(_kinemationLocalMuzzleFxInstance != null) {
                    QuiesceMuzzleFxInstance(_kinemationLocalMuzzleFxInstance, _kinemationLocalMuzzleVfx);
                    Destroy(_kinemationLocalMuzzleFxInstance);
                }

                _kinemationLocalMuzzleFxInstance = Instantiate(sourcePrefab, muzzleTransform.position, spawnRotation);
                _kinemationLocalMuzzleSourcePrefab = sourcePrefab;
                _kinemationLocalMuzzleVfx = _kinemationLocalMuzzleFxInstance.GetComponent<VisualEffect>();
                if(_kinemationLocalMuzzleVfx != null) {
                    _kinemationLocalMuzzleVfx.Stop();
                    _kinemationLocalMuzzleVfx.Reinit();
                }
            } else {
                _kinemationLocalMuzzleFxInstance.transform.SetPositionAndRotation(muzzleTransform.position,
                    spawnRotation);
            }

            AttachMuzzleFollow(_kinemationLocalMuzzleFxInstance, muzzleTransform, followRotation: false);
            ApplyLayerRecursive(_kinemationLocalMuzzleFxInstance, muzzleTransform.gameObject.layer);
            return _kinemationLocalMuzzleFxInstance;
        }

        private void TriggerKinemationLocalMuzzleFx() {
            if(_kinemationLocalMuzzleFxInstance == null) return;
            ReactivateMuzzleFxInstance(_kinemationLocalMuzzleFxInstance, _kinemationLocalMuzzleVfx);

            if(_kinemationLocalMuzzleVfx != null) {
                _kinemationLocalMuzzleVfx.Stop();
                _kinemationLocalMuzzleVfx.Reinit();
                _kinemationLocalMuzzleVfx.Play();
                return;
            }

            var particleSystems = _kinemationLocalMuzzleFxInstance.GetComponentsInChildren<ParticleSystem>(true);
            foreach(var system in particleSystems) {
                if(system == null) continue;
                system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                system.Play(true);
            }
        }

        private void ClearKinemationLocalMuzzleFxInstance() {
            if(_kinemationLocalMuzzleFxInstance != null) {
                QuiesceMuzzleFxInstance(_kinemationLocalMuzzleFxInstance, _kinemationLocalMuzzleVfx);
                Destroy(_kinemationLocalMuzzleFxInstance);
            }

            _kinemationLocalMuzzleFxInstance = null;
            _kinemationLocalMuzzleVfx = null;
            _kinemationLocalMuzzleSourcePrefab = null;
        }

        private static void QuiesceMuzzleFxInstance(GameObject fxInstance, VisualEffect cachedVisualEffect) {
            if(fxInstance == null) return;

            if(cachedVisualEffect != null) {
                cachedVisualEffect.Stop();
                cachedVisualEffect.Reinit();
            }

            var vfxComponents = fxInstance.GetComponentsInChildren<VisualEffect>(true);
            foreach(var vfx in vfxComponents) {
                if(vfx == null) continue;
                vfx.Stop();
                vfx.Reinit();
            }

            var particleSystems = fxInstance.GetComponentsInChildren<ParticleSystem>(true);
            foreach(var particleSystem in particleSystems) {
                if(particleSystem == null) continue;
                particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }

        private static void ReactivateMuzzleFxInstance(GameObject fxInstance, VisualEffect cachedVisualEffect) {
            if(fxInstance == null) return;
            _ = cachedVisualEffect;
            if(!fxInstance.activeSelf) {
                fxInstance.SetActive(true);
            }
        }

        private void PrewarmKinemationLocalMuzzleFxInstance() {
            if(_hasPrewarmedKinemationMuzzleForCurrentWeapon) return;
            if(_kinemationFpWeaponDriver == null) return;
            if(CurrentWeaponData == null || CurrentWeaponData.muzzleFlashPrefab == null) return;

            if(!TryGetRequiredOwnerMuzzleTransform(out var muzzleTransform, "PrewarmKinemationLocalMuzzleFxInstance",
                   logErrors: false)) {
                return;
            }

            var preferredDirection = Vector3.zero;
            var fpCameraTransform = playerController != null ? playerController.FpCameraTransform : null;
            if(fpCameraTransform != null) {
                preferredDirection = fpCameraTransform.forward;
            }

            var desiredWorldRotation = ResolveKinemationMuzzleFxRotation(muzzleTransform, preferredDirection);
            var fxGo = EnsureKinemationLocalMuzzleFxInstance(muzzleTransform, desiredWorldRotation);
            if(fxGo == null) return;

            QuiesceMuzzleFxInstance(fxGo, _kinemationLocalMuzzleVfx);
            _hasPrewarmedKinemationMuzzleForCurrentWeapon = true;
        }

        /// <summary>
        /// Play muzzle flash locally (owner only, FP)
        /// Muzzle flash tracks the weapon muzzle each frame to avoid drift while moving fast.
        /// </summary>
        private void PlayLocalMuzzleFlash(int authoritativeAmmoBeforeShot) {
            PlayFireAnimationForCurrentWeapon(authoritativeAmmoBeforeShot);

            PlayShootAnimationServerRpc();

            if(CurrentWeaponData != null && CurrentWeaponData.muzzleFlashPrefab != null) {
                if(TryGetRequiredOwnerMuzzleTransform(out var muzzleTransform, "PlayLocalMuzzleFlash")) {
                    if(_kinemationFpWeaponDriver != null) {
                        var preferredDirection = Vector3.zero;
                        var fpCameraTransform = playerController != null ? playerController.FpCameraTransform : null;
                        if(fpCameraTransform != null) {
                            preferredDirection = fpCameraTransform.forward;
                        }

                        var desiredWorldRotation =
                            ResolveKinemationMuzzleFxRotation(muzzleTransform, preferredDirection);
                        var fxGo = EnsureKinemationLocalMuzzleFxInstance(muzzleTransform, desiredWorldRotation);
                        if(fxGo != null) {
                            _localMuzzleFlashSpawnPositionForShot = fxGo.transform.position;
                            _hasLocalMuzzleFlashSpawnPositionForShot = true;
                            TriggerKinemationLocalMuzzleFx();
                        }
                    }
                }
            }

            if(!_fpMuzzleLight) return;
            _fpMuzzleLight.SetActive(true);
            _fpLightOffTime = Time.time + MuzzleLightTime;
        }

        private void ConsumePendingKinemationReloadSingleEvents() {
            if(!IsReloading || _kinemationFpWeaponDriver == null) return;
            if(CurrentWeaponData == null || CurrentWeaponData.useMagReload) return;

            var reloadSingleEvents = _kinemationFpWeaponDriver.ConsumeReloadSingleEventCount();
            for(var i = 0; i < reloadSingleEvents; i++) {
                HandleKinemationReloadSingleRound();
            }
        }
        [Rpc(SendTo.Everyone)]
        private void PlayShootAnimationServerRpc() {
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
            if(playerController != null && playerController.IsOwner) {
                return;
            }

            if(!TryGetStrictWorldMuzzleTransform(out var muzzleTransform, "PlayNetworkedMuzzleFlash")) {
                return;
            }

            // NON-OWNER ONLY: Play 3P world muzzle flash
            if(CurrentWeaponData != null &&
               CurrentWeaponData.muzzleFlashPrefab != null &&
               muzzleTransform != null) {
                var position = muzzleTransform.position;
                var tracerDirection = endPoint - position;
                var tracerDirectionValid = tracerDirection.sqrMagnitude > 0.0001f;
                var tracerDirectionNormalized = tracerDirectionValid ? tracerDirection.normalized : Vector3.zero;
                var desiredWorldRotation =
                    ResolveWorldMuzzleFxRotation(muzzleTransform, tracerDirectionNormalized, tracerDirectionValid);

                var fxGo = Instantiate(CurrentWeaponData.muzzleFlashPrefab, position,
                    desiredWorldRotation);
                AttachMuzzleFollow(fxGo, muzzleTransform, followRotation: false);
                ApplyLayerRecursive(fxGo, muzzleTransform.gameObject.layer);

                var fx = fxGo.GetComponent<VisualEffect>();
                if(fx != null) {
                    fx.Play();
                }

                Destroy(fxGo, 1f);
            } else {
                Debug.LogError(
                    "[Weapon][RemoteMuzzleStrict][PlayNetworkedMuzzleFlash] Missing muzzle flash prefab. " +
                    $"weapon={(CurrentWeaponData != null ? CurrentWeaponData.weaponName : "(none)")} " +
                    $"worldWeapon={(_currentWorldWeaponInstance != null ? _currentWorldWeaponInstance.name : "(none)")}",
                    this);
                return;
            }

            if(!_worldMuzzleLight) return;
            _worldMuzzleLight.SetActive(true);
            _worldLightOffTime = Time.time + MuzzleLightTime;
        }

        private Quaternion ResolveWorldMuzzleFxRotation(Transform muzzleTransform, Vector3 tracerDirectionNormalized,
            bool hasTracerDirection) {
            var direction = hasTracerDirection
                ? tracerDirectionNormalized
                : muzzleTransform != null
                    ? muzzleTransform.forward
                    : transform.forward;
            if(direction.sqrMagnitude <= 0.0001f) {
                direction = muzzleTransform != null ? muzzleTransform.forward : transform.forward;
            }

            direction.Normalize();

            var up = muzzleTransform != null ? muzzleTransform.up : Vector3.up;
            if(Mathf.Abs(Vector3.Dot(up, direction)) > 0.98f) {
                up = Vector3.right;
            }

            return Quaternion.LookRotation(direction, up);
        }

        private static void ApplyLayerRecursive(GameObject root, int layer) {
            if(root == null) return;
            root.layer = layer;
            foreach(Transform child in root.transform) {
                if(child != null) {
                    ApplyLayerRecursive(child.gameObject, layer);
                }
            }
        }

        private static void AttachMuzzleFollow(GameObject fxGo, Transform muzzleTransform, bool followRotation) {
            if(fxGo == null || muzzleTransform == null) return;

            var follower = fxGo.GetComponent<MuzzleFlashFollow>();
            if(follower == null) {
                follower = fxGo.AddComponent<MuzzleFlashFollow>();
            }

            follower.Bind(muzzleTransform, followRotation);
        }

        [DefaultExecutionOrder(7100)] // Must run after upper-body/spine pose updates.
        private sealed class MuzzleFlashFollow : MonoBehaviour {
            private Transform _muzzleTransform;
            private bool _followRotation;
            private int _lastSyncFrame = -1;

            public void Bind(Transform muzzleTransform, bool followRotation) {
                _muzzleTransform = muzzleTransform;
                _followRotation = followRotation;
                SyncToMuzzle(force: true);
            }

            private void Update() {
                SyncToMuzzle();
            }

            private void LateUpdate() {
                SyncToMuzzle(force: true);
            }

            private void SyncToMuzzle(bool force = false) {
                if(_muzzleTransform == null) return;
                if(!force && _lastSyncFrame == Time.frameCount) return;

                transform.position = _muzzleTransform.position;
                if(_followRotation) {
                    transform.rotation = _muzzleTransform.rotation;
                }

                _lastSyncFrame = Time.frameCount;
            }
        }

        public void SpawnTracerLocal(Vector3 start, Vector3 end, Vector3 hitNormal, bool madeImpact, bool hitPlayer,
            NetworkObjectReference hitPlayerRef = default, Vector3 shooterVelocity = default) {
            if(!CurrentWeaponData || !CurrentWeaponData.bulletTrail) return;

            // Get trail from pool
            var trail = GetTrailFromPool();
            if(trail == null) return;

            // Set up trail
            trail.transform.position = start;
            trail.transform.rotation = Quaternion.LookRotation(end - start);
            trail.gameObject.SetActive(true);
            trail.enabled = true;
            trail.emitting = true;
            trail.Clear(); // Clear any previous trail data

            // Disable AudioSource on trail if it exists (we'll play sound manually only on misses)
            var trailAudioSource = trail.GetComponent<AudioSource>();
            if(trailAudioSource != null) {
                trailAudioSource.enabled = false;
            }

            // Play trail sound immediately on spawn when bullet misses (no impact)
            // When hitting world or players, impact sounds are already played
            if(!madeImpact && playerController != null && playerController.IsOwner && _audioRelay != null) {
                _audioRelay.RequestPlay("weapons.bullet.trail", start, allowOverlap: true);
            }

            StartCoroutine(SpawnTrail(trail, end, hitNormal, madeImpact, hitPlayer, hitPlayerRef, shooterVelocity));
        }

        private void PlayFireSound() {
            if(UseKinemationEventSoundRouting() && _kinemationFpWeaponDriver != null &&
               _kinemationFpWeaponDriver.HasKinemationFireSound()) {
                if(playerController == null || !playerController.IsOwner) return;
                if(_audioRelay == null || !playerController.NetworkObject) return;

                var kinemationFireSoundId = _kinemationFpWeaponDriver.GetKinemationFireSoundId();
                if(!string.IsNullOrWhiteSpace(kinemationFireSoundId)) {
                    _audioRelay.RequestPlayAttached(kinemationFireSoundId,
                        new NetworkObjectReference(playerController.NetworkObject),
                        allowOverlap: true);
                }

                return;
            }

            if(UseKinemationInternalSounds()) return;
            if(!playerController.IsOwner) return;
            if(_audioRelay == null) return;

            var soundId = CurrentWeaponData != null ? CurrentWeaponData.shootSoundId : "";
            if(!string.IsNullOrWhiteSpace(soundId)) {
                _audioRelay.RequestPlayAttached(soundId, new NetworkObjectReference(playerController.NetworkObject),
                    allowOverlap: true);
            }
        }

        private void PlayDryFireSound() {
            if(!playerController.IsOwner) return;
            if(_audioRelay == null) return;
            _audioRelay.RequestPlayAttached("weapons.bullet.dry",
                new NetworkObjectReference(playerController.NetworkObject), allowOverlap: true);
        }

        private void PlayReloadEffects() {
            PlayReloadAnimationForCurrentWeapon();

            PlayReloadAnimationServerRpc();

            if(ShouldSuppressLegacyReloadSound()) return;
            if(UseKinemationInternalSounds()) return;
            if(!playerController.IsOwner) return;
            if(_audioRelay == null) return;
            var soundId = CurrentWeaponData != null ? CurrentWeaponData.reloadSoundId : "";
            if(!string.IsNullOrWhiteSpace(soundId)) {
                _audioRelay.RequestPlayAttached(soundId, new NetworkObjectReference(playerController.NetworkObject),
                    allowOverlap: false);
            }
        }
        [Rpc(SendTo.Everyone)]
        private void PlayReloadAnimationServerRpc() {
            _playerAnimator.SetTrigger(ReloadHash);
        }

        private void ExitReloadAnimation() {
            if(_kinemationFpWeaponDriver != null) KinemationFpWeaponDriver.PlayReloadCompleteAnimation();
        }

        private void RunReloadWatchdog() {
            if(Time.time < _nextReloadRecoveryAllowedTime) return;

            if(!IsReloading) return;
            if(Time.time <= _reloadExpectedCompleteTime) return;

            if(CurrentWeaponData != null && !CurrentWeaponData.useMagReload) {
                CompleteKinemationPartialReloadWithoutFilling();
            } else {
                CompleteReload();
            }

            _nextReloadRecoveryAllowedTime = Time.time + ReloadRecoveryCooldownSeconds;
        }

        private void UpdateKinemationReloadState() {
            if(!IsReloading || _kinemationFpWeaponDriver == null) return;

            var reloadSingleEvents = _kinemationFpWeaponDriver.ConsumeReloadSingleEventCount();
            for(var i = 0; i < reloadSingleEvents; i++) {
                HandleKinemationReloadSingleRound();
            }

            if(_kinemationFpWeaponDriver.ConsumeReloadCompleteEvent()) {
                CompleteReload();
                return;
            }

            if(!_kinemationFpWeaponDriver.IsReloadSequenceInProgress()) {
                if(CurrentWeaponData != null && !CurrentWeaponData.useMagReload) {
                    CompleteKinemationPartialReloadWithoutFilling();
                } else {
                    CompleteReload();
                }

                return;
            }

            if(Time.time <= _kinemationReloadFallbackDeadline) return;
            if(CurrentWeaponData != null && !CurrentWeaponData.useMagReload) {
                CompleteKinemationPartialReloadWithoutFilling();
            } else {
                CompleteReload();
            }

            _nextReloadRecoveryAllowedTime = Time.time + ReloadRecoveryCooldownSeconds;
        }

        private void ProcessKinemationSoundEvents() {
            if(_kinemationFpWeaponDriver == null) return;

            // Always drain queues to avoid stale events if ownership/state changed.
            _kinemationFpWeaponDriver.ConsumeWeaponFireSoundEventCount();
            _kinemationWeaponSoundEventBuffer.Clear();
            _kinemationFpWeaponDriver.ConsumeWeaponEventSoundIndices(_kinemationWeaponSoundEventBuffer);

            if(_kinemationWeaponSoundEventBuffer.Count == 0) return;
            if(!UseKinemationEventSoundRouting()) return;
            if(playerController == null || !playerController.IsOwner) return;
            if(_audioRelay == null || !playerController.NetworkObject) return;

            var attachRef = new NetworkObjectReference(playerController.NetworkObject);
            foreach(var clipIndex in _kinemationWeaponSoundEventBuffer) {
                if(!_kinemationFpWeaponDriver.TryGetKinemationEventSoundId(clipIndex, out var eventSoundId)) continue;
                if(string.IsNullOrWhiteSpace(eventSoundId)) continue;
                _audioRelay.RequestPlayAttached(eventSoundId, attachRef, allowOverlap: true);
            }
        }

        private void StopKinemationEventSoundsForCurrentWeapon() {
            if(_kinemationFpWeaponDriver == null) return;
            if(!UseKinemationEventSoundRouting()) return;
            if(playerController == null || !playerController.IsOwner) return;
            if(_audioRelay == null) return;

            var eventClipCount = _kinemationFpWeaponDriver.GetKinemationEventSoundClipCount();
            for(var clipIndex = 0; clipIndex < eventClipCount; clipIndex++) {
                if(!_kinemationFpWeaponDriver.IsLikelyReloadEventSoundClip(clipIndex)) continue;
                if(!_kinemationFpWeaponDriver.TryGetKinemationEventSoundId(clipIndex, out var eventSoundId)) continue;
                if(string.IsNullOrWhiteSpace(eventSoundId)) continue;
                _audioRelay.RequestStop(eventSoundId);
            }
        }

        private IEnumerator SpawnTrail(TrailRenderer trail, Vector3 hitPoint, Vector3 hitNormal, bool madeImpact,
            bool hitPlayer, NetworkObjectReference hitPlayerRef = default, Vector3 shooterVelocity = default) {
            var position = trail.transform.position;
            var distance = Vector3.Distance(position, hitPoint);
            if(distance <= 0.0001f) {
                trail.transform.position = hitPoint;
                yield return new WaitForSeconds(trail.time);
                ReturnTrailToPool(trail);
                yield break;
            }

            var shotDirection = (hitPoint - position) / distance;
            var inheritedPerpendicularVelocity =
                ComputeTracerInheritedPerpendicularVelocity(shooterVelocity, shotDirection);

            var remainingDistance = distance;
            var elapsed = 0f;

            while(remainingDistance > 0) {
                var t = 1f - remainingDistance / distance;
                var basePosition = Vector3.Lerp(position, hitPoint, t);
                var fade = Mathf.Pow(1f - Mathf.Clamp01(t), TracerPerpendicularVelocityFadeExponent);
                var offset = inheritedPerpendicularVelocity * (elapsed * fade);
                trail.transform.position = basePosition + offset;
                var dt = Time.deltaTime;
                remainingDistance -= BulletSpeed * dt;
                elapsed += dt;
                yield return null;
            }

            trail.transform.position = hitPoint;

            // Check if the local player is the one being hit - if so, don't spawn impact effect
            var isLocalPlayerHit = false;
            if(hitPlayer && hitPlayerRef.TryGet(out var hitNetworkObject) && hitNetworkObject != null) {
                var hitPlayerController = hitNetworkObject.GetComponent<PlayerController>();
                if(hitPlayerController != null && hitPlayerController.IsOwner) {
                    isLocalPlayerHit = true;
                }
            }

            if(madeImpact && CurrentWeaponData && CurrentWeaponData.bulletImpact && !isLocalPlayerHit) {
                var rotation = hitNormal.sqrMagnitude > 0.0001f
                    ? Quaternion.LookRotation(hitNormal)
                    : Quaternion.identity;

                var spawnPos = hitPoint + hitNormal.normalized * 0.005f;

                var impactInstance = Instantiate(CurrentWeaponData.bulletImpact.gameObject, spawnPos, rotation);
                switch(hitPlayer) {
                    case true: {
                        var decal = FindChildByNameRecursive(impactInstance.transform, "Decal");
                        if(decal != null) {
                            decal.gameObject.SetActive(false);
                        }

                        break;
                    }
                    // Don't play bullet impact sound when hitting a player (hitmarker and hurt sounds handle this)
                    case false when playerController.IsOwner && _audioRelay != null:
                        _audioRelay.RequestPlay("weapons.bullet.impact", hitPoint, allowOverlap: true);
                        break;
                }
            }

            // Wait for trail to fade out, then return to pool
            yield return new WaitForSeconds(trail.time);

            ReturnTrailToPool(trail);
        }

        private static Transform FindChildByNameRecursive(Transform root, string childName) {
            if(root == null || string.IsNullOrEmpty(childName)) return null;

            var childCount = root.childCount;
            for(var i = 0; i < childCount; i++) {
                var child = root.GetChild(i);
                if(child == null) continue;
                if(child.name == childName) {
                    return child;
                }

                var nested = FindChildByNameRecursive(child, childName);
                if(nested != null) {
                    return nested;
                }
            }

            return null;
        }

        private IEnumerator SpawnOwnerTracerLocalAfterViewUpdate(Vector3 fallbackStart, Vector3 end, Vector3 hitNormal,
            bool madeImpact, bool hitPlayer, NetworkObjectReference hitPlayerRef, Vector3 shooterVelocity) {
            // Wait until end-of-frame so camera/viewmodel transforms settle before we sample muzzle position.
            // This keeps local tracer origin aligned with the rendered FP muzzle during fast look updates.
            yield return new WaitForEndOfFrame();

            var start = fallbackStart;
            if(playerController != null && playerController.IsOwner) {
                if(!TryGetOwnerTracerStartPosition(out start)) {
                    start = fallbackStart;
                }
            }

            SpawnTracerLocal(start, end, hitNormal, madeImpact, hitPlayer, hitPlayerRef, shooterVelocity);
        }

        private static Vector3 ComputeTracerInheritedPerpendicularVelocity(Vector3 shooterVelocity,
            Vector3 shotDirection) {
            if(shooterVelocity.sqrMagnitude <= 0.0001f || shotDirection.sqrMagnitude <= 0.0001f) {
                return Vector3.zero;
            }

            var direction = shotDirection.normalized;
            var parallel = Vector3.Project(shooterVelocity, direction);
            var perpendicular = shooterVelocity - parallel;
            if(perpendicular.sqrMagnitude <= 0.0001f) {
                return Vector3.zero;
            }

            var inherited = perpendicular * TracerPerpendicularVelocityInheritanceScale;
            if(inherited.sqrMagnitude >
               TracerPerpendicularVelocityInheritanceMax * TracerPerpendicularVelocityInheritanceMax) {
                inherited = inherited.normalized * TracerPerpendicularVelocityInheritanceMax;
            }

            return inherited;
        }

        /// <summary>
        /// Initializes the trail pool with pre-allocated TrailRenderer objects.
        /// Only clears inactive trails from the pool - active trails are allowed to finish naturally.
        /// </summary>
        private void InitializeTrailPool() {
            // Clear existing pool (only inactive trails - active trails will finish and be cleaned up naturally)
            while(_trailPool.Count > 0) {
                var oldTrail = _trailPool.Dequeue();
                // Only destroy if it's inactive - active trails are still animating and will finish on their own
                if(oldTrail != null && !oldTrail.gameObject.activeInHierarchy) {
                    Destroy(oldTrail.gameObject);
                }
            }

            // Create new pool
            if(CurrentWeaponData == null || CurrentWeaponData.bulletTrail == null) return;
            for(var i = 0; i < TrailPoolSize; i++) {
                var trailObj = Instantiate(CurrentWeaponData.bulletTrail);
                trailObj.emitting = false;
                trailObj.gameObject.SetActive(false);
                _trailPool.Enqueue(trailObj);
            }
        }

        /// <summary>
        /// Gets an available trail from the pool, or creates a new one if pool is empty.
        /// </summary>
        private TrailRenderer GetTrailFromPool() {
            // Try to find an inactive trail in the pool
            TrailRenderer trail = null;
            var attempts = 0;

            while(attempts < _trailPool.Count && _trailPool.Count > 0) {
                var candidate = _trailPool.Dequeue();
                _trailPool.Enqueue(candidate); // Put it back at the end

                if(candidate != null && !candidate.gameObject.activeInHierarchy) {
                    trail = candidate;
                    break;
                }

                attempts++;
            }

            // If no available trail found, create a new one
            if(trail != null || CurrentWeaponData == null || CurrentWeaponData.bulletTrail == null) return trail;
            trail = Instantiate(CurrentWeaponData.bulletTrail);
            trail.emitting = false;

            return trail;
        }

        /// <summary>
        /// Returns a trail to the pool after it's finished.
        /// Only returns trails that are still valid and match the current weapon.
        /// </summary>
        private void ReturnTrailToPool(TrailRenderer trail) {
            // Check if trail was destroyed (e.g., during weapon switch)
            if(trail == null) return;

            // Check if trail's GameObject still exists
            if(trail.gameObject == null) return;

            // Don't return trails to pool if weapon has changed (let them be destroyed naturally)
            // Active trails from previous weapon will just be cleaned up by Unity
            if(CurrentWeaponData == null || CurrentWeaponData.bulletTrail == null) return;

            trail.emitting = false;
            trail.gameObject.SetActive(false);
            trail.Clear(); // Clear the trail data
            _trailPool.Enqueue(trail);
        }

        #endregion
    }
}
