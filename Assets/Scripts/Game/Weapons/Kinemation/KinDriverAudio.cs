using System.Collections.Generic;
using System.Reflection;
using KINEMATION.FPSAnimationPack.Scripts.Sounds;
using KINEMATION.FPSAnimationPack.Scripts.Weapon;
using UnityEngine;

namespace Game.Weapons.Kinemation {
    /// <summary>Dedicated weapon AudioSource, sound toggles, and fire/event sound metadata for the KIN viewmodel.</summary>
    internal sealed class KinDriverAudio {
        private static readonly FieldInfo FpsWeaponSoundAudioSourceField =
            typeof(FPSWeaponSound).GetField("_audioSource", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly IKinDriverResolverContext _context;
        private readonly KinActiveWeaponResolver _resolver;
        private AudioSource _weaponAudioSource;

        public KinDriverAudio(IKinDriverResolverContext context, KinActiveWeaponResolver resolver) {
            _context = context;
            _resolver = resolver;
        }

        public string ActiveWeaponSoundKey { get; private set; } = "unknown";

        public string ActiveWeaponFireSoundId { get; private set; } = "";

        public void ApplyActiveWeaponSoundToggles(FPSWeapon activeWeapon) {
            if(activeWeapon == null) return;
            var weaponSounds = _resolver.GetWeaponSounds(activeWeapon);
            if(weaponSounds == null || weaponSounds.Length == 0) return;

            var shouldEnable = !_context.WeaponSoundPlaybackDisabled;
            var sharedSource = shouldEnable ? EnsureWeaponAudioSource() : null;
            foreach(var ws in weaponSounds) {
                if(ws == null) continue;
                var resolved = shouldEnable ? GetOrAssignWeaponSoundAudioSource(ws, sharedSource) : null;
                ws.enabled = shouldEnable && resolved != null;
                foreach(var source in ws.GetComponents<AudioSource>()) {
                    if(source != null) source.enabled = shouldEnable;
                }
            }
        }

        public void RefreshWeaponSoundMetadata(FPSWeapon activeWeapon, System.Action applyGrappleWeaponIndex) {
            if(activeWeapon == null) {
                ActiveWeaponSoundKey = "unknown";
                ActiveWeaponFireSoundId = "";
                applyGrappleWeaponIndex?.Invoke();
                return;
            }
            var settings = activeWeapon.weaponSettings;
            ActiveWeaponSoundKey = KinSoundIdUtility.BuildWeaponSoundKey(settings, activeWeapon.name);
            ActiveWeaponFireSoundId = settings != null && HasAnyValidAudioClip(settings.fireSounds)
                ? KinSoundIdUtility.BuildFireSoundId(ActiveWeaponSoundKey)
                : "";
            applyGrappleWeaponIndex?.Invoke();
        }

        public void StopActiveWeaponAudioPlayback() {
            if(_weaponAudioSource != null) _weaponAudioSource.Stop();
            var activeWeapon = _resolver.ActiveWeapon;
            if(activeWeapon == null) return;
            var sources = _resolver.GetActiveWeaponAudioSources();
            if(sources == null) return;
            foreach(var s in sources) {
                if(s != null) s.Stop();
            }
        }

        public AudioSource EnsureWeaponAudioSource() {
            var playerInstance = _context.PlayerInstance;
            if(playerInstance == null) return null;
            if(_weaponAudioSource == null) {
                _weaponAudioSource = playerInstance.GetComponent<AudioSource>();
                if(_weaponAudioSource == null) _weaponAudioSource = playerInstance.AddComponent<AudioSource>();
            }
            _weaponAudioSource.playOnAwake = false;
            _weaponAudioSource.loop = false;
            _weaponAudioSource.spatialBlend = 0f;
            _weaponAudioSource.enabled = !_context.WeaponSoundPlaybackDisabled;
            return _weaponAudioSource;
        }

        private AudioSource GetOrAssignWeaponSoundAudioSource(FPSWeaponSound weaponSound, AudioSource preferred = null) {
            if(weaponSound == null) return null;
            var assigned = FpsWeaponSoundAudioSourceField?.GetValue(weaponSound) as AudioSource;
            if(assigned != null) return assigned;
            var resolved = preferred != null ? preferred : EnsureWeaponAudioSource();
            resolved = resolved != null ? resolved : weaponSound.transform.root.GetComponentInChildren<AudioSource>(true);
            if(resolved == null) return null;
            FpsWeaponSoundAudioSourceField?.SetValue(weaponSound, resolved);
            return resolved;
        }

        private static bool HasAnyValidAudioClip(List<AudioClip> clips) {
            if(clips == null || clips.Count == 0) return false;
            foreach(var c in clips) {
                if(c != null) return true;
            }
            return false;
        }
    }
}
