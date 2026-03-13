using System;
using KINEMATION.FPSAnimationPack.Scripts.Sounds;
using KINEMATION.FPSAnimationPack.Scripts.Weapon;
using UnityEngine;
using UnityEngine.VFX;

namespace Game.Weapons.Kinemation {
    internal sealed class KinemationActiveWeaponComponentCache {
        private int _cachedActiveWeaponInstanceId;
        private KinemationWeaponPartReferences _activeWeaponPartReferences;
        private Animator[] _activeWeaponAnimators;
        private FPSWeaponSound[] _activeWeaponSounds;
        private ParticleSystem[] _activeWeaponParticleSystems;
        private VisualEffect[] _activeWeaponVfxComponents;
        private Light[] _activeWeaponLights;
        private Pdw90Animation[] _activeWeaponPdwAnimations;
        private AudioSource[] _activeWeaponAudioSources;

        public void Invalidate() {
            _cachedActiveWeaponInstanceId = 0;
            _activeWeaponPartReferences = null;
            _activeWeaponAnimators = null;
            _activeWeaponSounds = null;
            _activeWeaponParticleSystems = null;
            _activeWeaponVfxComponents = null;
            _activeWeaponLights = null;
            _activeWeaponPdwAnimations = null;
            _activeWeaponAudioSources = null;
        }

        public void Ensure(FPSWeapon activeWeapon) {
            if(activeWeapon == null) {
                Invalidate();
                return;
            }

            var instanceId = activeWeapon.gameObject.GetInstanceID();
            if(_cachedActiveWeaponInstanceId == instanceId) return;
            _cachedActiveWeaponInstanceId = instanceId;
            _activeWeaponPartReferences = null;
            _activeWeaponAnimators = null;
            _activeWeaponSounds = null;
            _activeWeaponParticleSystems = null;
            _activeWeaponVfxComponents = null;
            _activeWeaponLights = null;
            _activeWeaponPdwAnimations = null;
            _activeWeaponAudioSources = null;
        }

        public Animator[] GetAnimators(FPSWeapon activeWeapon) {
            return GetActiveComponents(activeWeapon, ref _activeWeaponAnimators);
        }

        public FPSWeaponSound[] GetSounds(FPSWeapon activeWeapon) {
            return GetActiveComponents(activeWeapon, ref _activeWeaponSounds);
        }

        public FPSWeaponSound[] GetSounds(FPSWeapon activeWeapon, FPSWeapon weapon) {
            return GetWeaponComponents(activeWeapon, weapon, ref _activeWeaponSounds);
        }

        public ParticleSystem[] GetParticleSystems(FPSWeapon activeWeapon, FPSWeapon weapon) {
            return GetWeaponComponents(activeWeapon, weapon, ref _activeWeaponParticleSystems);
        }

        public VisualEffect[] GetVisualEffects(FPSWeapon activeWeapon, FPSWeapon weapon) {
            return GetWeaponComponents(activeWeapon, weapon, ref _activeWeaponVfxComponents);
        }

        public Light[] GetLights(FPSWeapon activeWeapon, FPSWeapon weapon) {
            return GetWeaponComponents(activeWeapon, weapon, ref _activeWeaponLights);
        }

        public Pdw90Animation[] GetPdwAnimations(FPSWeapon activeWeapon) {
            return GetActiveComponents(activeWeapon, ref _activeWeaponPdwAnimations);
        }

        public AudioSource[] GetAudioSources(FPSWeapon activeWeapon) {
            return GetActiveComponents(activeWeapon, ref _activeWeaponAudioSources);
        }

        public KinemationWeaponPartReferences GetPartReferences(FPSWeapon activeWeapon) {
            if(activeWeapon == null) return null;
            Ensure(activeWeapon);
            if(_activeWeaponPartReferences != null) return _activeWeaponPartReferences;

            _activeWeaponPartReferences = activeWeapon.GetComponent<KinemationWeaponPartReferences>();
            if(_activeWeaponPartReferences == null) {
                _activeWeaponPartReferences = activeWeapon.GetComponentInChildren<KinemationWeaponPartReferences>(true);
            }

            return _activeWeaponPartReferences;
        }

        private T[] GetActiveComponents<T>(FPSWeapon activeWeapon, ref T[] cache) where T : Component {
            if(activeWeapon == null) return Array.Empty<T>();
            Ensure(activeWeapon);
            if(cache == null) {
                cache = activeWeapon.GetComponentsInChildren<T>(true);
            }

            return cache;
        }

        private T[] GetWeaponComponents<T>(FPSWeapon activeWeapon, FPSWeapon weapon, ref T[] activeWeaponCache)
            where T : Component {
            if(weapon == null) return Array.Empty<T>();
            return weapon == activeWeapon
                ? GetActiveComponents(activeWeapon, ref activeWeaponCache)
                : weapon.GetComponentsInChildren<T>(true);
        }
    }
}
