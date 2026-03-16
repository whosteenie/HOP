#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Game.Audio.Authoring;
using UnityEditor;
using UnityEngine;

namespace Game.Audio.Editor {
    public static class SoundCatalogValidator {
        [MenuItem("Tools/AudioService/Validate Selected SoundCatalog", priority = 0)]
        private static void ValidateSelected() {
            var catalog = Selection.activeObject as SoundCatalog;
            if(catalog == null) {
                Debug.LogWarning("[AudioService] Select a SoundCatalog asset to validate.");
                return;
            }

            var ok = Validate(catalog, logToConsole: true);
            if(ok) {
                Debug.Log($"[AudioService] SoundCatalog '{catalog.name}' validated OK.", catalog);
            }
        }

        private static bool Validate(SoundCatalog catalog, bool logToConsole) {
            if(catalog == null) return false;

            var ok = true;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            if(catalog.entries == null) {
                if(logToConsole) Debug.LogWarning($"[AudioService] SoundCatalog '{catalog.name}' has null entries list.", catalog);
                return false;
            }

            for(var i = 0; i < catalog.entries.Count; i++) {
                var e = catalog.entries[i];
                if(string.IsNullOrWhiteSpace(e.id)) {
                    ok = false;
                    if(logToConsole) Debug.LogWarning($"[AudioService] Catalog '{catalog.name}' entry {i} missing id.", catalog);
                    continue;
                }

                if(e.cue == null) {
                    ok = false;
                    if(logToConsole) Debug.LogWarning($"[AudioService] Catalog '{catalog.name}' entry '{e.id}' missing cue.", catalog);
                    continue;
                }

                if(!seen.Add(e.id)) {
                    ok = false;
                    if(logToConsole) Debug.LogWarning($"[AudioService] Catalog '{catalog.name}' duplicate id '{e.id}'.", catalog);
                }

                var cue = e.cue;
                if(string.IsNullOrWhiteSpace(cue.id)) {
                    // Not fatal; some teams prefer catalog-only IDs.
                    if(logToConsole) Debug.LogWarning($"[AudioService] Cue '{cue.name}' has empty internal id field (catalog id '{e.id}').", cue);
                }

                if(!cue.HasValidVariants()) {
                    ok = false;
                    if(logToConsole) Debug.LogWarning($"[AudioService] Cue '{cue.name}' (id '{e.id}') has no valid variants.", cue);
                }

                if(!(cue.maxDistance < cue.minDistance)) continue;
                ok = false;
                if(logToConsole) Debug.LogWarning($"[AudioService] Cue '{cue.name}' has maxDistance < minDistance.", cue);
            }

            return ok;
        }
    }
}
#endif

