using System.Collections.Generic;
using Game.Weapons.Core;
using UnityEngine;

namespace Game.Weapons.Kinemation {
    /// <summary>Pending weapon fire/event sound queues and reload-event clip detection for KIN viewmodel sound routing.</summary>
    internal sealed class KinemationDriverSoundEvents {
        private static readonly HashSet<int> MissingKinemationReloadSoundIndexWarnings = new();

        private readonly IKinemationDriverResolverContext _context;
        private readonly KinemationActiveWeaponResolver _resolver;
        private readonly KinemationDriverAudio _audio;
        private int _pendingWeaponFireSoundEvents;
        private readonly List<int> _pendingWeaponEventSoundIndices = new();

        public KinemationDriverSoundEvents(IKinemationDriverResolverContext context, KinemationActiveWeaponResolver resolver,
            KinemationDriverAudio audio) {
            _context = context;
            _resolver = resolver;
            _audio = audio;
        }

        public bool IsKinemationSoundEventRoutingEnabled(FuncBool tryCacheActiveWeapon) {
            if(!_context.RouteWeaponSoundEventsToAudioService) return false;
            return tryCacheActiveWeapon() && _resolver.ActiveWeapon != null && _resolver.ActiveWeapon.weaponSettings != null;
        }

        public int GetKinemationEventSoundClipCount(FuncBool tryCacheActiveWeapon) {
            if(!tryCacheActiveWeapon() || _resolver.ActiveWeapon == null || _resolver.ActiveWeapon.weaponSettings == null)
                return 0;
            var eventSounds = _resolver.ActiveWeapon.weaponSettings.weaponEventSounds;
            return eventSounds != null ? eventSounds.Count : 0;
        }

        public bool IsLikelyReloadEventSoundClip(int clipIndex, FuncBool tryCacheActiveWeapon) {
            if(!tryCacheActiveWeapon() || _resolver.ActiveWeapon == null || _resolver.ActiveWeapon.weaponSettings == null)
                return false;
            var eventSounds = _resolver.ActiveWeapon.weaponSettings.weaponEventSounds;
            if(eventSounds == null || clipIndex < 0 || clipIndex >= eventSounds.Count) return false;
            var data = _resolver.GetActiveWeaponData();
            if(data != null && data.kinemationReloadEventSoundIndices is { Length: > 0 }) {
                foreach(var idx in data.kinemationReloadEventSoundIndices) {
                    if(idx == clipIndex) return true;
                }
                return false;
            }
            ReportMissingReloadSoundIndexConfig(data);
            return false;
        }

        public void NotifyWeaponFireSoundEvent(FuncBool isRoutingEnabled) {
            if(!isRoutingEnabled()) return;
            var activeWeapon = _resolver.ActiveWeapon;
            if(activeWeapon == null || activeWeapon.weaponSettings == null || activeWeapon.weaponSettings.fireSounds == null) return;
            if(!HasAnyValidClip(activeWeapon.weaponSettings.fireSounds)) return;
            _pendingWeaponFireSoundEvents++;
        }

        public void NotifyWeaponEventSoundEvent(int clipIndex, FuncBool isRoutingEnabled) {
            if(!isRoutingEnabled()) return;
            if(clipIndex < 0) return;
            var activeWeapon = _resolver.ActiveWeapon;
            var eventSounds = activeWeapon != null ? activeWeapon.weaponSettings != null ? activeWeapon.weaponSettings.weaponEventSounds : null : null;

            if(eventSounds == null || clipIndex >= eventSounds.Count || eventSounds[clipIndex] == null) return;
            _pendingWeaponEventSoundIndices.Add(clipIndex);
        }

        public int ConsumeWeaponFireSoundEventCount() {
            if(_pendingWeaponFireSoundEvents <= 0) return 0;
            var count = _pendingWeaponFireSoundEvents;
            _pendingWeaponFireSoundEvents = 0;
            return count;
        }

        public void ClearPendingWeaponSoundEvents() {
            _pendingWeaponFireSoundEvents = 0;
            _pendingWeaponEventSoundIndices.Clear();
        }

        public void ConsumeWeaponEventSoundIndices(List<int> destination) {
            if(destination == null || _pendingWeaponEventSoundIndices.Count == 0) return;
            destination.AddRange(_pendingWeaponEventSoundIndices);
            _pendingWeaponEventSoundIndices.Clear();
        }

        public bool TryGetKinemationEventSoundId(int clipIndex, out string soundId) {
            soundId = "";
            if(clipIndex < 0) return false;
            var activeWeapon = _resolver.ActiveWeapon;
            if(activeWeapon == null || activeWeapon.weaponSettings == null) return false;
            var eventSounds = activeWeapon.weaponSettings.weaponEventSounds;
            if(eventSounds == null || clipIndex >= eventSounds.Count || eventSounds[clipIndex] == null) return false;
            soundId = KinemationSoundIdUtility.BuildEventSoundId(_audio.ActiveWeaponSoundKey, clipIndex);
            return !string.IsNullOrWhiteSpace(soundId);
        }

        private static bool HasAnyValidClip(List<AudioClip> clips) {
            if(clips == null || clips.Count == 0) return false;
            foreach(var c in clips) {
                if(c != null) return true;
            }
            return false;
        }

        private static void ReportMissingReloadSoundIndexConfig(WeaponData data) {
            if(data == null) return;
            if(!MissingKinemationReloadSoundIndexWarnings.Add(data.GetInstanceID())) return;
            var label = string.IsNullOrWhiteSpace(data.weaponName) ? data.name : data.weaponName;
            Debug.LogError(
                $"[KinemationFpWeaponDriver] WeaponData '{label}' has no kinemationReloadEventSoundIndices configured. " +
                "Reload event SFX stopping is strict and requires explicit index assignment.",
                data);
        }
    }

    internal delegate bool FuncBool();
}
