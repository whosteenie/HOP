using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

namespace Game.Audio2 {
    [CreateAssetMenu(fileName = "AudioConfig", menuName = "AudioService/Audio Config")]
    public sealed class AudioConfig : ScriptableObject {
        [Serializable]
        public struct BusConfig {
            public SoundBus bus;
            public AudioMixerGroup outputGroup;

            [Tooltip("AudioMixer exposed parameter name for this bus volume (dB).")]
            public string mixerVolumeParam;

            [Min(0)]
            public int prewarmSources;

            [Min(0)]
            public int maxPoolSize;
        }

        [Header("Mixer")]
        public AudioMixer mixer;

        [Header("Pooling")]
        [Tooltip("Prefab used when instantiating pooled AudioSources.")]
        public AudioSource audioSourcePrefab;

        [Tooltip("Hard cap on simultaneous active voices across all buses.")]
        [Min(1)]
        public int globalMaxVoices = 64;

        [Header("Bus configs")]
        public List<BusConfig> buses = new();

        private Dictionary<SoundBus, BusConfig> _busLookup;

        public bool TryGetBusConfig(SoundBus bus, out BusConfig cfg) {
            if(_busLookup == null) {
                _busLookup = new Dictionary<SoundBus, BusConfig>();
                if(buses != null) {
                    for(var i = 0; i < buses.Count; i++) {
                        var b = buses[i];
                        if(_busLookup.ContainsKey(b.bus)) continue;
                        _busLookup.Add(b.bus, b);
                    }
                }
            }
            return _busLookup.TryGetValue(bus, out cfg);
        }
    }
}

