using System.Collections.Generic;
using Diagnostics;
using Game.Weapon.Core;
using KINEMATION.FPSAnimationPack.Scripts.Player;
using KINEMATION.FPSAnimationPack.Scripts.Sounds;
using KINEMATION.FPSAnimationPack.Scripts.Weapon;
using UnityEngine;
using UnityEngine.VFX;

namespace Game.Weapon.Kinemation {
    /// <summary>Small runtime surface Kinemation needs from the weapon system.</summary>
    internal interface IKinWeaponRuntimeContext {
        WeaponData GetCurrentWeaponData();
        void HandleKinemationEquipCompleted();
    }

    /// <summary>Context the resolver needs from the driver (player instance, layer, weapon state, sound flags for relay attach).</summary>
    internal interface IKinDriverResolverContext {
        GameObject PlayerInstance { get; }
        Transform DriverTransform { get; }
        FPSPlayer FpsPlayer { get; }
        Animator FpsAnimator { get; }
        int RenderLayer { get; }
        IKinWeaponRuntimeContext WeaponRuntimeContext { get; }
        bool WeaponSoundPlaybackDisabled { get; }
        bool DisableKinemationPlayerSounds { get; }
        bool RouteWeaponSoundEventsToAudioService { get; }
        bool DisableKinemationInternalMuzzleFx { get; }
        KinFpWeaponDriver DriverForRelays { get; }
        bool TryGetWeaponCameraTransform(out Transform cameraTransform);
    }

    /// <summary>Resolves active FPS weapon, muzzle transform, part references, and weapon data. Owns component cache and part-reference warnings.</summary>
    internal sealed class KinActiveWeaponResolver {
        private const int DrakeTopShellReferenceKey = 11;
        private const int DrakeBottomShellReferenceKey = 12;
        private const int KarLoopBulletReferenceKey = 13;
        private const int FpMuzzleReferenceKey = 21;

        private static readonly HashSet<int> MissingKinemationSpecialHandlingWarnings = new();
        private static readonly HashSet<int> MissingKinemationGrappleIndexWarnings = new();
        private static readonly HashSet<int> MissingKinemationPartReferenceWarnings = new();
        private static readonly HashSet<int> InvalidKinemationPartReferenceWarnings = new();

        private readonly IKinDriverResolverContext _context;
        private readonly KinActiveWeaponComponentCache _cache = new();
        private readonly HashSet<int> _suppressedMuzzleFxWeaponIds = new();

        private Transform _muzzleTransform;

        public KinActiveWeaponResolver(IKinDriverResolverContext context) {
            _context = context;
        }

        public FPSWeapon ActiveWeapon { get; private set; }

        public Transform MuzzleTransform => _muzzleTransform;

        public bool TryCacheActiveWeapon(
            System.Action<FPSWeapon> onApplySoundToggles,
            System.Action<FPSWeapon> onRefreshSoundMetadata,
            System.Action<FPSWeapon> onSuppressMuzzleFx) {
            var playerInstance = _context.PlayerInstance;
            var fpsPlayer = _context.FpsPlayer;
            var renderLayer = _context.RenderLayer;

            if(ActiveWeapon != null && !ActiveWeapon.gameObject.activeInHierarchy) {
                var resolved = FindActiveWeaponComponent();
                if(resolved != null && resolved != ActiveWeapon) {
                    ActiveWeapon = resolved;
                    _muzzleTransform = null;
                    _cache.Invalidate();
                    ApplyLayerShadowsAndRelays(playerInstance, renderLayer);
                }
            }

            if(ActiveWeapon != null) _cache.Ensure(ActiveWeapon);

            if(ActiveWeapon != null && _muzzleTransform != null && ActiveWeapon.gameObject.activeInHierarchy) {
                onApplySoundToggles?.Invoke(ActiveWeapon);
                onRefreshSoundMetadata?.Invoke(ActiveWeapon);
                onSuppressMuzzleFx?.Invoke(ActiveWeapon);
                return true;
            }

            if(fpsPlayer == null || playerInstance == null) return false;

            if(ActiveWeapon == null) {
                ActiveWeapon = FindActiveWeaponComponent();
                if(ActiveWeapon == null) return false;
                _cache.Invalidate();
                if(renderLayer >= 0) KinemationViewmodelUtility.SetLayerRecursive(playerInstance, renderLayer);
                KinemationViewmodelUtility.DisableViewmodelShadows(playerInstance);
                KinemationViewmodelUtility.AttachReloadEventRelays(playerInstance, _context.DriverForRelays,
                    _context.WeaponSoundPlaybackDisabled, _context.DisableKinemationPlayerSounds);
            }

            _cache.Ensure(ActiveWeapon);
            if(_muzzleTransform == null) {
                var partRefs = GetActivePartReferences();
                if(partRefs != null) {
                    TryResolvePartReference(partRefs.FpMuzzleTransform, FpMuzzleReferenceKey,
                        nameof(KinWeaponPartReferences.FpMuzzleTransform), out _muzzleTransform);
                } else {
                    ReportMissingPartReference(FpMuzzleReferenceKey,
                        nameof(KinWeaponPartReferences.FpMuzzleTransform), true);
                }
            }

            onApplySoundToggles?.Invoke(ActiveWeapon);
            onRefreshSoundMetadata?.Invoke(ActiveWeapon);
            onSuppressMuzzleFx?.Invoke(ActiveWeapon);
            return ActiveWeapon != null;
        }

        private void ApplyLayerShadowsAndRelays(GameObject playerInstance, int renderLayer) {
            if(playerInstance == null) return;
            if(renderLayer >= 0) KinemationViewmodelUtility.SetLayerRecursive(playerInstance, renderLayer);
            KinemationViewmodelUtility.DisableViewmodelShadows(playerInstance);
            KinemationViewmodelUtility.AttachReloadEventRelays(playerInstance, _context.DriverForRelays,
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

        public WeaponData GetActiveWeaponData() {
            return _context.WeaponRuntimeContext?.GetCurrentWeaponData();
        }

        public WeaponData.KinemationSpecialHandling GetActiveWeaponHandling() {
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

        public Animator[] GetActiveWeaponAnimators() => _cache.GetAnimators(ActiveWeapon);
        public FPSWeaponSound[] GetActiveWeaponSounds() => _cache.GetSounds(ActiveWeapon);
        public FPSWeaponSound[] GetWeaponSounds(FPSWeapon weapon) => _cache.GetSounds(ActiveWeapon, weapon);
        private ParticleSystem[] GetWeaponParticleSystems(FPSWeapon weapon) => _cache.GetParticleSystems(ActiveWeapon, weapon);
        private VisualEffect[] GetWeaponVisualEffects(FPSWeapon weapon) => _cache.GetVisualEffects(ActiveWeapon, weapon);
        private Light[] GetWeaponLights(FPSWeapon weapon) => _cache.GetLights(ActiveWeapon, weapon);
        public Pdw90Animation[] GetActiveWeaponPdwAnimations() => _cache.GetPdwAnimations(ActiveWeapon);
        public AudioSource[] GetActiveWeaponAudioSources() => _cache.GetAudioSources(ActiveWeapon);
        private KinWeaponPartReferences GetActivePartReferences() => _cache.GetPartReferences(ActiveWeapon);

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
            if(ActiveWeapon == null) return false;
            var partRefs = GetActivePartReferences();
            if(partRefs != null)
                return TryResolvePartReference(partRefs.DrakeTopShell, DrakeTopShellReferenceKey,
                    nameof(KinWeaponPartReferences.DrakeTopShell), out topShell);
            ReportMissingPartReference(DrakeTopShellReferenceKey, nameof(KinWeaponPartReferences.DrakeTopShell), true);
            return false;
        }

        public bool TryResolveDrakeBottomShell(out Transform bottomShell) {
            bottomShell = null;
            if(ActiveWeapon == null) return false;
            var partRefs = GetActivePartReferences();
            if(partRefs != null)
                return TryResolvePartReference(partRefs.DrakeBottomShell, DrakeBottomShellReferenceKey,
                    nameof(KinWeaponPartReferences.DrakeBottomShell), out bottomShell);
            ReportMissingPartReference(DrakeBottomShellReferenceKey, nameof(KinWeaponPartReferences.DrakeBottomShell), true);
            return false;
        }

        public bool TryResolveKarLoopBullet(out Transform loopBullet) {
            loopBullet = null;
            if(ActiveWeapon == null) return false;
            var partRefs = GetActivePartReferences();
            if(partRefs != null)
                return TryResolvePartReference(partRefs.KarLoopBullet, KarLoopBulletReferenceKey,
                    nameof(KinWeaponPartReferences.KarLoopBullet), out loopBullet);
            ReportMissingPartReference(KarLoopBulletReferenceKey, nameof(KinWeaponPartReferences.KarLoopBullet), true);
            return false;
        }

        private bool TryResolvePartReference(Transform configuredPart, int partKey, string partFieldName, out Transform resolved) {
            resolved = null;
            if(ActiveWeapon == null) return false;
            if(configuredPart == null) {
                ReportMissingPartReference(partKey, partFieldName, false);
                return false;
            }
            if(!configuredPart.IsChildOf(ActiveWeapon.transform)) {
                ReportInvalidPartReference(partKey, partFieldName, configuredPart);
                return false;
            }
            resolved = configuredPart;
            return true;
        }

        private int BuildPartReferenceWarningKey(int partKey) {
            var weaponId = ActiveWeapon != null ? ActiveWeapon.GetInstanceID() : 0;
            return unchecked(weaponId * 397 ^ partKey);
        }

        private void ReportMissingPartReference(int partKey, string partFieldName, bool missingComponent) {
            var key = BuildPartReferenceWarningKey(partKey);
            if(!MissingKinemationPartReferenceWarnings.Add(key)) return;
            var label = GetActiveWeaponLabel();
            var guidance = missingComponent
                ? "Add KinWeaponPartReferences to the weapon prefab and assign required parts."
                : "Assign this field on KinWeaponPartReferences.";
            DevLog.LogError($"[KinFpWeaponDriver] Weapon '{label}' is missing explicit part reference '{partFieldName}'. {guidance}", ActiveWeapon);
        }

        private void ReportInvalidPartReference(int partKey, string partFieldName, Transform configuredPart) {
            var key = BuildPartReferenceWarningKey(partKey);
            if(!InvalidKinemationPartReferenceWarnings.Add(key)) return;
            var label = GetActiveWeaponLabel();
            DevLog.LogError($"[KinFpWeaponDriver] Weapon '{label}' has invalid part reference '{partFieldName}' (assigned '{configuredPart.name}', outside active weapon hierarchy).", ActiveWeapon);
        }

        private static void ReportMissingAssignment(WeaponData data, HashSet<int> cache, string fieldName, string impact) {
            if(data == null || cache == null) return;
            if(!cache.Add(data.GetInstanceID())) return;
            var label = string.IsNullOrWhiteSpace(data.weaponName) ? data.name : data.weaponName;
            DevLog.LogError($"[KinFpWeaponDriver] WeaponData '{label}' has {fieldName}=NULL. {impact}", data);
        }

        private string GetActiveWeaponLabel() {
            var data = GetActiveWeaponData();
            if(data != null) return string.IsNullOrWhiteSpace(data.weaponName) ? data.name : data.weaponName;
            return ActiveWeapon != null ? ActiveWeapon.name : "(unknown)";
        }
    }
}
