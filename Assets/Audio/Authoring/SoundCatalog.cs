using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Audio2 {
    [CreateAssetMenu(fileName = "SoundCatalog", menuName = "AudioService/Sound Catalog")]
    public sealed class SoundCatalog : ScriptableObject {
        [Serializable]
        public struct Entry {
            public string id;
            public SoundCue cue;
        }

        [Header("Entries")]
        public List<Entry> entries = new();

        private Dictionary<string, SoundCue> _lookup;

        public void InitializeLookup(bool logWarnings) {
            if(_lookup == null) {
                _lookup = new Dictionary<string, SoundCue>(StringComparer.Ordinal);
            } else {
                _lookup.Clear();
            }

            if(entries == null) return;

            for(var i = 0; i < entries.Count; i++) {
                var e = entries[i];
                if(string.IsNullOrWhiteSpace(e.id) || e.cue == null) {
                    if(logWarnings) {
                        Debug.LogWarning($"[AudioService] SoundCatalog '{name}': invalid entry at index {i} (id/cue missing).", this);
                    }
                    continue;
                }

                if(_lookup.ContainsKey(e.id)) {
                    if(logWarnings) {
                        Debug.LogWarning($"[AudioService] SoundCatalog '{name}': duplicate id '{e.id}'.", this);
                    }
                    continue;
                }

                _lookup.Add(e.id, e.cue);
            }
        }

        public bool TryGetCue(string id, out SoundCue cue) {
            cue = null;
            if(string.IsNullOrWhiteSpace(id)) return false;
            if(_lookup == null) {
                InitializeLookup(logWarnings: false);
            }
            return _lookup.TryGetValue(id, out cue);
        }
    }
}

