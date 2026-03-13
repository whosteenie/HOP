using System.Collections.Generic;
using Game.Weapons.Core;
using Game.Weapons.Manager;
using KINEMATION.FPSAnimationPack.Scripts.Player;
using KINEMATION.FPSAnimationPack.Scripts.Sounds;
using KINEMATION.FPSAnimationPack.Scripts.Weapon;
using UnityEngine;
using UnityEngine.VFX;

namespace Game.Weapons.Kinemation {
    /// <summary>Context the resolver needs from the driver (player instance, layer, weapon manager, sound flags for relay attach).</summary>
    internal interface IKinemationDriverResolverContext {
        GameObject PlayerInstance { get; }
        Transform DriverTransform { get; }
        FPSPlayer FpsPlayer { get; }
        Animator FpsAnimator { get; }
        int RenderLayer { get; }
        WeaponManager WeaponManager { get; }
        bool WeaponSoundPlaybackDisabled { get; }
        bool DisableKinemationPlayerSounds { get; }
        bool RouteWeaponSoundEventsToAudioService { get; }
        KinemationFpWeaponDriver DriverForRelays { get; }
        bool TryGetWeaponCameraTransform(out Transform cameraTransform);
    }

    /// <summary>Resolves active FPS weapon, muzzle transform, part references, and weapon data. Owns component cache and part-reference warnings.</summary>
    internal sealed class KinemationActiveWeaponResolver {
        private const int DrakeTopShellReferenceKey = 11;
        private const int DrakeBottomShellReferenceKey = 12;
        private const int KarLoopBulletReferenceKey = 13;
        private const int FpMuzzleReferenceKey = 21;

        private static readonly HashSet<int> MissingKinemationSpecialHandlingWarnings = new();
        private static readonly HashSet<int> MissingKinemationGrappleIndexWarnings = new();
        private static readonly HashSet<int> MissingKinemationPartReferenceWarnings = new();
        private static readonly HashSet<int> InvalidKinemationPartReferenceWarnings = new();

        private readonly IKinemationDriverResolverContext _context;
        private readonly KinemationActiveWeaponComponentCache _cache = new();
        private readonly HashSet<int> _suppressedMuzzleFxWeaponIds = new();

        private FPSWeapon _activeWeapon;
        private Transform _muzzleTransform;

        public KinemationActiveWeaponResolver(IKinemationDriverResolverContext context) {
            _context = context;
        }

        public FPSWeapon ActiveWeapon => _activeWeapon;
        public Transform MuzzleTransform => _muzzleTransform;

        public bool TryCacheActiveWeapon(
            System.Action<FPSWeapon> onApplySoundToggles,
            System.Action<FPSWeapon> onRefreshSoundMetadata,
            System.Action<FPSWeapon> onSuppressMuzzleFx) {
            var playerInstance = _context.PlayerInstance;
            var fpsPlayer = _context.FpsPlayer;
            var renderLayer = _context.RenderLayer;

            if(_activeWeapon != null && !_activeWeapon.gameObject.activeInHierarchy) {
                var resolved = FindActiveWeaponComponent();
                if(resolved != null && resolved != _activeWeapon) {
                    _activeWeapon = resolved;
                    _muzzleTransform = null;
                    _cache.Invalidate();
                    ApplyLayerShadowsAndRelays(playerInstance, renderLayer);
                }
            }

            if(_activeWeapon != null) _cache.Ensure(_activeWeapon);

            if(_activeWeapon != null && _muzzleTransform != null && _activeWeapon.gameObject.activeInHierarchy) {
                onApplySoundToggles?.Invoke(_activeWeapon);
                onRefreshSoundMetadata?.Invoke(_activeWeapon);
                onSuppressMuzzleFx?.Invoke(_activeWeapon);
                return true;
            }

            if(fpsPlayer == null || playerInstance == null) return false;

            if(_activeWeapon == null) {
                _activeWeapon = FindActiveWeaponComponent();
                if(_activeWeapon == null) return false;
                _cache.Invalidate();
                if(renderLayer >= 0) WeaponFpPresentation.SetLayerRecursive(playerInstance, renderLayer);
                WeaponFpPresentation.DisableViewmodelShadows(playerInstance);
                WeaponFpPresentation.AttachReloadEventRelays(playerInstance, _context.DriverForRelays,
                    _context.WeaponSoundPlaybackDisabled, _context.DisableKinemationPlayerSounds);
            }

            _cache.Ensure(_activeWeapon);
            if(_muzzleTransform == null) {
                var partRefs = GetActiveWeaponPartReferences();
                if(partRefs != null) {
                    TryResolvePartReference(partRefs.FpMuzzleTransform, FpMuzzleReferenceKey,
                        nameof(KinemationWeaponPartReferences.FpMuzzleTransform), out _muzzleTransform);
                } else {
                    ReportMissingPartReference(FpMuzzleReferenceKey,
                        nameof(KinemationWeaponPartReferences.FpMuzzleTransform), true);
                }
            }

            onApplySoundToggles?.Invoke(_activeWeapon);
            onRefreshSoundMetadata?.Invoke(_activeWeapon);
            onSuppressMuzzleFx?.Invoke(_activeWeapon);
            return _activeWeapon != null;
        }

        private void ApplyLayerShadowsAndRelays(GameObject playerInstance, int renderLayer) {
            if(playerInstance == null) return;
            if(renderLayer >= 0) WeaponFpPresentation.SetLayerRecursive(playerInstance, renderLayer);
            WeaponFpPresentation.DisableViewmodelShadows(playerInstance);
            WeaponFpPresentation.AttachReloadEventRelays(playerInstance, _context.DriverForRelays,
                _context.WeaponSoundPlaybackDisabled, _context.DisableKinemationPlayerSounds);
        }

        private FPSWeapon FindActiveWeaponComponent() {
            var playerInstance = _context.PlayerInstance;
            if(playerInstance == null) return null;
            var weapons = playerInstance.GetComponentsInChildren<FPSWeapon>(true);
            if(weapons == null || weapons.Length == 0) return null;
            foreach(var w in weapons) {
                if(w != null && w.gameObject.activeInHierarchy) return w;
            }
            return weapons[0];
        }

        public Transform GetMuzzleTransform() => _muzzleTransform;

        public WeaponData GetActiveWeaponData() {
            var wm = _context.WeaponManager;
            if(wm == null || wm.CurrentWeapon == null) return null;
            return wm.CurrentWeapon.CurrentWeaponData;
        }

        public WeaponData.KinemationSpecialHandling GetActiveWeaponSpecialHandling() {
            var data = GetActiveWeaponData();
            if(data == null) return WeaponData.KinemationSpecialHandling.Null;
            if(data.kinemationSpecialHandling == WeaponData.KinemationSpecialHandling.Null) {
                ReportMissingAssignment(data, MissingKinemationSpecialHandlingWarnings,
                    nameof(WeaponData.kinemationSpecialHandling), "Drake/Kar special handling is disabled until assigned.");
            }
            return data.kinemationSpecialHandling;
        }

        public int GetGrappleWeaponIndex() {
            var data = GetActiveWeaponData();
            if(data == null) return -1;
            if(data.kinemationGrappleWeaponIndex != WeaponData.KinemationGrappleWeaponIndex.Null)
                return (int)data.kinemationGrappleWeaponIndex;
            ReportMissingAssignment(data, MissingKinemationGrappleIndexWarnings,
                nameof(WeaponData.kinemationGrappleWeaponIndex), "Grapple animation index is invalid until assigned.");
            return -1;
        }

        public void InvalidateCache() => _cache.Invalidate();

        public void OnActiveWeaponSwitched() {
            _muzzleTransform = null;
            _cache.Invalidate();
        }

        public Animator[] GetActiveWeaponAnimators() => _cache.GetAnimators(_activeWeapon);
        public FPSWeaponSound[] GetActiveWeaponSounds() => _cache.GetSounds(_activeWeapon);
        public FPSWeaponSound[] GetWeaponSounds(FPSWeapon weapon) => _cache.GetSounds(_activeWeapon, weapon);
        private ParticleSystem[] GetWeaponParticleSystems(FPSWeapon weapon) => _cache.GetParticleSystems(_activeWeapon, weapon);
        private VisualEffect[] GetWeaponVisualEffects(FPSWeapon weapon) => _cache.GetVisualEffects(_activeWeapon, weapon);
        private Light[] GetWeaponLights(FPSWeapon weapon) => _cache.GetLights(_activeWeapon, weapon);
        public Pdw90Animation[] GetActiveWeaponPdwAnimations() => _cache.GetPdwAnimations(_activeWeapon);
        public AudioSource[] GetActiveWeaponAudioSources() => _cache.GetAudioSources(_activeWeapon);
        private KinemationWeaponPartReferences GetActiveWeaponPartReferences() => _cache.GetPartReferences(_activeWeapon);

        public void SuppressInternalMuzzleFx(FPSWeapon activeWeapon, bool disableMuzzleFx) {
            if(!disableMuzzleFx || activeWeapon == null) return;
            var weaponId = activeWeapon.gameObject.GetInstanceID();
            if(_suppressedMuzzleFxWeaponIds.Contains(weaponId)) return;
            int disabledParticles = 0, disabledVfx = 0, disabledLights = 0;
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
            if(disabledParticles > 0 || disabledVfx > 0 || disabledLights > 0) _suppressedMuzzleFxWeaponIds.Add(weaponId);
        }

        private static bool IsLikelyMuzzleFxNode(Transform t) {
            while(t != null) {
                var name = t.name.ToLowerInvariant();
                if(name.Contains("muzzle") || name.Contains("flash") || name.Contains("shotfx") ||
                   name.Contains("firefx") || name.Contains("fire_fx") || name.Contains("vfx")) return true;
                t = t.parent;
            }
            return false;
        }

        public bool TryResolveDrakeTopShell(out Transform topShell) {
            topShell = null;
            if(_activeWeapon == null) return false;
            var partRefs = GetActiveWeaponPartReferences();
            if(partRefs == null) {
                ReportMissingPartReference(DrakeTopShellReferenceKey, nameof(KinemationWeaponPartReferences.DrakeTopShell), true);
                return false;
            }
            return TryResolvePartReference(partRefs.DrakeTopShell, DrakeTopShellReferenceKey,
                nameof(KinemationWeaponPartReferences.DrakeTopShell), out topShell);
        }

        public bool TryResolveDrakeBottomShell(out Transform bottomShell) {
            bottomShell = null;
            if(_activeWeapon == null) return false;
            var partRefs = GetActiveWeaponPartReferences();
            if(partRefs == null) {
                ReportMissingPartReference(DrakeBottomShellReferenceKey, nameof(KinemationWeaponPartReferences.DrakeBottomShell), true);
                return false;
            }
            return TryResolvePartReference(partRefs.DrakeBottomShell, DrakeBottomShellReferenceKey,
                nameof(KinemationWeaponPartReferences.DrakeBottomShell), out bottomShell);
        }

        public bool TryResolveKarLoopBullet(out Transform loopBullet) {
            loopBullet = null;
            if(_activeWeapon == null) return false;
            var partRefs = GetActiveWeaponPartReferences();
            if(partRefs == null) {
                ReportMissingPartReference(KarLoopBulletReferenceKey, nameof(KinemationWeaponPartReferences.KarLoopBullet), true);
                return false;
            }
            return TryResolvePartReference(partRefs.KarLoopBullet, KarLoopBulletReferenceKey,
                nameof(KinemationWeaponPartReferences.KarLoopBullet), out loopBullet);
        }

        private bool TryResolvePartReference(Transform configuredPart, int partKey, string partFieldName, out Transform resolved) {
            resolved = null;
            if(_activeWeapon == null) return false;
            if(configuredPart == null) {
                ReportMissingPartReference(partKey, partFieldName, false);
                return false;
            }
            if(!configuredPart.IsChildOf(_activeWeapon.transform)) {
                ReportInvalidPartReference(partKey, partFieldName, configuredPart);
                return false;
            }
            resolved = configuredPart;
            return true;
        }

        private int BuildPartReferenceWarningKey(int partKey) {
            var weaponId = _activeWeapon != null ? _activeWeapon.GetInstanceID() : 0;
            return unchecked(weaponId * 397 ^ partKey);
        }

        private void ReportMissingPartReference(int partKey, string partFieldName, bool missingComponent) {
            var key = BuildPartReferenceWarningKey(partKey);
            if(!MissingKinemationPartReferenceWarnings.Add(key)) return;
            var label = GetActiveWeaponLabel();
            var guidance = missingComponent
                ? "Add KinemationWeaponPartReferences to the weapon prefab and assign required parts."
                : "Assign this field on KinemationWeaponPartReferences.";
            Debug.LogError($"[KinemationFpWeaponDriver] Weapon '{label}' is missing explicit part reference '{partFieldName}'. {guidance}", _activeWeapon);
        }

        private void ReportInvalidPartReference(int partKey, string partFieldName, Transform configuredPart) {
            var key = BuildPartReferenceWarningKey(partKey);
            if(!InvalidKinemationPartReferenceWarnings.Add(key)) return;
            var label = GetActiveWeaponLabel();
            Debug.LogError($"[KinemationFpWeaponDriver] Weapon '{label}' has invalid part reference '{partFieldName}' (assigned '{configuredPart.name}', outside active weapon hierarchy).", _activeWeapon);
        }

        private static void ReportMissingAssignment(WeaponData data, HashSet<int> cache, string fieldName, string impact) {
            if(data == null || cache == null) return;
            if(!cache.Add(data.GetInstanceID())) return;
            var label = string.IsNullOrWhiteSpace(data.weaponName) ? data.name : data.weaponName;
            Debug.LogError($"[KinemationFpWeaponDriver] WeaponData '{label}' has {fieldName}=NULL. {impact}", data);
        }

        private string GetActiveWeaponLabel() {
            var data = GetActiveWeaponData();
            if(data != null) return string.IsNullOrWhiteSpace(data.weaponName) ? data.name : data.weaponName;
            return _activeWeapon != null ? _activeWeapon.name : "(unknown)";
        }
    }
}
