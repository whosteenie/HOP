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

        public const int CurrentVersion = 4;

        [Serializable]
        public sealed class AudioSettings {
            // Stored as dB, matching AudioMixer exposed params.
            public float masterVolumeDb;
            public float musicVolumeDb = -20f;
            public float sfxVolumeDb = -8f;
        }

        [Serializable]
        public sealed class ControlsSettings {
            public float sensitivity = 0.1f;
            public bool invertY;
            public bool playerTrails = true;
            public bool holdMantle = true;
            public int grappleIndicator;
            public int crosshairStyle;
            public int crosshairColor;
            public bool autoWallRun;
        }

        [Serializable]
        public sealed class VideoSettings {
            public string mainMenuBackgroundSelection = "Random";
            public int windowMode;
            public string aspectRatio = "";
            public int resolutionWidth;
            public int resolutionHeight;
            public int msaa;
            public float shadowDistance;
            public int shadowResolution;
            public bool bloomEnabled = true;
            public bool motionBlurEnabled = true;
            public bool filmGrainEnabled = true;
            public bool vignetteEnabled = true;
            public bool vsync;
            public int targetFpsIndex = 1;
        }

        [Serializable]
        public sealed class SocialSettings {
            public int voiceInputMode;
            public float voiceVolume = 1f;
            public float voiceInputVolume = 1f;
            public string voiceInputDevice = "Default";
            public bool profanityFilterEnabled;
            public bool streamerModeEnabled;
            // Legacy serialized field name kept for backward compatibility with existing settings files.
            // This currently controls local EventBus failure diagnostics capture/file logging.
            public bool analyticsEnabled = true;
            public bool EventBusDiagnosticsEnabled {
                get => analyticsEnabled;
                set => analyticsEnabled = value;
            }

            public List<string> mutedPlayers = new();
            public List<string> blockedPlayers = new();
        }

        [Serializable]
        public sealed class PlayerSettings {
            public int primaryWeaponIndex;
            public int secondaryWeaponIndex;
            public int tertiaryWeaponIndex;

            public CustomizationSettings customization = new();
        }

        [Serializable]
        public sealed class CustomizationSettings {
            public int materialPacketIndex;
            public Vector4 baseColor = new(1f, 1f, 1f, 1f);
            public float smoothness = 0.5f;
            public float metallic;
            public Vector4 specularColor = new(0.2f, 0.2f, 0.2f, 1f);
            public float heightStrength = 0.02f;

            public bool emissionEnabled;
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

