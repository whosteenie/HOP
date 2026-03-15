#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Game.Audio2;
using Game.Weapon.Kinemation;
using KINEMATION.FPSAnimationPack.Scripts.Weapon;
using UnityEditor;
using UnityEngine;

namespace Audio.Editor {
    public static class KinemationSoundCueGenerator {
        private const string KinemationCueRootFolder = "Assets/Audio/SoundCue/Weapons/Kinemation";
        private const string DefaultCatalogPath = "Assets/Audio/SoundCatalog.asset";

        private readonly struct WeaponSoundSource {
            public readonly string Key;
            public readonly FPSWeaponSettings Settings;

            public WeaponSoundSource(string key, FPSWeaponSettings settings) {
                Key = key;
                Settings = settings;
            }

            public int GetClipScore() {
                var fireCount = Settings != null && Settings.fireSounds != null ? Settings.fireSounds.Count : 0;
                var eventCount = Settings != null && Settings.weaponEventSounds != null ? Settings.weaponEventSounds.Count : 0;
                return fireCount + eventCount;
            }
        }

        [MenuItem("Tools/AudioService/KINEMATION/Generate Weapon Sound Cues", priority = 10)]
        private static void GenerateWeaponSoundCues() {
            EnsureFolderPath(KinemationCueRootFolder);

            var soundCatalog = LoadSoundCatalog();
            if(soundCatalog == null) {
                Debug.LogWarning("[AudioService] KINEMATION cue generation aborted: no SoundCatalog found.");
                return;
            }

            var sourcesByKey = CollectKinemationWeaponSoundSources();
            if(sourcesByKey.Count == 0) {
                Debug.LogWarning("[AudioService] KINEMATION cue generation found no FPSWeapon prefabs with weapon settings.");
                return;
            }

            var generatedCueById = new Dictionary<string, SoundCue>(StringComparer.Ordinal);
            var createdCueCount = 0;
            var updatedCueCount = 0;

            foreach(var sourcePair in sourcesByKey) {
                var source = sourcePair.Value;
                var settings = source.Settings;
                if(settings == null) continue;

                var fileToken = source.Key.Replace('.', '_');
                var weaponFolder = $"{KinemationCueRootFolder}/{fileToken}";
                EnsureFolderPath(weaponFolder);

                if(settings.fireSounds is { Count: > 0 }) {
                    var fireId = KinSoundIdUtility.BuildFireSoundId(source.Key);
                    var firePath = $"{weaponFolder}/{fileToken}_fireCue.asset";
                    var fireCue = CreateOrUpdateCue(
                        firePath,
                        fireId,
                        settings.fireSounds,
                        isFireCue: true,
                        settings,
                        ref createdCueCount,
                        ref updatedCueCount
                    );
                    if(fireCue != null) {
                        generatedCueById[fireId] = fireCue;
                    }
                }

                if(settings.weaponEventSounds == null || settings.weaponEventSounds.Count == 0) continue;

                for(var i = 0; i < settings.weaponEventSounds.Count; i++) {
                    var clip = settings.weaponEventSounds[i];
                    if(clip == null) continue;

                    var eventId = KinSoundIdUtility.BuildEventSoundId(source.Key, i);
                    var eventPath = $"{weaponFolder}/{fileToken}_event_{i:D2}Cue.asset";
                    var eventCue = CreateOrUpdateCue(
                        eventPath,
                        eventId,
                        new List<AudioClip> { clip },
                        isFireCue: false,
                        settings,
                        ref createdCueCount,
                        ref updatedCueCount
                    );
                    if(eventCue != null) {
                        generatedCueById[eventId] = eventCue;
                    }
                }
            }

            var addedCatalogEntries = 0;
            var updatedCatalogEntries = 0;
            foreach(var cuePair in generatedCueById) {
                AddOrUpdateCatalogEntry(soundCatalog, cuePair.Key, cuePair.Value, ref addedCatalogEntries,
                    ref updatedCatalogEntries);
            }

            EditorUtility.SetDirty(soundCatalog);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log(
                $"[AudioService] Generated/updated KINEMATION cues: created={createdCueCount}, updated={updatedCueCount}, " +
                $"catalogAdded={addedCatalogEntries}, catalogUpdated={updatedCatalogEntries}.");
        }

        private static Dictionary<string, WeaponSoundSource> CollectKinemationWeaponSoundSources() {
            var result = new Dictionary<string, WeaponSoundSource>(StringComparer.Ordinal);
            var prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" });

            foreach(var guid in prefabGuids) {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if(string.IsNullOrWhiteSpace(path)) continue;

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if(prefab == null) continue;

                var fpsWeapon = prefab.GetComponentInChildren<FPSWeapon>(true);
                if(fpsWeapon == null || fpsWeapon.weaponSettings == null) continue;

                var key = KinSoundIdUtility.BuildWeaponSoundKey(fpsWeapon.weaponSettings, prefab.name);
                var source = new WeaponSoundSource(key, fpsWeapon.weaponSettings);
                if(!result.TryGetValue(key, out var existing)) {
                    result.Add(key, source);
                    continue;
                }

                // Prefer the source with more clip coverage.
                if(source.GetClipScore() > existing.GetClipScore()) {
                    result[key] = source;
                }
            }

            return result;
        }

        private static SoundCue CreateOrUpdateCue(string cueAssetPath, string id, IList<AudioClip> clips, bool isFireCue,
            FPSWeaponSettings settings, ref int createdCueCount, ref int updatedCueCount) {
            if(clips == null || clips.Count == 0) return null;

            EnsureFolderPath(Path.GetDirectoryName(cueAssetPath)?.Replace('\\', '/'));

            var cue = AssetDatabase.LoadAssetAtPath<SoundCue>(cueAssetPath);
            var isCreated = cue == null;
            if(isCreated) {
                cue = ScriptableObject.CreateInstance<SoundCue>();
                AssetDatabase.CreateAsset(cue, cueAssetPath);
            }

            cue.id = id;
            cue.bus = SoundBus.Weapons;
            cue.outputGroup = null;
            cue.is3D = true;
            cue.spatialBlend = 1f;
            cue.minDistance = 1f;
            cue.maxDistance = 50f;
            cue.rolloffMode = AudioRolloffMode.Logarithmic;
            cue.priority = 128;
            cue.maxInstances = isFireCue ? 10 : 6;
            cue.cooldownSeconds = 0f;
            cue.stealPolicy = VoiceStealPolicy.StealLowestPriorityThenOldest;
            cue.stopBehavior = StopBehavior.StopLast;
            cue.preload = false;
            cue.variants ??= new List<SoundCue.Variant>();
            cue.variants.Clear();

            var firePitch = 1f;
            var fireRandomPitch = 0f;
            var fireVolumeDb = 0f;
            if(isFireCue) {
                ResolveFireVariantDefaults(settings, out firePitch, out fireRandomPitch, out fireVolumeDb);
            }

            foreach(var clip in clips) {
                if(clip == null) continue;

                cue.variants.Add(new SoundCue.Variant {
                    clip = clip,
                    weight = 1f,
                    volumeDb = isFireCue ? fireVolumeDb : 0f,
                    pitch = isFireCue ? firePitch : 1f,
                    randomPitch = isFireCue ? fireRandomPitch : 0f
                });
            }

            EditorUtility.SetDirty(cue);
            if(isCreated) {
                createdCueCount++;
            } else {
                updatedCueCount++;
            }

            return cue;
        }

        private static void ResolveFireVariantDefaults(FPSWeaponSettings settings, out float pitch, out float randomPitch,
            out float volumeDb) {
            pitch = 1f;
            randomPitch = 0f;
            volumeDb = 0f;
            if(settings == null) return;

            var pitchMin = Mathf.Min(settings.firePitchRange.x, settings.firePitchRange.y);
            var pitchMax = Mathf.Max(settings.firePitchRange.x, settings.firePitchRange.y);
            pitch = Mathf.Clamp((pitchMin + pitchMax) * 0.5f, 0.1f, 3f);
            randomPitch = Mathf.Clamp((pitchMax - pitchMin) * 0.5f, 0f, 0.5f);

            var volumeMin = Mathf.Min(settings.fireVolumeRange.x, settings.fireVolumeRange.y);
            var volumeMax = Mathf.Max(settings.fireVolumeRange.x, settings.fireVolumeRange.y);
            var volumeMid = (volumeMin + volumeMax) * 0.5f;
            if(volumeMid <= 0f) {
                volumeDb = 0f;
                return;
            }

            volumeDb = Mathf.Clamp(20f * Mathf.Log10(volumeMid), -80f, 24f);
        }

        private static void AddOrUpdateCatalogEntry(SoundCatalog catalog, string id, SoundCue cue,
            ref int addedCatalogEntries, ref int updatedCatalogEntries) {
            if(catalog == null || cue == null || string.IsNullOrWhiteSpace(id)) return;

            catalog.entries ??= new List<SoundCatalog.Entry>();
            var firstMatchIndex = -1;
            for(var i = 0; i < catalog.entries.Count; i++) {
                var entry = catalog.entries[i];
                if(!string.Equals(entry.id, id, StringComparison.Ordinal)) continue;

                if(firstMatchIndex < 0) {
                    firstMatchIndex = i;
                    continue;
                }

                catalog.entries.RemoveAt(i);
                i--;
            }

            if(firstMatchIndex < 0) {
                catalog.entries.Add(new SoundCatalog.Entry {
                    id = id,
                    cue = cue
                });
                addedCatalogEntries++;
                return;
            }

            var existing = catalog.entries[firstMatchIndex];
            if(existing.cue == cue && string.Equals(existing.id, id, StringComparison.Ordinal)) return;

            existing.id = id;
            existing.cue = cue;
            catalog.entries[firstMatchIndex] = existing;
            updatedCatalogEntries++;
        }

        private static SoundCatalog LoadSoundCatalog() {
            var catalog = AssetDatabase.LoadAssetAtPath<SoundCatalog>(DefaultCatalogPath);
            if(catalog != null) {
                return catalog;
            }

            var guids = AssetDatabase.FindAssets("t:SoundCatalog", new[] { "Assets" });
            if(guids == null || guids.Length == 0) {
                return null;
            }

            var firstPath = AssetDatabase.GUIDToAssetPath(guids[0]);
            return AssetDatabase.LoadAssetAtPath<SoundCatalog>(firstPath);
        }

        private static void EnsureFolderPath(string assetFolderPath) {
            if(string.IsNullOrWhiteSpace(assetFolderPath)) return;
            var normalizedPath = assetFolderPath.Replace('\\', '/');
            if(AssetDatabase.IsValidFolder(normalizedPath)) return;

            var segments = normalizedPath.Split('/');
            if(segments.Length == 0) return;

            var current = segments[0];
            for(var i = 1; i < segments.Length; i++) {
                var next = segments[i];
                if(string.IsNullOrWhiteSpace(next)) continue;

                var candidate = $"{current}/{next}";
                if(!AssetDatabase.IsValidFolder(candidate)) {
                    AssetDatabase.CreateFolder(current, next);
                }

                current = candidate;
            }
        }
    }
}
#endif
