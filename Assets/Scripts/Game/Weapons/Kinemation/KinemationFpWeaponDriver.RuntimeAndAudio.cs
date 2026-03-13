using System;
using System.Collections.Generic;
using Game.Weapons.Kinemation;
using KINEMATION.FPSAnimationPack.Scripts.Sounds;
using KINEMATION.FPSAnimationPack.Scripts.Weapon;
using UnityEngine;

namespace Game.Weapons {
    public sealed partial class KinemationFpWeaponDriver {
        private void ApplyFixedWristOffsets() {
            if(_playerInstance == null) return;

            CacheWristDebugBonesIfNeeded();
            if(_wristDebugUpperarmLeft == null && _wristDebugTwistLeft == null) return;

            var preserveHandGrip = _wristDebugHandLeft != null;
            Vector3 cachedHandPosition = default;
            Quaternion cachedHandRotation = default;
            if(preserveHandGrip) {
                cachedHandPosition = _wristDebugHandLeft.position;
                cachedHandRotation = _wristDebugHandLeft.rotation;
            }

            if(_wristDebugUpperarmLeft != null && FixedUpperarmLeftPositionOffset.sqrMagnitude > 0.00000001f) {
                _wristDebugUpperarmLeft.localPosition += FixedUpperarmLeftPositionOffset;
            }

            if(_wristDebugTwistLeft != null && FixedTwistLeftEulerOffset.sqrMagnitude > 0.000001f) {
                var delta = Quaternion.Euler(FixedTwistLeftEulerOffset);
                _wristDebugTwistLeft.localRotation *= delta;
            }

            if(preserveHandGrip) {
                _wristDebugHandLeft.SetPositionAndRotation(cachedHandPosition, cachedHandRotation);
            }
        }

        private void CacheWristDebugBonesIfNeeded() {
            if(_hasCachedWristDebugBones || _playerInstance == null) return;

            var root = _playerInstance.transform;
            TryFindChildByName(root, "clavicle_l", out _clavicleLeft);
            TryFindChildByName(root, "upperarm_l", out _wristDebugUpperarmLeft);
            TryFindChildByName(root, "lowerarm_l", out _wristDebugLowerarmLeft);
            TryFindChildByName(root, "lowerarm_twist_01_l", out _wristDebugTwistLeft);
            TryFindChildByName(root, "hand_l", out _wristDebugHandLeft);
            TryFindChildByName(root, "ik_hand_l", out _ikHandLeft);
            TryFindChildByName(root, "GrappleOrigin", out _grappleOrigin);
            _hasCachedWristDebugBones = true;
        }

        private void ApplyActiveWeaponSoundToggles(FPSWeapon activeWeapon) {
            if(activeWeapon == null) return;

            var weaponSounds = GetWeaponSounds(activeWeapon);
            if(weaponSounds == null || weaponSounds.Length == 0) return;

            var shouldEnableSounds = !disableKinemationWeaponSounds && !routeWeaponSoundEventsToAudioService;
            var sharedAudioSource = shouldEnableSounds ? EnsureDedicatedWeaponAudioSource() : null;
            foreach(var weaponSound in weaponSounds) {
                if(weaponSound == null) continue;
                var resolvedAudioSource = shouldEnableSounds
                    ? GetOrAssignWeaponSoundAudioSource(weaponSound, sharedAudioSource)
                    : null;

                weaponSound.enabled = shouldEnableSounds && resolvedAudioSource != null;

                var audioSources = weaponSound.GetComponents<AudioSource>();
                foreach(var source in audioSources) {
                    if(source != null) {
                        source.enabled = shouldEnableSounds;
                    }
                }
            }
        }

        private void RefreshActiveWeaponSoundMetadata(FPSWeapon activeWeapon) {
            if(activeWeapon == null) {
                _activeWeaponSoundKey = "unknown";
                _activeWeaponFireSoundId = string.Empty;
                ApplyGrappleWeaponIndex();
                return;
            }

            var settings = activeWeapon.weaponSettings;
            _activeWeaponSoundKey = KinemationSoundIdUtility.BuildWeaponSoundKey(settings, activeWeapon.name);
            _activeWeaponFireSoundId = settings != null && HasAnyValidAudioClip(settings.fireSounds)
                ? KinemationSoundIdUtility.BuildFireSoundId(_activeWeaponSoundKey)
                : string.Empty;
            ApplyGrappleWeaponIndex();
        }

        private void SuppressInternalMuzzleFx(FPSWeapon activeWeapon) {
            if(!disableKinemationInternalMuzzleFx || activeWeapon == null) return;
            var weaponId = activeWeapon.gameObject.GetInstanceID();
            if(_suppressedMuzzleFxWeaponIds.Contains(weaponId)) return;

            var disabledParticles = 0;
            var disabledVfx = 0;
            var disabledLights = 0;

            var particleSystems = GetWeaponParticleSystems(activeWeapon);
            foreach(var ps in particleSystems) {
                if(ps == null || !IsLikelyMuzzleFxNode(ps.transform)) continue;
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var emission = ps.emission;
                emission.enabled = false;
                disabledParticles++;
            }

            var vfxComponents = GetWeaponVisualEffects(activeWeapon);
            foreach(var vfx in vfxComponents) {
                if(vfx == null || !IsLikelyMuzzleFxNode(vfx.transform)) continue;
                vfx.Stop();
                vfx.enabled = false;
                disabledVfx++;
            }

            var lights = GetWeaponLights(activeWeapon);
            foreach(var l in lights) {
                if(l == null || !IsLikelyMuzzleFxNode(l.transform)) continue;
                l.enabled = false;
                disabledLights++;
            }

            if(disabledParticles <= 0 && disabledVfx <= 0 && disabledLights <= 0) return;
            _suppressedMuzzleFxWeaponIds.Add(weaponId);
        }

        private static bool IsLikelyMuzzleFxNode(Transform transform) {
            var cursor = transform;
            while(cursor != null) {
                var name = cursor.name.ToLowerInvariant();
                if(name.Contains("muzzle") || name.Contains("flash") || name.Contains("shotfx") ||
                   name.Contains("firefx") || name.Contains("fire_fx") || name.Contains("vfx")) {
                    return true;
                }

                cursor = cursor.parent;
            }

            return false;
        }

        private static bool HasAnyValidAudioClip(List<AudioClip> clips) {
            if(clips == null || clips.Count == 0) return false;
            foreach(var c in clips) {
                if(c != null) {
                    return true;
                }
            }

            return false;
        }

        private void ApplyAuthoritativeAmmoToActiveWeapon(int authoritativeAmmo, bool cancelPendingInvokes,
            out int clampedAmmo, out int maxAmmo) {
            clampedAmmo = 0;
            maxAmmo = 1;
            if(_activeWeapon == null) return;
            if(cancelPendingInvokes) {
                _activeWeapon.CancelInvoke();
            }

            maxAmmo = Mathf.Max(1, _activeWeapon.weaponSettings != null ? _activeWeapon.weaponSettings.ammo : authoritativeAmmo);

            clampedAmmo = Mathf.Clamp(authoritativeAmmo, 0, maxAmmo);
            FpsWeaponActiveAmmoField?.SetValue(_activeWeapon, clampedAmmo);
            FpsWeaponIsReloadingField?.SetValue(_activeWeapon, false);
            FpsWeaponIsFiringField?.SetValue(_activeWeapon, false);
        }

        private void PrepareActiveWeaponForEquip() {
            if(_activeWeapon == null) return;

            _activeWeapon.CancelInvoke();
            FpsWeaponIsReloadingField?.SetValue(_activeWeapon, false);
            FpsWeaponIsFiringField?.SetValue(_activeWeapon, false);

            var weaponAnimator = FpsWeaponAnimatorField?.GetValue(_activeWeapon) as Animator;
            SnapAnimatorToIdle(weaponAnimator);
        }

        private void SyncAmmoDrivenViewmodelVisuals(int clampedAmmo, int maxAmmo) {
            if(_activeWeapon == null) return;
            maxAmmo = Mathf.Max(1, maxAmmo);

            // PDW90 viewmodel smooths ammo weight over time by default, which causes a visible one-frame
            // lag after switch/reload-cancel. Push the smoothed value directly to authoritative ammo.
            var targetWeight = 1f - (float)clampedAmmo / maxAmmo;
            var pdwAnimations = GetActiveWeaponPdwAnimations();
            foreach(var pdwAnimation in pdwAnimations) {
                if(pdwAnimation == null) continue;
                Pdw90SmoothAmmoWeightField?.SetValue(pdwAnimation, targetWeight);
            }
        }

        private static void SnapAnimatorToIdle(Animator animator, bool forceRebindIfReloadStillActive = false) {
            if(animator == null || animator.runtimeAnimatorController == null) return;

            var playedIdleOnAnyLayer = false;
            for(var layer = 0; layer < animator.layerCount; layer++) {
                if(!animator.HasState(layer, IdleHash)) continue;
                animator.Play(IdleHash, layer, 0f);
                playedIdleOnAnyLayer = true;
            }

            if(!playedIdleOnAnyLayer) {
                animator.Rebind();
                animator.Update(0f);
                return;
            }

            animator.Update(0f);

            if(!forceRebindIfReloadStillActive || !AnimatorHasReloadClip(animator)) {
                return;
            }

            animator.Rebind();
            animator.Update(0f);

            for(var layer = 0; layer < animator.layerCount; layer++) {
                if(!animator.HasState(layer, IdleHash)) continue;
                animator.Play(IdleHash, layer, 0f);
            }

            animator.Update(0f);
        }

        private void StopActiveWeaponAudioPlayback() {
            if(_weaponAudioSource != null) {
                _weaponAudioSource.Stop();
            }

            if(_activeWeapon == null) return;
            var audioSources = GetActiveWeaponAudioSources();
            foreach(var source in audioSources) {
                if(source == null) continue;
                source.Stop();
            }
        }

        private AudioSource EnsureDedicatedWeaponAudioSource() {
            if(_playerInstance == null) {
                return null;
            }

            if(_weaponAudioSource == null) {
                _weaponAudioSource = _playerInstance.GetComponent<AudioSource>();
                if(_weaponAudioSource == null) {
                    _weaponAudioSource = _playerInstance.AddComponent<AudioSource>();
                }
            }

            _weaponAudioSource.playOnAwake = false;
            _weaponAudioSource.loop = false;
            _weaponAudioSource.spatialBlend = 0f;
            _weaponAudioSource.enabled = !disableKinemationWeaponSounds && !routeWeaponSoundEventsToAudioService;
            return _weaponAudioSource;
        }

        private AudioSource GetOrAssignWeaponSoundAudioSource(FPSWeaponSound weaponSound,
            AudioSource preferredSource = null) {
            if(weaponSound == null) return null;

            var assignedSource = FpsWeaponSoundAudioSourceField?.GetValue(weaponSound) as AudioSource;
            if(assignedSource != null) {
                return assignedSource;
            }

            var resolvedSource = preferredSource ? preferredSource : EnsureDedicatedWeaponAudioSource();
            resolvedSource = resolvedSource ? resolvedSource : weaponSound.transform.root.GetComponentInChildren<AudioSource>(true);
            if(resolvedSource == null) {
                return null;
            }

            FpsWeaponSoundAudioSourceField?.SetValue(weaponSound, resolvedSource);
            return resolvedSource;
        }

        private void AttachReloadEventRelays() {
            if(_playerInstance == null) return;
            var weaponSoundPlaybackDisabled = disableKinemationWeaponSounds || routeWeaponSoundEventsToAudioService;

            var animators = _playerInstance.GetComponentsInChildren<Animator>(true);
            foreach(var animator in animators) {
                if(animator == null) continue;
                var relay = animator.GetComponent<KinemationReloadEventRelay>();
                if(relay == null) {
                    relay = animator.gameObject.AddComponent<KinemationReloadEventRelay>();
                }

                relay.Bind(this);
            }

            var weaponSounds = _playerInstance.GetComponentsInChildren<FPSWeaponSound>(true);
            foreach(var weaponSound in weaponSounds) {
                if(weaponSound == null) continue;
                var relay = weaponSound.GetComponent<KinemationReloadEventRelay>();
                if(relay == null) {
                    relay = weaponSound.gameObject.AddComponent<KinemationReloadEventRelay>();
                }

                relay.Bind(this);

                if(weaponSoundPlaybackDisabled) {
                    Destroy(weaponSound);
                }
            }

            if(!disableKinemationPlayerSounds) return;
            var playerSounds = _playerInstance.GetComponentsInChildren<FPSPlayerSound>(true);
            foreach(var playerSound in playerSounds) {
                if(playerSound == null) continue;
                if(playerSound.GetComponent<KinemationPlayerSoundEventRelay>() == null) {
                    playerSound.gameObject.AddComponent<KinemationPlayerSoundEventRelay>();
                }

                Destroy(playerSound);
            }
        }

        private bool IsAnyReloadClipActive() {
            if(_fpsAnimator != null && AnimatorHasReloadClip(_fpsAnimator)) {
                return true;
            }

            if(_activeWeapon == null) {
                return false;
            }

            var weaponAnimators = GetActiveWeaponAnimators();
            foreach(var weaponAnimator in weaponAnimators) {
                if(weaponAnimator == null || weaponAnimator == _fpsAnimator) continue;
                if(AnimatorHasReloadClip(weaponAnimator)) {
                    return true;
                }
            }

            return false;
        }

        private bool TryGetEquipStateProgress(out float normalizedProgress) {
            normalizedProgress = 0f;

            if(TryGetAnimatorEquipProgress(_fpsAnimator, out var characterProgress)) {
                normalizedProgress = characterProgress;
                return true;
            }

            if(_activeWeapon == null) {
                return false;
            }

            var weaponAnimators = GetActiveWeaponAnimators();
            foreach(var weaponAnimator in weaponAnimators) {
                if(weaponAnimator == null || weaponAnimator == _fpsAnimator) continue;
                if(!TryGetAnimatorEquipProgress(weaponAnimator, out var weaponProgress)) continue;
                normalizedProgress = Mathf.Max(normalizedProgress, weaponProgress);
                return true;
            }

            return false;
        }

        private static bool AnimatorHasReloadClip(Animator animator) {
            if(animator == null || !animator.isActiveAndEnabled) return false;

            for(var layer = 0; layer < animator.layerCount; layer++) {
                var clips = animator.GetCurrentAnimatorClipInfo(layer);
                if(clips == null || clips.Length == 0) continue;

                foreach(var clipInfo in clips) {
                    var clip = clipInfo.clip;
                    if(clip == null || string.IsNullOrEmpty(clip.name)) continue;
                    if(clip.name.IndexOf("reload", StringComparison.OrdinalIgnoreCase) >= 0) {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryGetAnimatorEquipProgress(Animator animator, out float normalizedProgress) {
            normalizedProgress = 0f;
            if(animator == null || !animator.isActiveAndEnabled) return false;

            for(var layer = 0; layer < animator.layerCount; layer++) {
                var currentState = animator.GetCurrentAnimatorStateInfo(layer);
                if(currentState.shortNameHash == EquipHash || currentState.shortNameHash == EquipOverrideHash) {
                    normalizedProgress = Mathf.Max(normalizedProgress, Mathf.Clamp01(currentState.normalizedTime));
                    return true;
                }

                if(!animator.IsInTransition(layer)) continue;
                var nextState = animator.GetNextAnimatorStateInfo(layer);
                if(nextState.shortNameHash != EquipHash && nextState.shortNameHash != EquipOverrideHash) continue;
                normalizedProgress = Mathf.Max(normalizedProgress, Mathf.Clamp01(nextState.normalizedTime));
                return true;
            }

            return false;
        }

        private void DisableFpsPlayerMovementControl() {
            if(_fpsPlayer == null) return;

            if(FpsPlayerSetMovementEnabledMethod != null) {
                FpsPlayerSetMovementEnabledMethod.Invoke(_fpsPlayer, new object[] { false });
                return;
            }

            FpsPlayerAllowControllerMovementField?.SetValue(_fpsPlayer, false);
        }

        private void LateUpdate() {
            ApplyRuntimeGrappleClavicleOffset();
            ApplyFixedWristOffsets();
            ApplySuppressedDrakeTopShellPose();
            ApplySuppressedDrakeBottomShellPose();
            ApplyHiddenKarLoopBulletPose();
        }

        private void ApplyRuntimeGrappleClavicleOffset() {
            if(!enableRuntimeGrappleClavicleOffset) return;
            if(!_isRuntimeGrappleClavicleOffsetActive || _runtimeGrappleClavicleOffset.sqrMagnitude <= 0.00000001f) return;
            if(_playerInstance == null || !_playerInstance.activeInHierarchy) return;

            CacheWristDebugBonesIfNeeded();
            if(_clavicleLeft == null && !TryFindChildByName(_playerInstance.transform, "clavicle_l", out _clavicleLeft)) {
                return;
            }

            var runtimeWeight = ComputeRuntimeGrappleOffsetWeight();
            if(runtimeWeight <= 0.0001f) return;

            var appliedOffset = _runtimeGrappleClavicleOffset * (RuntimeGrappleClavicleOffsetScale * runtimeWeight);
            _clavicleLeft.localPosition += appliedOffset;
        }

        private float ComputeRuntimeGrappleOffsetWeight() {
            if(_fpsAnimator == null || GrappleLayerIndex >= _fpsAnimator.layerCount) return 1f;

            var clipInfos = _fpsAnimator.GetCurrentAnimatorClipInfo(GrappleLayerIndex);
            if(clipInfos == null || clipInfos.Length == 0) return 0f;

            var clipWeight = 0f;
            foreach(var c in clipInfos) {
                var clip = c.clip;
                if(clip == null) continue;
                if(clip.name.IndexOf("Grapple", StringComparison.OrdinalIgnoreCase) < 0) continue;
                clipWeight = Mathf.Max(clipWeight, c.weight);
            }
            if(clipWeight <= 0.0001f) return 0f;

            var state = _fpsAnimator.GetCurrentAnimatorStateInfo(GrappleLayerIndex);
            var normalized = Mathf.Repeat(state.normalizedTime, 1f);
            var inWeight = Mathf.Clamp01(normalized / GrappleOffsetBlendInNormalized);
            var outWeight = normalized <= GrappleOffsetBlendOutStartNormalized
                ? 1f
                : 1f - Mathf.Clamp01((normalized - GrappleOffsetBlendOutStartNormalized) /
                    Mathf.Max(0.0001f, GrappleOffsetBlendOutEndNormalized - GrappleOffsetBlendOutStartNormalized));
            return clipWeight * inWeight * outWeight;
        }

        private void ApplySuppressedDrakeTopShellPose() {
            if(!_isDrakeTopShellSuppressionApplied) return;
            if(_suppressedDrakeTopShellTransform == null) return;

            if(_hasSuppressedDrakeTopShellOriginalLocalPosition) {
                _suppressedDrakeTopShellTransform.localPosition =
                    _suppressedDrakeTopShellOriginalLocalPosition + Vector3.down * DrakeTopShellHideOffset;
            }

            if(_hasSuppressedDrakeTopShellOriginalLocalScale) {
                _suppressedDrakeTopShellTransform.localScale = Vector3.zero;
            }

            if(_suppressedDrakeTopShellRenderers == null) return;
            foreach(var shellRenderer in _suppressedDrakeTopShellRenderers) {
                if(shellRenderer == null) continue;
                if(shellRenderer.enabled) {
                    shellRenderer.enabled = false;
                }
            }
        }

        private void ApplySuppressedDrakeBottomShellPose() {
            if(!_isDrakeBottomShellSuppressionApplied) return;
            if(_suppressedDrakeBottomShellTransform == null) return;

            if(_hasSuppressedDrakeBottomShellOriginalLocalPosition) {
                _suppressedDrakeBottomShellTransform.localPosition =
                    _suppressedDrakeBottomShellOriginalLocalPosition + Vector3.down * DrakeTopShellHideOffset;
            }

            if(_hasSuppressedDrakeBottomShellOriginalLocalScale) {
                _suppressedDrakeBottomShellTransform.localScale = Vector3.zero;
            }

            if(_suppressedDrakeBottomShellRenderers == null) return;
            foreach(var shellRenderer in _suppressedDrakeBottomShellRenderers) {
                if(shellRenderer == null) continue;
                if(shellRenderer.enabled) {
                    shellRenderer.enabled = false;
                }
            }
        }

        private void ApplyHiddenKarLoopBulletPose() {
            if(!_isKarLoopBulletHidden) return;
            if(_karLoopBulletTransform == null) return;

            if(_hasKarLoopBulletOriginalLocalPosition) {
                _karLoopBulletTransform.localPosition =
                    _karLoopBulletOriginalLocalPosition + Vector3.down * KarLoopBulletHideOffset;
            }

            if(_hasKarLoopBulletOriginalLocalScale) {
                _karLoopBulletTransform.localScale = Vector3.zero;
            }

            if(_karLoopBulletRenderers == null) return;
            foreach(var bulletRenderer in _karLoopBulletRenderers) {
                if(bulletRenderer == null) continue;
                if(bulletRenderer.enabled) {
                    bulletRenderer.enabled = false;
                }
            }
        }

        private void OnDestroy() {
            RestoreDrakeTopShellImmediate();
            RestoreDrakeBottomShellImmediate();
            RestoreKarLoopBulletImmediate();
            if(_runtimePlayerSettings == null) return;
            Destroy(_runtimePlayerSettings);
            _runtimePlayerSettings = null;
        }
    }
}