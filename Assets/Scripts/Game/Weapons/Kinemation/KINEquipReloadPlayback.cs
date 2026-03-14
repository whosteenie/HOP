using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Weapons.Core;
using KINEMATION.FPSAnimationPack.Scripts.Weapon;
using UnityEngine;

namespace Game.Weapons.Kinemation {
    /// <summary>High-level equip/reload/fire playback and ammo sync. Coordinates resolver, tracker, Drake/Kar, and audio.</summary>
    internal sealed class KINEquipReloadPlayback {
        private static readonly int IdleHash = Animator.StringToHash("Idle");
        private static readonly int EquipHash = Animator.StringToHash("Equip");
        private static readonly int EquipOverrideHash = Animator.StringToHash("Equip_Override");
        private static readonly FieldInfo FpsWeaponActiveAmmoField =
            typeof(FPSWeapon).GetField("_activeAmmo", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FpsWeaponIsReloadingField =
            typeof(FPSWeapon).GetField("_isReloading", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FpsWeaponIsFiringField =
            typeof(FPSWeapon).GetField("_isFiring", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FpsWeaponCharacterAnimatorField =
            typeof(FPSWeapon).GetField("characterAnimator", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FpsWeaponAnimatorField =
            typeof(FPSWeapon).GetField("weaponAnimator", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo Pdw90SmoothAmmoWeightField =
            typeof(Pdw90Animation).GetField("_smoothAmmoWeight", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly IKinDriverResolverContext _context;
        private readonly KinActiveWeaponResolver _resolver;
        private readonly KinReloadEquipTracker _tracker;
        private readonly KinDrakeKarVisuals _drakeKar;
        private readonly KinDriverAudio _audio;
        private readonly KinGrappleClavicle _grappleClavicle;
        private readonly FuncBool _tryCacheActiveWeapon;
        private readonly float _equipUnlockNormalizedTime;

        public KINEquipReloadPlayback(IKinDriverResolverContext context,
            KinActiveWeaponResolver resolver, KinReloadEquipTracker tracker,
            KinDrakeKarVisuals drakeKar, KinDriverAudio audio, KinGrappleClavicle grappleClavicle,
            FuncBool tryCacheActiveWeapon, float equipUnlockNormalizedTime) {
            _context = context;
            _resolver = resolver;
            _tracker = tracker;
            _drakeKar = drakeKar;
            _audio = audio;
            _grappleClavicle = grappleClavicle;
            _tryCacheActiveWeapon = tryCacheActiveWeapon;
            _equipUnlockNormalizedTime = Mathf.Clamp01(equipUnlockNormalizedTime);
        }

        public void PlayEquipAnimation(bool immediate) {
            if(!_tryCacheActiveWeapon()) return;
            PrepareActiveWeaponForEquip();
            _tracker.ResetEquipTracking();
            var activeWeapon = _resolver.ActiveWeapon;
            if(activeWeapon == null) return;
            if(immediate) {
                activeWeapon.OnEquipped_Immediate();
            } else {
                _tracker.StartEquipTracking();
                activeWeapon.OnEquipped();
            }
            _grappleClavicle.ApplyGrappleWeaponIndex();
        }

        public void PlayFireAnimation(int authoritativeAmmoBeforeShot, FuncBool isAnyReloadClipActive) {
            if(!_tryCacheActiveWeapon()) return;
            var activeWeapon = _resolver.ActiveWeapon;
            if(activeWeapon == null) return;
            var reloadBlocking = _tracker.IsTrackingReload ||
                                (FpsWeaponIsReloadingField?.GetValue(activeWeapon) is true) ||
                                isAnyReloadClipActive();
            if(reloadBlocking) {
                if(_resolver.GetActiveWeaponSpecialHandling() == WeaponData.KinemationSpecialHandling.DrakeShell)
                    _tracker.MarkDrakeReloadCanceledByShot();
                var ammo = authoritativeAmmoBeforeShot >= 0 ? authoritativeAmmoBeforeShot : GetActiveWeaponAmmoForInterrupt();
                AbortReloadAndSyncAmmo(ammo);
            }
            _resolver.SuppressInternalMuzzleFx(activeWeapon, true);
            activeWeapon.OnFirePressed();
            activeWeapon.OnFireReleased();
        }

        public void PlayReloadAnimation() {
            if(!_tryCacheActiveWeapon()) return;
            var activeWeapon = _resolver.ActiveWeapon;
            if(activeWeapon == null) return;
            _tracker.ResetReloadTracking();
            _tracker.StartReloadTracking();
            var ammoAtStart = GetActiveWeaponAmmoForInterrupt();
            var isDrake = _resolver.GetActiveWeaponSpecialHandling() == WeaponData.KinemationSpecialHandling.DrakeShell;
            _tracker.SetDrakeReloadStartedEmpty(isDrake && ammoAtStart <= 0);

            var suppressTop = _tracker.GetSuppressDrakeTopShellOnNextReload() ||
                              _tracker.ShouldHideDrakeTopShellForThisReload(isDrake,
                                  _tracker.GetDrakeTopShellEjectedSinceReloadComplete(),
                                  _tracker.GetDrakeShotCanceledReloadAfterAmmoEject());
            if(suppressTop) _drakeKar.SuppressDrakeTopShellForReloadStart();
            if(_tracker.GetSuppressDrakeBottomShellOnNextReload()) _drakeKar.SuppressDrakeBottomShellForReloadStart();
            _tracker.ClearSuppressDrakeFlagsAfterReloadStart();
            activeWeapon.OnReload();
        }

        public void SyncActiveAmmo(int authoritativeAmmo) {
            if(!_tryCacheActiveWeapon() || _resolver.ActiveWeapon == null) return;
            ApplyAuthoritativeAmmoToActiveWeapon(authoritativeAmmo, false, out var clamped, out var maxAmmo);
            SyncAmmoDrivenViewmodelVisuals(clamped, maxAmmo);
        }

        public void AbortReloadAndSyncAmmo(int authoritativeAmmo) {
            if(!_tryCacheActiveWeapon() || _resolver.ActiveWeapon == null) return;
            var activeWeapon = _resolver.ActiveWeapon;
            activeWeapon.CancelInvoke();
            activeWeapon.OnFireReleased();
            ApplyAuthoritativeAmmoToActiveWeapon(authoritativeAmmo, false, out var clamped, out var maxAmmo);
            SyncAmmoDrivenViewmodelVisuals(clamped, maxAmmo);
            ForceReloadAnimatorsToIdle();
            _audio.StopActiveWeaponAudioPlayback();
            _tracker.ResetReloadTracking();
        }

        public bool IsAnyReloadClipActive() {
            if(_context.FpsAnimator != null && AnimatorHasReloadClip(_context.FpsAnimator)) return true;
            var activeWeapon = _resolver.ActiveWeapon;
            if(activeWeapon == null) return false;
            var animators = _resolver.GetActiveWeaponAnimators();
            if(animators == null) return false;
            foreach(var a in animators) {
                if(a != null && a != _context.FpsAnimator && AnimatorHasReloadClip(a)) return true;
            }
            return false;
        }

        public bool TryGetEquipStateProgress(out float normalizedProgress) {
            normalizedProgress = 0f;
            if(TryGetAnimatorEquipProgress(_context.FpsAnimator, out var p)) { normalizedProgress = p; return true; }
            var activeWeapon = _resolver.ActiveWeapon;
            if(activeWeapon == null) return false;
            var animators = _resolver.GetActiveWeaponAnimators();
            if(animators == null) return false;
            foreach(var a in animators) {
                if(a == null || a == _context.FpsAnimator) continue;
                if(!TryGetAnimatorEquipProgress(a, out var wp)) continue;
                normalizedProgress = Mathf.Max(normalizedProgress, wp); return true;
            }
            return false;
        }

        private int GetActiveWeaponAmmoForInterrupt() {
            var activeWeapon = _resolver.ActiveWeapon;
            if(activeWeapon == null) return 0;
            if(FpsWeaponActiveAmmoField?.GetValue(activeWeapon) is int ammo) return Mathf.Max(0, ammo);
            return activeWeapon.weaponSettings != null ? Mathf.Max(0, activeWeapon.weaponSettings.ammo) : 0;
        }

        private void PrepareActiveWeaponForEquip() {
            var activeWeapon = _resolver.ActiveWeapon;
            if(activeWeapon == null) return;
            activeWeapon.CancelInvoke();
            FpsWeaponIsReloadingField?.SetValue(activeWeapon, false);
            FpsWeaponIsFiringField?.SetValue(activeWeapon, false);
            var weaponAnimator = FpsWeaponAnimatorField?.GetValue(activeWeapon) as Animator;
            SnapAnimatorToIdle(weaponAnimator);
        }

        private void ForceReloadAnimatorsToIdle() {
            var activeWeapon = _resolver.ActiveWeapon;
            if(activeWeapon == null) return;
            var animators = new List<Animator>(8);
            AddUnique(animators, FpsWeaponCharacterAnimatorField?.GetValue(activeWeapon) as Animator);
            AddUnique(animators, FpsWeaponAnimatorField?.GetValue(activeWeapon) as Animator);
            AddUnique(animators, _context.FpsAnimator);
            var weaponAnimators = _resolver.GetActiveWeaponAnimators();
            if(weaponAnimators != null) foreach(var a in weaponAnimators) AddUnique(animators, a);
            foreach(var t in animators) SnapAnimatorToIdle(t, true);
        }

        private void ApplyAuthoritativeAmmoToActiveWeapon(int authoritativeAmmo, bool cancelPendingInvokes, out int clampedAmmo, out int maxAmmo) {
            clampedAmmo = 0;
            maxAmmo = 1;
            var activeWeapon = _resolver.ActiveWeapon;
            if(activeWeapon == null) return;
            if(cancelPendingInvokes) activeWeapon.CancelInvoke();
            maxAmmo = Mathf.Max(1, activeWeapon.weaponSettings != null ? activeWeapon.weaponSettings.ammo : authoritativeAmmo);
            clampedAmmo = Mathf.Clamp(authoritativeAmmo, 0, maxAmmo);
            FpsWeaponActiveAmmoField?.SetValue(activeWeapon, clampedAmmo);
            FpsWeaponIsReloadingField?.SetValue(activeWeapon, false);
            FpsWeaponIsFiringField?.SetValue(activeWeapon, false);
        }

        private void SyncAmmoDrivenViewmodelVisuals(int clampedAmmo, int maxAmmo) {
            var activeWeapon = _resolver.ActiveWeapon;
            if(activeWeapon == null) return;
            maxAmmo = Mathf.Max(1, maxAmmo);
            var targetWeight = 1f - (float)clampedAmmo / maxAmmo;
            var pdwAnimations = _resolver.GetActiveWeaponPdwAnimations();
            if(pdwAnimations == null) return;
            foreach(var pdw in pdwAnimations)
                if(pdw != null) Pdw90SmoothAmmoWeightField?.SetValue(pdw, targetWeight);
        }

        private static void SnapAnimatorToIdle(Animator animator, bool forceRebindIfReloadStillActive = false) {
            if(animator == null || animator.runtimeAnimatorController == null) return;
            var playedIdle = false;
            for(var layer = 0; layer < animator.layerCount; layer++) {
                if(!animator.HasState(layer, IdleHash)) continue;
                animator.Play(IdleHash, layer, 0f);
                playedIdle = true;
            }
            if(!playedIdle) { animator.Rebind(); animator.Update(0f); return; }
            animator.Update(0f);
            if(!forceRebindIfReloadStillActive || !AnimatorHasReloadClip(animator)) return;
            animator.Rebind();
            animator.Update(0f);
            for(var layer = 0; layer < animator.layerCount; layer++)
                if(animator.HasState(layer, IdleHash)) animator.Play(IdleHash, layer, 0f);
            animator.Update(0f);
        }

        private static bool AnimatorHasReloadClip(Animator animator) {
            if(animator == null || !animator.isActiveAndEnabled) return false;
            for(var layer = 0; layer < animator.layerCount; layer++) {
                var clips = animator.GetCurrentAnimatorClipInfo(layer);
                if(clips == null || clips.Length == 0) continue;
                foreach(var info in clips) {
                    if(info.clip == null || string.IsNullOrEmpty(info.clip.name)) continue;
                    if(info.clip.name.IndexOf("reload", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
            return false;
        }

        private static bool TryGetAnimatorEquipProgress(Animator animator, out float normalizedProgress) {
            normalizedProgress = 0f;
            if(animator == null || !animator.isActiveAndEnabled) return false;
            for(var layer = 0; layer < animator.layerCount; layer++) {
                var state = animator.GetCurrentAnimatorStateInfo(layer);
                if(state.shortNameHash == EquipHash || state.shortNameHash == EquipOverrideHash) {
                    normalizedProgress = Mathf.Max(normalizedProgress, Mathf.Clamp01(state.normalizedTime));
                    return true;
                }
                if(!animator.IsInTransition(layer)) continue;
                var next = animator.GetNextAnimatorStateInfo(layer);
                if(next.shortNameHash != EquipHash && next.shortNameHash != EquipOverrideHash) continue;
                normalizedProgress = Mathf.Max(normalizedProgress, Mathf.Clamp01(next.normalizedTime));
                return true;
            }
            return false;
        }

        private static void AddUnique(List<Animator> list, Animator a) {
            if(list == null || a == null || list.Contains(a)) return;
            list.Add(a);
        }
    }
}
