using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Settings {
    [Serializable]
    public sealed class SettingsData {
        public int version = CurrentVersion;

        public AudioSettings audio = new();
        public ControlsSettings controls = new();
        public VideoSettings video = new();
        public SocialSettings social = new();
        public PlayerSettings player = new();
        public KeybindSettings keybinds = new();

        public const int CurrentVersion = 1;

        [Serializable]
        public sealed class AudioSettings {
            // Stored as dB, matching AudioMixer exposed params.
            public float masterVolumeDb = 0f;
            public float musicVolumeDb = -20f;
            public float sfxVolumeDb = -8f;
        }

        [Serializable]
        public sealed class ControlsSettings {
            public float sensitivity = 0.1f;
            public bool invertY = false;
            public bool playerTrails = true;
            public bool holdMantle = true;
            public int grappleIndicator = 0;
        }

        [Serializable]
        public sealed class VideoSettings {
            public int windowMode = 0;
            public string aspectRatio = "";
            public int resolutionWidth = 0;
            public int resolutionHeight = 0;
            public int msaa = 0;
            public float shadowDistance = 0f;
            public int shadowResolution = 0;
            public bool vsync = false;
            public int targetFpsIndex = 1;
        }

        [Serializable]
        public sealed class SocialSettings {
            public int voiceInputMode = 0;
            public float voiceVolume = 1f;
            public float voiceInputVolume = 1f;
            public string voiceInputDevice = "Default";
            public bool profanityFilterEnabled = false;

            public List<string> mutedPlayers = new();
            public List<string> blockedPlayers = new();
        }

        [Serializable]
        public sealed class PlayerSettings {
            public string playerName = "Unknown Player";
            public int primaryWeaponIndex = 0;
            public int secondaryWeaponIndex = 0;
            public int tertiaryWeaponIndex = 0;

            public CustomizationSettings customization = new();
        }

        [Serializable]
        public sealed class CustomizationSettings {
            public int materialPacketIndex = 0;
            public Vector4 baseColor = new(1f, 1f, 1f, 1f);
            public float smoothness = 0.5f;
            public float metallic = 0f;
            public Vector4 specularColor = new(0.2f, 0.2f, 0.2f, 1f);
            public float heightStrength = 0.02f;

            public bool emissionEnabled = false;
            public Vector4 emissionColor = new(0f, 0f, 0f, 1f);
        }

        [Serializable]
        public sealed class KeybindSettings {
            public List<KeybindEntry> entries = new();
        }

        [Serializable]
        public sealed class KeybindEntry {
            public string name = "";
            public string binding0 = "";
            public string binding1 = "";
        }
    }
}

