using System;
using System.Collections.Generic;
using Diagnostics;
using Game.Audio.Authoring;
using UnityEngine;

namespace Game.Audio.System {
    [DisallowMultipleComponent]
    public sealed class AudioService : MonoBehaviour {
        public static AudioService Instance { get; private set; }

        [Header("Data")]
        [SerializeField] private AudioConfig config;
        [SerializeField] private SoundCatalog catalog;

        private sealed class Voice {
            internal string Id;
            internal SoundCue Cue;
            internal AudioSource Src;
            internal float StartTime;
            internal int Priority;
        }

        private readonly Dictionary<SoundBus, Queue<AudioSource>> _pools = new();
        private readonly List<Voice> _active = new();
        private readonly Dictionary<string, List<Voice>> _activeById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, float> _lastPlayTime = new(StringComparer.Ordinal);
        private readonly Dictionary<EntityId, float> _nextSourceStateLogTime = new();
        private readonly Dictionary<string, float> _nextDropReasonLogTime = new(StringComparer.Ordinal);


        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);

            if(catalog != null) {
                catalog.InitializeLookup(logWarnings: true);
                PreloadMarkedCues();
            }

            InitializePools();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureExists() {
            if(Instance != null) return;
            var existing = FindAnyObjectByType<AudioService>();
            if(existing != null) return;
            var go = new GameObject("AudioService");
            go.AddComponent<AudioService>();
        }

        private void Update() {
            // Return finished voices to pools without spawning a coroutine per sound.
            for(var i = _active.Count - 1; i >= 0; i--) {
                var v = _active[i];
                if(v == null || v.Src == null) {
                    _active.RemoveAt(i);
                    continue;
                }

                if(v.Src.isPlaying) continue;
                StopInternal(v, returnToPool: true);
            }
        }

        private void InitializePools() {
            _pools.Clear();
            if(config == null) return;
            if(config.audioSourcePrefab == null) return;

            var parent = transform;
            if(config.buses == null) return;
            foreach(var bc in config.buses) {
                if(_pools.ContainsKey(bc.bus)) continue;
                var q = new Queue<AudioSource>(Mathf.Max(0, bc.prewarmSources));
                _pools.Add(bc.bus, q);

                for(var j = 0; j < bc.prewarmSources; j++) {
                    var src = Instantiate(config.audioSourcePrefab, parent);
                    src.gameObject.SetActive(false);
                    q.Enqueue(src);
                }
            }
        }

        private void PreloadMarkedCues() {
            if(catalog == null || catalog.entries == null) return;

            foreach(var e in catalog.entries) {
                var cue = e.cue;
                if(cue == null) continue;
                if(!cue.preload) continue;
                if(cue.variants == null) continue;

                for(var j = 0; j < cue.variants.Count; j++) {
                    var clip = cue.variants[j].clip;
                    if(clip == null) continue;
                    // Access clip data to encourage Unity to load it; no-op if already loaded.
                    _ = clip.length;
                }
            }
        }

        public void Play(string id, Vector3 worldPosition, uint seed = 0) {
            Play(id, new PlayParams { useWorldPosition = true, worldPosition = worldPosition, seed = seed });
        }

        public void PlayAttached(string id, Transform parent, uint seed = 0) {
            Play(id, new PlayParams { parent = parent, useWorldPosition = false, seed = seed });
        }

        private void Play(string id, PlayParams p) {
            if(string.IsNullOrWhiteSpace(id)) return;
            if(config == null || catalog == null) return;

            if(!catalog.TryGetCue(id, out var cue) || cue == null) {
                // Dev-visible by design; content should be correct. Do not spam logs per frame.
                DevLog.LogWarning($"[AudioService] Missing SoundCue for id '{id}'.", catalog);
                return;
            }

            if(!cue.HasValidVariants()) {
                DevLog.LogWarning($"[AudioService] SoundCue '{cue.name}' has no valid variants (id '{id}').", cue);
                return;
            }

            // Cooldown (per cue)
            if(cue.cooldownSeconds > 0f) {
                var now = Time.unscaledTime;
                if(_lastPlayTime.TryGetValue(id, out var last) && now - last < cue.cooldownSeconds) {
                    EmitDroppedPlayLog(id, "cooldown",
                        $"remaining={cue.cooldownSeconds - (now - last):0.000}s");
                    return;
                }
                _lastPlayTime[id] = now;
            }

            // Voice limit (per cue)
            if(cue.maxInstances > 0) {
                if(_activeById.TryGetValue(id, out var list) && list != null && list.Count >= cue.maxInstances) {
                    if(!TryStealVoice(cue, list)) {
                        EmitDroppedPlayLog(id, "max_instances_cap",
                            $"active={list.Count} cap={cue.maxInstances} policy={cue.stealPolicy}");
                        return;
                    }
                }
            }

            // Global cap
            if(_active.Count >= config.globalMaxVoices) {
                if(!TryStealGlobal()) {
                    EmitDroppedPlayLog(id, "global_voice_cap",
                        $"active={_active.Count} cap={config.globalMaxVoices}");
                    return;
                }
            }

            var src = GetPooledSource(cue.bus);
            if(src == null) {
                EmitDroppedPlayLog(id, "pool_exhausted", $"bus={cue.bus}");
                return;
            }

            if(!ApplyCueToSource(src, cue, id, p.seed)) {
                EmitDroppedPlayLog(id, "apply_cue_failed", $"bus={cue.bus}");
                ReturnToPool(cue.bus, src);
                return;
            }

            if(p.force2D && src != null) {
                src.spatialBlend = 0f;
            }

            // Recover from misconfigured pooled sources and emit diagnostics at low frequency.
            EnsureSourcePlayable(src, id, cue.bus);

            // Parent vs world
            if(p.parent != null && !p.useWorldPosition) {
                Transform tr;
                (tr = src.transform).SetParent(p.parent, false);
                tr.localPosition = Vector3.zero;
            } else {
                Transform tr;
                (tr = src.transform).SetParent(transform, false);
                tr.position = p.worldPosition;
            }

            try {
                src.Play();
            } catch(Exception ex) {
                DevLog.LogWarning(
                    $"[AudioService] Failed to play id='{id}' bus='{cue.bus}' src='{GetSourceDebugId(src)}': {ex.Message}");
                ReturnToPool(cue.bus, src);
                return;
            }

            var voice = new Voice {
                Id = id,
                Cue = cue,
                Src = src,
                StartTime = Time.unscaledTime,
                Priority = cue.priority
            };
            _active.Add(voice);
            if(!_activeById.TryGetValue(id, out var byId)) {
                byId = new List<Voice>();
                _activeById.Add(id, byId);
            }
            byId.Add(voice);
        }

        public void Stop(string id) {
            if(string.IsNullOrWhiteSpace(id)) return;
            if(!_activeById.TryGetValue(id, out var list) || list == null || list.Count == 0) return;

            // Respect cue stop behavior (use the first voice's cue).
            var cue = list[0] != null ? list[0].Cue : null;
            var behavior = cue != null ? cue.stopBehavior : StopBehavior.StopAll;

            switch(behavior) {
                case StopBehavior.NotStoppable:
                    return;
                case StopBehavior.StopLast: {
                    var last = GetNewest(list);
                    if(last == null) return;
                    StopInternal(last, returnToPool: true);
                    return;
                }
                case StopBehavior.StopAll:
                default:
                    break;
            }

            // StopAll
            for(var i = list.Count - 1; i >= 0; i--) {
                var v = list[i];
                if(v == null) continue;
                StopInternal(v, returnToPool: true);
            }
        }

        public void StopAll() {
            for(var i = _active.Count - 1; i >= 0; i--) {
                var v = _active[i];
                if(v == null) continue;
                StopInternal(v, returnToPool: true);
            }
        }

        private bool TryStealVoice(SoundCue cue, List<Voice> list) {
            if(cue.stealPolicy == VoiceStealPolicy.DropNew) return false;
            if(list == null || list.Count == 0) return false;

            Voice best = null;
            foreach(var v in list) {
                if(v == null) continue;
                if(best == null) {
                    best = v;
                    continue;
                }

                // Higher numeric priority is lower importance.
                if(v.Priority > best.Priority) {
                    best = v;
                    continue;
                }

                if(v.Priority == best.Priority && v.StartTime < best.StartTime) {
                    best = v;
                }
            }

            if(best == null) return false;
            StopInternal(best, returnToPool: true);
            return true;
        }

        private bool TryStealGlobal() {
            // Steal the oldest, lowest-importance voice globally.
            Voice best = null;
            foreach(var v in _active) {
                if(v == null) continue;
                if(best == null) {
                    best = v;
                    continue;
                }

                if(v.Priority > best.Priority) {
                    best = v;
                    continue;
                }

                if(v.Priority == best.Priority && v.StartTime < best.StartTime) {
                    best = v;
                }
            }

            if(best == null) return false;
            StopInternal(best, returnToPool: true);
            return true;
        }

        private AudioSource GetPooledSource(SoundBus bus) {
            if(config == null || config.audioSourcePrefab == null) return null;

            if(!_pools.TryGetValue(bus, out var q) || q == null) {
                q = new Queue<AudioSource>();
                _pools[bus] = q;
            }

            if(q.Count > 0) {
                var src = q.Dequeue();
                if(src != null) {
                    src.gameObject.SetActive(true);
                    if(!src.enabled) {
                        src.enabled = true;
                    }

                    return src;
                }
            }

            // Grow pool on demand (bounded if maxPoolSize is set).
            var canGrow = true;
            if(config.TryGetBusConfig(bus, out var bc) && bc.maxPoolSize > 0) {
                // Approximate: current pool + active voices for this bus.
                var activeCount = 0;
                foreach(var v in _active) {
                    if(v == null || v.Src == null) continue;
                    if(v.Cue != null && v.Cue.bus == bus) activeCount++;
                }
                canGrow = q.Count + activeCount < bc.maxPoolSize;
            }

            if(!canGrow) return null;
            var created = Instantiate(config.audioSourcePrefab, transform);
            created.gameObject.SetActive(true);
            created.enabled = true;
            return created;
        }

        private void ReturnToPool(SoundBus bus, AudioSource src) {
            if(src == null) return;
            src.Stop();
            src.clip = null;
            src.transform.SetParent(transform, false);
            src.gameObject.SetActive(false);

            if(!_pools.TryGetValue(bus, out var q) || q == null) {
                q = new Queue<AudioSource>();
                _pools[bus] = q;
            }
            q.Enqueue(src);
        }

        private void StopInternal(Voice v, bool returnToPool) {
            if(v == null) return;

            if(v.Src != null) {
                v.Src.Stop();
            }

            _active.Remove(v);
            if(v.Id != null && _activeById.TryGetValue(v.Id, out var byId) && byId != null) {
                byId.Remove(v);
                if(byId.Count == 0) {
                    _activeById.Remove(v.Id);
                }
            }

            if(!returnToPool || v.Src == null || v.Cue == null) return;
            // Jumppad: destroy source so next play gets a fresh one (reused source often fails to play).
            if(v.Id == "gameplay.jumppad") {
                Destroy(v.Src.gameObject);
            } else {
                ReturnToPool(v.Cue.bus, v.Src);
            }
        }

        private static float DbToLinear(float db) {
            return db <= -80f ? 0f : Mathf.Pow(10f, db / 20f);
        }

        private bool ApplyCueToSource(AudioSource src, SoundCue cue, string id, uint seed) {
            if(src == null || cue == null) return false;
            if(cue.variants == null || cue.variants.Count == 0) return false;

            var picked = PickVariantIndex(cue, seed, out var variant);
            if(picked < 0) return false;
            if(variant.clip == null) return false;

            // Base
            src.clip = variant.clip;
            src.priority = Mathf.Clamp(cue.priority, 0, 256);

            // Spatial
            src.spatialBlend = cue.is3D ? Mathf.Clamp01(cue.spatialBlend) : 0f;
            src.minDistance = Mathf.Max(0f, cue.minDistance);
            src.maxDistance = Mathf.Max(src.minDistance, cue.maxDistance);
            src.rolloffMode = cue.rolloffMode;

            // Routing
            var group = cue.outputGroup;
            if(group == null && config != null && config.TryGetBusConfig(cue.bus, out var bc)) {
                group = bc.outputGroup;
            }
            src.outputAudioMixerGroup = group;

            // Pitch and volume
            var pitch = variant.pitch <= 0f ? 1f : variant.pitch;
            if(variant.randomPitch > 0f) {
                var r = DeterministicRange(seed, id, -variant.randomPitch, variant.randomPitch);
                pitch += r;
            }
            src.pitch = Mathf.Clamp(pitch, 0.1f, 3f);
            src.volume = DbToLinear(variant.volumeDb);

            return true;
        }

        private static Voice GetNewest(List<Voice> list) {
            if(list == null || list.Count == 0) return null;
            Voice best = null;
            foreach(var v in list) {
                if(v == null) continue;
                if(best == null || v.StartTime > best.StartTime) {
                    best = v;
                }
            }
            return best;
        }

        private static int PickVariantIndex(SoundCue cue, uint seed, out SoundCue.Variant v) {
            v = default;
            if(cue == null || cue.variants == null || cue.variants.Count == 0) return -1;

            // Weighted random over variants with clip != null and weight > 0.
            var total = 0f;
            foreach(var vi in cue.variants) {
                if(vi.clip == null) continue;
                if(!(vi.weight > 0f)) continue;
                total += vi.weight;
            }

            if(!(total > 0f)) return -1;

            var t = Deterministic01(seed, cue.name);
            var target = t * total;
            var acc = 0f;
            for(var i = 0; i < cue.variants.Count; i++) {
                var vi = cue.variants[i];
                if(vi.clip == null) continue;
                if(!(vi.weight > 0f)) continue;
                acc += vi.weight;
                if(!(acc >= target)) continue;
                v = vi;
                return i;
            }

            // Fallback: last valid
            for(var i = cue.variants.Count - 1; i >= 0; i--) {
                var vi = cue.variants[i];
                if(vi.clip == null) continue;
                if(!(vi.weight > 0f)) continue;
                v = vi;
                return i;
            }

            return -1;
        }

        private static float Deterministic01(uint seed, string salt) {
            unchecked {
                var h = 2166136261u;
                h = (h ^ seed) * 16777619u;
                if(salt == null) return (h & 0x00FFFFFF) / 16777216f;
                foreach(var t in salt) {
                    h = (h ^ t) * 16777619u;
                }
                // map to [0,1)
                return (h & 0x00FFFFFF) / 16777216f;
            }
        }

        private static float DeterministicRange(uint seed, string salt, float min, float max) {
            var t = Deterministic01(seed, salt);
            return Mathf.Lerp(min, max, t);
        }

        private static string GetSourceDebugId(AudioSource src) {
            return src == null ? "null" : $"{src.gameObject.name}#{src.GetEntityId().GetRawData()}";
        }

        private bool ShouldEmitSourceStateLog(AudioSource src, float intervalSeconds = 10f) {
            if(src == null) return false;
            var key = src.GetEntityId();
            var now = Time.unscaledTime;
            if(_nextSourceStateLogTime.TryGetValue(key, out var next) && now < next) {
                return false;
            }

            _nextSourceStateLogTime[key] = now + intervalSeconds;
            return true;
        }

        private void EnsureSourcePlayable(AudioSource src, string id, SoundBus bus) {
            if(src == null) return;

            if(!src.enabled) {
                src.enabled = true;
                if(ShouldEmitSourceStateLog(src)) {
                    DevLog.LogWarning(
                        $"[AudioService] Re-enabled disabled pooled source for id='{id}' bus='{bus}' src='{GetSourceDebugId(src)}'.");
                }
            }

            if(src.gameObject.activeSelf) return;
            src.gameObject.SetActive(true);
            if(ShouldEmitSourceStateLog(src)) {
                DevLog.LogWarning(
                    $"[AudioService] Reactivated pooled source GameObject for id='{id}' bus='{bus}' src='{GetSourceDebugId(src)}'.");
            }
        }

        private void EmitDroppedPlayLog(string id, string reason, string details, float intervalSeconds = 5f) {
            if(!Debug.isDebugBuild) return;
            if(config == null || !config.enableHopflowAudioDropReasonLogs) return;
            var key = $"{reason}:{id}";
            var now = Time.unscaledTime;
            if(_nextDropReasonLogTime.TryGetValue(key, out var next) && now < next) {
                return;
            }

            _nextDropReasonLogTime[key] = now + intervalSeconds;
            DevLog.LogWarning($"[HOPFLOW][AUDIO] PLAY_DROP reason={reason} id={id} {details}");
        }
    }

    [Serializable]
    public struct PlayParams {
        public Transform parent;
        public bool useWorldPosition;
        public Vector3 worldPosition;
        public uint seed;
        /// <summary>When true, force spatialBlend to 0 so the sound plays as 2D (e.g. for local owner jumppad).</summary>
        public bool force2D;
    }
}
