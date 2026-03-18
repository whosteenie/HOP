using System.Collections;
using Diagnostics;
using Game.Weapon.Kinemation;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.VFX;

namespace Game.Weapon.Core {
    internal sealed class WeaponEffects {
        private readonly Weapon _weapon;

        public WeaponEffects(Weapon weapon) {
            _weapon = weapon;
        }

        public void PlayLocalMuzzleFlash(int authoritativeAmmoBeforeShot) {
            PlayFireAnimation(authoritativeAmmoBeforeShot);
            _weapon.PlayShootAnimationServerRpc();

            if(_weapon.CurrentWeaponData != null && _weapon.CurrentWeaponData.muzzleFlashPrefab != null) {
                if(_weapon.TryGetOwnerMuzzleTransformInternal(out var muzzleTransform, "PlayLocalMuzzleFlash")) {
                    if(_weapon.KinDriver != null) {
                        var preferredDirection = Vector3.zero;
                        var fpCameraTransform = _weapon.OwnerContext != null ? _weapon.OwnerContext.FpCameraTransform : null;
                        if(fpCameraTransform != null) {
                            preferredDirection = fpCameraTransform.forward;
                        }

                        var desiredWorldRotation = ResolveKinemationMuzzleFxRotation(muzzleTransform, preferredDirection);
                        var fxGo = EnsureKinemationMuzzleFx(muzzleTransform, desiredWorldRotation);
                        if(fxGo != null) {
                            _weapon.LocalMuzzleFlashSpawnPositionForShot = fxGo.transform.position;
                            _weapon.HasLocalMuzzleFlashSpawnPositionForShot = true;
                            TriggerKinemationLocalMuzzleFx();
                        }
                    }
                }
            }

            if(_weapon.FpMuzzleLight == null) return;
            _weapon.FpMuzzleLight.SetActive(true);
            _weapon.FpLightOffTime = Time.time + Weapon.MuzzleLightTime;
        }

        public void PlayNetworkedMuzzleFlash(Vector3 endPoint) {
            if(_weapon.OwnerContext is { IsOwner: true }) {
                return;
            }

            if(!_weapon.TryGetStrictWorldMuzzleTransformInternal(out var muzzleTransform, "PlayNetworkedMuzzleFlash")) {
                return;
            }

            if(_weapon.CurrentWeaponData != null &&
               _weapon.CurrentWeaponData.muzzleFlashPrefab != null &&
               muzzleTransform != null) {
                var position = muzzleTransform.position;
                var tracerDirection = endPoint - position;
                var tracerDirectionValid = tracerDirection.sqrMagnitude > 0.0001f;
                var tracerDirectionNormalized = tracerDirectionValid ? tracerDirection.normalized : Vector3.zero;
                var desiredWorldRotation =
                    ResolveWorldMuzzleFxRotation(muzzleTransform, tracerDirectionNormalized, tracerDirectionValid);

                var fxGo = Object.Instantiate(_weapon.CurrentWeaponData.muzzleFlashPrefab, position, desiredWorldRotation);
                AttachMuzzleFollow(fxGo, muzzleTransform, followRotation: false);
                ApplyLayerRecursive(fxGo, muzzleTransform.gameObject.layer);

                var fx = fxGo.GetComponent<VisualEffect>();
                if(fx != null) {
                    fx.Play();
                }

                Object.Destroy(fxGo, 1f);
            } else {
                DevLog.LogError(
                    "[Weapon][RemoteMuzzleStrict][PlayNetworkedMuzzleFlash] Missing muzzle flash prefab. " +
                    $"weapon={(_weapon.CurrentWeaponData != null ? _weapon.CurrentWeaponData.weaponName : "(none)")} " +
                    $"worldWeapon={(_weapon.CurrentWorldWeaponInstance != null ? _weapon.CurrentWorldWeaponInstance.name : "(none)")}",
                    _weapon);
                return;
            }

            if(_weapon.WorldMuzzleLight == null) return;
            _weapon.WorldMuzzleLight.SetActive(true);
            _weapon.WorldLightOffTime = Time.time + Weapon.MuzzleLightTime;
        }

        public void SpawnTracerLocal(Vector3 start, Vector3 end, Vector3 hitNormal, bool madeImpact, bool hitPlayer,
            NetworkObjectReference hitPlayerRef = default, Vector3 shooterVelocity = default) {
            if(!_weapon.CurrentWeaponData || !_weapon.CurrentWeaponData.bulletTrail) return;

            var trail = GetTrailFromPool();
            if(trail == null) return;

            trail.transform.position = start;
            trail.transform.rotation = Quaternion.LookRotation(end - start);
            trail.gameObject.SetActive(true);
            trail.enabled = true;
            trail.emitting = true;
            trail.Clear();

            var trailAudioSource = trail.GetComponent<AudioSource>();
            if(trailAudioSource != null) {
                trailAudioSource.enabled = false;
            }

            if(!madeImpact && _weapon.OwnerContext is { IsOwner: true } && _weapon.AudioRelay != null) {
                _weapon.AudioRelay.RequestPlay("weapons.bullet.trail", start, allowOverlap: true);
            }

            _weapon.StartCoroutine(SpawnTrail(trail, end, hitNormal, madeImpact, hitPlayer, hitPlayerRef, shooterVelocity));
        }

        public IEnumerator SpawnOwnerTracerLocalAfterViewUpdate(Vector3 fallbackStart, Vector3 end, Vector3 hitNormal,
            bool madeImpact, bool hitPlayer, NetworkObjectReference hitPlayerRef, Vector3 shooterVelocity) {
            yield return new WaitForEndOfFrame();

            var start = fallbackStart;
            if(_weapon.OwnerContext is { IsOwner: true }) {
                if(!_weapon.TryGetOwnerTracerStartPositionInternal(out start)) {
                    start = fallbackStart;
                }
            }

            SpawnTracerLocal(start, end, hitNormal, madeImpact, hitPlayer, hitPlayerRef, shooterVelocity);
        }

        public void PlayFireSound() {
            if(UseKinemationEventSoundRouting() && _weapon.KinDriver != null &&
               _weapon.KinDriver.HasKinemationFireSound()) {
                if(_weapon.OwnerContext is not { IsOwner: true }) return;
                if(_weapon.AudioRelay == null || _weapon.OwnerContext.NetworkObject == null) return;

                var kinemationFireSoundId = _weapon.KinDriver.GetKinemationFireSoundId();
                if(!string.IsNullOrWhiteSpace(kinemationFireSoundId)) {
                    _weapon.AudioRelay.RequestPlayAttached(
                        kinemationFireSoundId,
                        new NetworkObjectReference(_weapon.OwnerContext.NetworkObject),
                        allowOverlap: true);
                }

                return;
            }

            if(UseKinemationInternalSounds()) return;
            if(_weapon.OwnerContext is not { IsOwner: true }) return;
            if(_weapon.AudioRelay == null) return;

            var soundId = _weapon.CurrentWeaponData != null ? _weapon.CurrentWeaponData.shootSoundId : "";
            if(!string.IsNullOrWhiteSpace(soundId)) {
                _weapon.AudioRelay.RequestPlayAttached(soundId,
                    new NetworkObjectReference(_weapon.OwnerContext.NetworkObject), allowOverlap: true);
            }
        }

        public void PlayDryFireSound() {
            if(_weapon.OwnerContext is not { IsOwner: true }) return;
            if(_weapon.AudioRelay == null) return;
            _weapon.AudioRelay.RequestPlayAttached("weapons.bullet.dry",
                new NetworkObjectReference(_weapon.OwnerContext.NetworkObject), allowOverlap: true);
        }

        public void PlayReloadEffects() {
            PlayReloadAnimation();
            _weapon.PlayReloadAnimationServerRpc();

            if(ShouldSuppressLegacyReloadSound()) return;
            if(UseKinemationInternalSounds()) return;
            if(_weapon.OwnerContext is not { IsOwner: true }) return;
            if(_weapon.AudioRelay == null) return;
            var soundId = _weapon.CurrentWeaponData != null ? _weapon.CurrentWeaponData.reloadSoundId : "";
            if(!string.IsNullOrWhiteSpace(soundId)) {
                _weapon.AudioRelay.RequestPlayAttached(soundId,
                    new NetworkObjectReference(_weapon.OwnerContext.NetworkObject), allowOverlap: false);
            }
        }

        public void ExitReloadAnimation() {
            if(_weapon.KinDriver != null) {
                KinFpWeaponDriver.PlayReloadCompleteAnimation();
            }
        }

        public void ProcessKinemationSoundEvents() {
            if(_weapon.KinDriver == null) return;

            _weapon.KinemationWeaponSoundEventBuffer.Clear();
            _weapon.KinDriver.ConsumeWeaponEventSoundIndices(_weapon.KinemationWeaponSoundEventBuffer);

            if(_weapon.KinemationWeaponSoundEventBuffer.Count == 0) return;
            if(!UseKinemationEventSoundRouting()) return;
            if(_weapon.OwnerContext is not { IsOwner: true }) return;
            if(_weapon.AudioRelay == null || _weapon.OwnerContext.NetworkObject == null) return;

            var attachRef = new NetworkObjectReference(_weapon.OwnerContext.NetworkObject);
            foreach(var clipIndex in _weapon.KinemationWeaponSoundEventBuffer) {
                if(!_weapon.KinDriver.TryGetKinemationSoundId(clipIndex, out var eventSoundId)) continue;
                if(string.IsNullOrWhiteSpace(eventSoundId)) continue;
                _weapon.AudioRelay.RequestPlayAttached(eventSoundId, attachRef, allowOverlap: true);
            }
        }

        public void StopKinemationEventSounds() {
            if(_weapon.KinDriver == null) return;
            if(!UseKinemationEventSoundRouting()) return;
            if(_weapon.OwnerContext is not { IsOwner: true }) return;
            if(_weapon.AudioRelay == null) return;

            var eventClipCount = _weapon.KinDriver.GetKinemationSoundClipCount();
            for(var clipIndex = 0; clipIndex < eventClipCount; clipIndex++) {
                if(!_weapon.KinDriver.IsLikelyReloadEventSoundClip(clipIndex)) continue;
                if(!_weapon.KinDriver.TryGetKinemationSoundId(clipIndex, out var eventSoundId)) continue;
                if(string.IsNullOrWhiteSpace(eventSoundId)) continue;
                _weapon.AudioRelay.RequestStop(eventSoundId);
            }
        }

        public bool UseKinemationInternalSounds() {
            return _weapon.KinDriver != null && _weapon.KinDriver.AreKinemationSoundsEnabled();
        }

        private bool UseKinemationEventSoundRouting() {
            return _weapon.KinDriver != null && _weapon.KinDriver.IsKinemationSoundRoutingEnabled();
        }

        public bool ShouldSuppressLegacyReloadSound() {
            return UseKinemationEventSoundRouting() &&
                   _weapon.KinDriver != null &&
                   _weapon.KinDriver.HasAnyKinemationEventSound();
        }

        public void ClearKinemationMuzzleFx() {
            if(_weapon.KinemationLocalMuzzleFxInstance != null) {
                QuiesceMuzzleFxInstance(_weapon.KinemationLocalMuzzleFxInstance, _weapon.KinemationLocalMuzzleVfx);
                Object.Destroy(_weapon.KinemationLocalMuzzleFxInstance);
            }

            _weapon.KinemationLocalMuzzleFxInstance = null;
            _weapon.KinemationLocalMuzzleVfx = null;
            _weapon.KinemationLocalMuzzleSourcePrefab = null;
        }

        public void PrewarmKinemationMuzzleFx() {
            if(_weapon.HasPrewarmedKinemationMuzzleForCurrentWeapon) return;
            if(_weapon.KinDriver == null) return;
            if(_weapon.CurrentWeaponData == null || _weapon.CurrentWeaponData.muzzleFlashPrefab == null) return;

            if(!_weapon.TryGetOwnerMuzzleTransformInternal(out var muzzleTransform, "PrewarmKinemationMuzzleFx",
                   logErrors: false)) {
                return;
            }

            var preferredDirection = Vector3.zero;
            var fpCameraTransform = _weapon.OwnerContext != null ? _weapon.OwnerContext.FpCameraTransform : null;
            if(fpCameraTransform != null) {
                preferredDirection = fpCameraTransform.forward;
            }

            var desiredWorldRotation = ResolveKinemationMuzzleFxRotation(muzzleTransform, preferredDirection);
            var fxGo = EnsureKinemationMuzzleFx(muzzleTransform, desiredWorldRotation);
            if(fxGo == null) return;

            QuiesceMuzzleFxInstance(fxGo, _weapon.KinemationLocalMuzzleVfx);
            _weapon.HasPrewarmedKinemationMuzzleForCurrentWeapon = true;
        }

        private void PlayFireAnimation(int authoritativeAmmoBeforeShot) {
            if(_weapon.KinDriver == null) return;
            _weapon.KinDriver.PlayFireAnimation(authoritativeAmmoBeforeShot);
        }

        private void PlayReloadAnimation() {
            if(_weapon.KinDriver != null) {
                _weapon.KinDriver.PlayReloadAnimation();
            }
        }

        private Quaternion ResolveKinemationMuzzleFxRotation(Transform muzzleTransform, Vector3 preferredDirection) {
            var direction = preferredDirection;
            if(direction.sqrMagnitude <= 0.0001f && muzzleTransform != null) {
                direction = muzzleTransform.forward;
            }

            if(direction.sqrMagnitude <= 0.0001f) {
                direction = _weapon.transform.forward;
            }

            direction.Normalize();

            var up = Vector3.up;
            var cameraTransform = _weapon.OwnerContext != null ? _weapon.OwnerContext.FpCameraTransform : null;
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

        private GameObject EnsureKinemationMuzzleFx(Transform muzzleTransform, Quaternion spawnRotation) {
            if(_weapon.CurrentWeaponData == null || _weapon.CurrentWeaponData.muzzleFlashPrefab == null || muzzleTransform == null) {
                return null;
            }

            var sourcePrefab = _weapon.CurrentWeaponData.muzzleFlashPrefab;
            var needsRecreate = _weapon.KinemationLocalMuzzleFxInstance == null ||
                                _weapon.KinemationLocalMuzzleSourcePrefab != sourcePrefab;
            if(needsRecreate) {
                if(_weapon.KinemationLocalMuzzleFxInstance != null) {
                    QuiesceMuzzleFxInstance(_weapon.KinemationLocalMuzzleFxInstance, _weapon.KinemationLocalMuzzleVfx);
                    Object.Destroy(_weapon.KinemationLocalMuzzleFxInstance);
                }

                _weapon.KinemationLocalMuzzleFxInstance =
                    Object.Instantiate(sourcePrefab, muzzleTransform.position, spawnRotation);
                _weapon.KinemationLocalMuzzleSourcePrefab = sourcePrefab;
                _weapon.KinemationLocalMuzzleVfx =
                    _weapon.KinemationLocalMuzzleFxInstance.GetComponent<VisualEffect>();
                if(_weapon.KinemationLocalMuzzleVfx != null) {
                    _weapon.KinemationLocalMuzzleVfx.Stop();
                    _weapon.KinemationLocalMuzzleVfx.Reinit();
                }
            } else {
                _weapon.KinemationLocalMuzzleFxInstance.transform.SetPositionAndRotation(muzzleTransform.position, spawnRotation);
            }

            AttachMuzzleFollow(_weapon.KinemationLocalMuzzleFxInstance, muzzleTransform, followRotation: false);
            ApplyLayerRecursive(_weapon.KinemationLocalMuzzleFxInstance, muzzleTransform.gameObject.layer);
            return _weapon.KinemationLocalMuzzleFxInstance;
        }

        private void TriggerKinemationLocalMuzzleFx() {
            if(_weapon.KinemationLocalMuzzleFxInstance == null) return;
            ReactivateMuzzleFxInstance(_weapon.KinemationLocalMuzzleFxInstance, _weapon.KinemationLocalMuzzleVfx);

            if(_weapon.KinemationLocalMuzzleVfx != null) {
                _weapon.KinemationLocalMuzzleVfx.Stop();
                _weapon.KinemationLocalMuzzleVfx.Reinit();
                _weapon.KinemationLocalMuzzleVfx.Play();
                return;
            }

            var particleSystems = _weapon.KinemationLocalMuzzleFxInstance.GetComponentsInChildren<ParticleSystem>(true);
            foreach(var system in particleSystems) {
                if(system == null) continue;
                system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                system.Play(true);
            }
        }

        private Quaternion ResolveWorldMuzzleFxRotation(Transform muzzleTransform, Vector3 tracerDirectionNormalized,
            bool hasTracerDirection) {
            var direction = hasTracerDirection
                ? tracerDirectionNormalized
                : muzzleTransform != null
                    ? muzzleTransform.forward
                    : _weapon.transform.forward;
            if(direction.sqrMagnitude <= 0.0001f) {
                direction = muzzleTransform != null ? muzzleTransform.forward : _weapon.transform.forward;
            }

            direction.Normalize();

            var up = muzzleTransform != null ? muzzleTransform.up : Vector3.up;
            if(Mathf.Abs(Vector3.Dot(up, direction)) > 0.98f) {
                up = Vector3.right;
            }

            return Quaternion.LookRotation(direction, up);
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
            var inheritedLateralVelocity = ComputeTracerLateralVelocity(shooterVelocity, shotDirection);

            var remainingDistance = distance;
            var elapsed = 0f;

            while(remainingDistance > 0) {
                var t = 1f - remainingDistance / distance;
                var basePosition = Vector3.Lerp(position, hitPoint, t);
                var fade = Mathf.Pow(1f - Mathf.Clamp01(t), Weapon.TracerPerpendicularVelocityFadeExponent);
                var offset = inheritedLateralVelocity * (elapsed * fade);
                trail.transform.position = basePosition + offset;
                var dt = Time.deltaTime;
                remainingDistance -= Weapon.BulletSpeed * dt;
                elapsed += dt;
                yield return null;
            }

            trail.transform.position = hitPoint;

            var isLocalPlayerHit = false;
            if(hitPlayer && hitPlayerRef.TryGet(out var hitNetworkObject) && hitNetworkObject != null) {
                var hitParticipant = hitNetworkObject.GetComponent<IWeaponCombatParticipant>();
                if(hitParticipant is { IsOwner: true }) {
                    isLocalPlayerHit = true;
                }
            }

            if(madeImpact && _weapon.CurrentWeaponData && _weapon.CurrentWeaponData.bulletImpact && !isLocalPlayerHit) {
                var rotation = hitNormal.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(hitNormal) : Quaternion.identity;
                var spawnPos = hitPoint + hitNormal.normalized * 0.005f;

                var impactInstance = Object.Instantiate(_weapon.CurrentWeaponData.bulletImpact.gameObject, spawnPos, rotation);
                if(hitPlayer) {
                    var decal = FindChildByNameRecursive(impactInstance.transform, "Decal");
                    if(decal != null) {
                        decal.gameObject.SetActive(false);
                    }
                } else if(_weapon.OwnerContext is { IsOwner: true } && _weapon.AudioRelay != null) {
                    _weapon.AudioRelay.RequestPlay("weapons.bullet.impact", hitPoint, allowOverlap: true);
                }
            }

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

        private static Vector3 ComputeTracerLateralVelocity(Vector3 shooterVelocity, Vector3 shotDirection) {
            if(shooterVelocity.sqrMagnitude <= 0.0001f || shotDirection.sqrMagnitude <= 0.0001f) {
                return Vector3.zero;
            }

            var direction = shotDirection.normalized;
            var parallel = Vector3.Project(shooterVelocity, direction);
            var perpendicular = shooterVelocity - parallel;
            if(perpendicular.sqrMagnitude <= 0.0001f) {
                return Vector3.zero;
            }

            var inherited = perpendicular * Weapon.TracerPerpendicularVelocityInheritanceScale;
            if(inherited.sqrMagnitude >
               Weapon.TracerPerpendicularVelocityInheritanceMax * Weapon.TracerPerpendicularVelocityInheritanceMax) {
                inherited = inherited.normalized * Weapon.TracerPerpendicularVelocityInheritanceMax;
            }

            return inherited;
        }

        private void InitializeTrailPool() {
            while(_weapon.TrailPool.Count > 0) {
                var oldTrail = _weapon.TrailPool.Dequeue();
                if(oldTrail != null && !oldTrail.gameObject.activeInHierarchy) {
                    Object.Destroy(oldTrail.gameObject);
                }
            }

            if(_weapon.CurrentWeaponData == null || _weapon.CurrentWeaponData.bulletTrail == null) return;
            for(var i = 0; i < Weapon.TrailPoolSize; i++) {
                var trailObj = Object.Instantiate(_weapon.CurrentWeaponData.bulletTrail);
                trailObj.emitting = false;
                trailObj.gameObject.SetActive(false);
                _weapon.TrailPool.Enqueue(trailObj);
            }
        }

        private TrailRenderer GetTrailFromPool() {
            TrailRenderer trail = null;
            var attempts = 0;

            while(attempts < _weapon.TrailPool.Count && _weapon.TrailPool.Count > 0) {
                var candidate = _weapon.TrailPool.Dequeue();
                _weapon.TrailPool.Enqueue(candidate);

                if(candidate != null && !candidate.gameObject.activeInHierarchy) {
                    trail = candidate;
                    break;
                }

                attempts++;
            }

            if(trail != null || _weapon.CurrentWeaponData == null || _weapon.CurrentWeaponData.bulletTrail == null) return trail;
            trail = Object.Instantiate(_weapon.CurrentWeaponData.bulletTrail);
            trail.emitting = false;
            return trail;
        }

        private void ReturnTrailToPool(TrailRenderer trail) {
            if(trail == null) return;
            if(trail.gameObject == null) return;
            if(_weapon.CurrentWeaponData == null || _weapon.CurrentWeaponData.bulletTrail == null) return;

            trail.emitting = false;
            trail.gameObject.SetActive(false);
            trail.Clear();
            _weapon.TrailPool.Enqueue(trail);
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

        [DefaultExecutionOrder(7100)]
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

        public void InitializeTrailPoolFacade() {
            InitializeTrailPool();
        }
    }
}
