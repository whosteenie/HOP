using System.Collections.Generic;
using Diagnostics;
using Game.Weapon.Core;

namespace Game.Weapon.Kinemation {
    /// <summary>Pending weapon fire/event sound queues and reload-event clip detection for KIN viewmodel sound routing.</summary>
    internal sealed class KinDriverSoundEvents {
        private static readonly HashSet<int> MissingKinemationReloadSoundIndexWarnings = new();

        private readonly IKinDriverResolverContext _context;
        private readonly KinActiveWeaponResolver _resolver;
        private readonly KinDriverAudio _audio;
        private readonly List<int> _pendingWeaponEventSoundIndices = new();

        public KinDriverSoundEvents(IKinDriverResolverContext context, KinActiveWeaponResolver resolver,
            KinDriverAudio audio) {
            _context = context;
            _resolver = resolver;
            _audio = audio;
        }

        public bool IsKinemationSoundRoutingEnabled(FuncBool tryCacheActiveWeapon) {
            if(!_context.RouteWeaponSoundEventsToAudioService) return false;
            return tryCacheActiveWeapon() && _resolver.ActiveWeapon != null && _resolver.ActiveWeapon.weaponSettings != null;
        }

        public int GetKinemationSoundClipCount(FuncBool tryCacheActiveWeapon) {
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

        public void NotifyWeaponEventSoundEvent(int clipIndex, FuncBool isRoutingEnabled) {
            if(!isRoutingEnabled()) return;
            if(clipIndex < 0) return;
            var activeWeapon = _resolver.ActiveWeapon;
            var eventSounds = activeWeapon != null ? activeWeapon.weaponSettings != null ? activeWeapon.weaponSettings.weaponEventSounds : null : null;

            if(eventSounds == null || clipIndex >= eventSounds.Count || eventSounds[clipIndex] == null) return;
            _pendingWeaponEventSoundIndices.Add(clipIndex);
        }

        public void ClearPendingWeaponSoundEvents() {
            _pendingWeaponEventSoundIndices.Clear();
        }

        public void ConsumeWeaponEventSoundIndices(List<int> destination) {
            if(destination == null || _pendingWeaponEventSoundIndices.Count == 0) return;
            destination.AddRange(_pendingWeaponEventSoundIndices);
            _pendingWeaponEventSoundIndices.Clear();
        }

        public bool TryGetKinemationSoundId(int clipIndex, out string soundId) {
            soundId = "";
            if(clipIndex < 0) return false;
            var activeWeapon = _resolver.ActiveWeapon;
            if(activeWeapon == null || activeWeapon.weaponSettings == null) return false;
            var eventSounds = activeWeapon.weaponSettings.weaponEventSounds;
            if(eventSounds == null || clipIndex >= eventSounds.Count || eventSounds[clipIndex] == null) return false;
            soundId = KinSoundIdUtility.BuildEventSoundId(_audio.ActiveWeaponSoundKey, clipIndex);
            return !string.IsNullOrWhiteSpace(soundId);
        }

        private static void ReportMissingReloadSoundIndexConfig(WeaponData data) {
            if(data == null) return;
            if(!MissingKinemationReloadSoundIndexWarnings.Add(data.GetInstanceID())) return;
            var label = string.IsNullOrWhiteSpace(data.weaponName) ? data.name : data.weaponName;
            DevLog.LogError(
                $"[KinFpWeaponDriver] WeaponData '{label}' has no kinemationReloadEventSoundIndices configured. " +
                "Reload event SFX stopping is strict and requires explicit index assignment.",
                data);
        }
    }

    internal delegate bool FuncBool();
}
