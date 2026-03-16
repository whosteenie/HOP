using System;
using System.Collections.Generic;
using Game.Audio.System;
using UnityEngine;
using UnityEngine.Audio;

namespace Game.Audio.Authoring {
    [CreateAssetMenu(fileName = "SoundCue", menuName = "AudioService/Sound Cue")]
    public sealed class SoundCue : ScriptableObject {
        [Serializable]
        public struct Variant {
            public AudioClip clip;

            [Tooltip("Weight used for weighted random selection. Must be > 0 to be selectable.")]
            [Min(0f)]
            public float weight;

            [Header("Per-variant tuning")]
            [Tooltip("Per-variant gain in dB. 0 = unchanged, -6 = half power, +6 = double power.")]
            public float volumeDb;

            [Range(0.1f, 3f)]
            public float pitch;

            [Tooltip("Adds random pitch variation +/- this value.")]
            [Range(0f, 0.5f)]
            public float randomPitch;
        }

        [Header("Identity")]
        [Tooltip("Stable authoring ID (e.g. \"ui.click\", \"weapon.rifle.shoot\"). Must be unique within a SoundCatalog.")]
        public string id;

        [Header("Variants")]
        public List<Variant> variants = new();

        [Header("Mixer routing")]
        public SoundBus bus = SoundBus.Sfx;

        [Tooltip("Optional explicit mixer group. If unset, AudioService uses the bus mapping from AudioConfig.")]
        public AudioMixerGroup outputGroup;

        [Header("Spatial")]
        public bool is3D = true;

        [Range(0f, 1f)]
        public float spatialBlend = 1f;

        [Min(0f)]
        public float minDistance = 1f;

        [Min(0f)]
        public float maxDistance = 50f;

        public AudioRolloffMode rolloffMode = AudioRolloffMode.Logarithmic;

        [Header("Priority & limits")]
        [Range(0, 256)]
        public int priority = 128;

        [Min(0)]
        public int maxInstances = 4;

        [Min(0f)]
        public float cooldownSeconds;

        public VoiceStealPolicy stealPolicy = VoiceStealPolicy.StealLowestPriorityThenOldest;

        [Header("Stop behavior")]
        public StopBehavior stopBehavior = StopBehavior.StopLast;

        [Header("Loading")]
        public bool preload;

        public bool HasValidVariants() {
            if(variants == null || variants.Count == 0) return false;
            foreach(var v in variants) {
                if(v.clip == null) continue;
                if(v.weight <= 0f) continue;
                return true;
            }
            return false;
        }
    }
}

