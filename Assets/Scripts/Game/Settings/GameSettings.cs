using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Settings {
    public static class GameSettings {
        public static event Action OnSettingsChanged;

        private static SettingsData data;
        private static bool loaded;

        public static SettingsData Data {
            get {
                EnsureLoaded();
                return data;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Preload() {
            EnsureLoaded();
        }

        private static void EnsureLoaded() {
            if(loaded) return;
            loaded = true;

            if(SettingsFile.TryLoad(out var settingsData) && settingsData != null) {
                data = settingsData;
                ValidateAndClamp(data);
                return;
            }

            // If load failed, quarantine any existing file and regenerate.
            SettingsFile.QuarantineCorruptFile();

            data = new SettingsData();
            MigrateFromPlayerPrefsIfPresent(data);
            ValidateAndClamp(data);
            SettingsFile.Save(data);
        }

        public static void Save() {
            EnsureLoaded();
            ValidateAndClamp(data);
            SettingsFile.Save(data);
            OnSettingsChanged?.Invoke();
        }

        private static void ValidateAndClamp(SettingsData d) {
            if(d == null) return;

            if(d.version < SettingsData.CurrentVersion) {
                d.version = SettingsData.CurrentVersion;
            }

            if(d.audio == null) d.audio = new SettingsData.AudioSettings();
            if(d.controls == null) d.controls = new SettingsData.ControlsSettings();
            if(d.video == null) d.video = new SettingsData.VideoSettings();
            if(d.social == null) d.social = new SettingsData.SocialSettings();
            if(d.player == null) d.player = new SettingsData.PlayerSettings();
            if(d.player.customization == null) d.player.customization = new SettingsData.CustomizationSettings();
            if(d.keybinds == null) d.keybinds = new SettingsData.KeybindSettings();
            if(d.keybinds.entries == null) d.keybinds.entries = new List<SettingsData.KeybindEntry>();
            SanitizeKeybindEntries(d.keybinds.entries);

            // Audio (dB). Keep within sane mixer ranges.
            d.audio.masterVolumeDb = Mathf.Clamp(d.audio.masterVolumeDb, -80f, 20f);
            d.audio.musicVolumeDb = Mathf.Clamp(d.audio.musicVolumeDb, -80f, 20f);
            d.audio.sfxVolumeDb = Mathf.Clamp(d.audio.sfxVolumeDb, -80f, 20f);

            // Controls
            d.controls.sensitivity = Mathf.Clamp(d.controls.sensitivity, 0.01f, 5f);
            d.controls.grappleIndicator = Mathf.Clamp(d.controls.grappleIndicator, 0, 2);

            // Social
            d.social.voiceInputMode = Mathf.Clamp(d.social.voiceInputMode, 0, 1);
            d.social.voiceVolume = Mathf.Clamp01(d.social.voiceVolume);
            d.social.voiceInputVolume = Mathf.Clamp01(d.social.voiceInputVolume);
            if(string.IsNullOrWhiteSpace(d.social.voiceInputDevice)) {
                d.social.voiceInputDevice = "Default";
            }

            if(d.social.mutedPlayers == null) d.social.mutedPlayers = new List<string>();
            if(d.social.blockedPlayers == null) d.social.blockedPlayers = new List<string>();
            TrimList(d.social.mutedPlayers, 200);
            TrimList(d.social.blockedPlayers, 200);

            // Player
            d.player.primaryWeaponIndex = Mathf.Max(0, d.player.primaryWeaponIndex);
            d.player.secondaryWeaponIndex = Mathf.Max(0, d.player.secondaryWeaponIndex);
            d.player.tertiaryWeaponIndex = Mathf.Max(0, d.player.tertiaryWeaponIndex);

            // Customization
            d.player.customization.materialPacketIndex = Mathf.Max(0, d.player.customization.materialPacketIndex);
            d.player.customization.smoothness = Mathf.Clamp01(d.player.customization.smoothness);
            d.player.customization.metallic = Mathf.Clamp01(d.player.customization.metallic);
            d.player.customization.heightStrength = Mathf.Clamp(d.player.customization.heightStrength, 0.005f, 0.08f);
        }

        private static void TrimList(List<string> list, int max) {
            if(list == null) return;
            while(list.Count > max) {
                list.RemoveAt(0);
            }
        }

        private static void SanitizeKeybindEntries(List<SettingsData.KeybindEntry> entries) {
            if(entries == null) return;

            var seen = new HashSet<string>();
            for(var i = entries.Count - 1; i >= 0; i--) {
                var e = entries[i];
                if(e == null) {
                    entries.RemoveAt(i);
                    continue;
                }

                if(string.IsNullOrWhiteSpace(e.name)) {
                    entries.RemoveAt(i);
                    continue;
                }

                if(!seen.Add(e.name)) {
                    entries.RemoveAt(i);
                    continue;
                }

                if(e.binding0 == null) e.binding0 = "";
                if(e.binding1 == null) e.binding1 = "";
            }
        }

        private static void MigrateFromPlayerPrefsIfPresent(SettingsData d) {
            if(d == null) return;

            // Audio
            if(PlayerPrefs.HasKey("MasterVolume")) d.audio.masterVolumeDb = PlayerPrefs.GetFloat("MasterVolume", d.audio.masterVolumeDb);
            if(PlayerPrefs.HasKey("MusicVolume")) d.audio.musicVolumeDb = PlayerPrefs.GetFloat("MusicVolume", d.audio.musicVolumeDb);
            if(PlayerPrefs.HasKey("SFXVolume")) d.audio.sfxVolumeDb = PlayerPrefs.GetFloat("SFXVolume", d.audio.sfxVolumeDb);

            // Controls
            if(PlayerPrefs.HasKey("Sensitivity")) {
                d.controls.sensitivity = PlayerPrefs.GetFloat("Sensitivity", d.controls.sensitivity);
            } else if(PlayerPrefs.HasKey("SensitivityX")) {
                d.controls.sensitivity = PlayerPrefs.GetFloat("SensitivityX", d.controls.sensitivity);
            }
            if(PlayerPrefs.HasKey("InvertY")) d.controls.invertY = PlayerPrefs.GetInt("InvertY", d.controls.invertY ? 1 : 0) == 1;
            if(PlayerPrefs.HasKey("PlayerTrails")) d.controls.playerTrails = PlayerPrefs.GetInt("PlayerTrails", d.controls.playerTrails ? 1 : 0) == 1;
            if(PlayerPrefs.HasKey("HoldMantle")) d.controls.holdMantle = PlayerPrefs.GetInt("HoldMantle", d.controls.holdMantle ? 1 : 0) == 1;
            if(PlayerPrefs.HasKey("AutoWallRun")) d.controls.autoWallRun = PlayerPrefs.GetInt("AutoWallRun", d.controls.autoWallRun ? 1 : 0) == 1;
            if(PlayerPrefs.HasKey("GrappleIndicator")) d.controls.grappleIndicator = PlayerPrefs.GetInt("GrappleIndicator", d.controls.grappleIndicator);

            // Video (best effort)
            if(PlayerPrefs.HasKey("WindowMode")) d.video.windowMode = PlayerPrefs.GetInt("WindowMode", d.video.windowMode);
            if(PlayerPrefs.HasKey("AspectRatio")) d.video.aspectRatio = PlayerPrefs.GetString("AspectRatio", d.video.aspectRatio);
            if(PlayerPrefs.HasKey("ResolutionWidth")) d.video.resolutionWidth = PlayerPrefs.GetInt("ResolutionWidth", d.video.resolutionWidth);
            if(PlayerPrefs.HasKey("ResolutionHeight")) d.video.resolutionHeight = PlayerPrefs.GetInt("ResolutionHeight", d.video.resolutionHeight);
            if(PlayerPrefs.HasKey("MSAA")) d.video.msaa = PlayerPrefs.GetInt("MSAA", d.video.msaa);
            if(PlayerPrefs.HasKey("ShadowDistance")) d.video.shadowDistance = PlayerPrefs.GetFloat("ShadowDistance", d.video.shadowDistance);
            if(PlayerPrefs.HasKey("ShadowResolution")) d.video.shadowResolution = PlayerPrefs.GetInt("ShadowResolution", d.video.shadowResolution);
            if(PlayerPrefs.HasKey("VSync")) d.video.vsync = PlayerPrefs.GetInt("VSync", d.video.vsync ? 1 : 0) == 1;
            if(PlayerPrefs.HasKey("TargetFPS")) d.video.targetFpsIndex = PlayerPrefs.GetInt("TargetFPS", d.video.targetFpsIndex);

            // Player
            if(PlayerPrefs.HasKey("PrimaryWeaponIndex")) d.player.primaryWeaponIndex = PlayerPrefs.GetInt("PrimaryWeaponIndex", d.player.primaryWeaponIndex);
            if(PlayerPrefs.HasKey("SecondaryWeaponIndex")) d.player.secondaryWeaponIndex = PlayerPrefs.GetInt("SecondaryWeaponIndex", d.player.secondaryWeaponIndex);
            if(PlayerPrefs.HasKey("TertiaryWeaponIndex")) d.player.tertiaryWeaponIndex = PlayerPrefs.GetInt("TertiaryWeaponIndex", d.player.tertiaryWeaponIndex);

            // Customization (best effort)
            if(PlayerPrefs.HasKey("PlayerMaterialPacketIndex")) d.player.customization.materialPacketIndex = PlayerPrefs.GetInt("PlayerMaterialPacketIndex", d.player.customization.materialPacketIndex);

            if(PlayerPrefs.HasKey("PlayerBaseColorR") ||
               PlayerPrefs.HasKey("PlayerBaseColorG") ||
               PlayerPrefs.HasKey("PlayerBaseColorB") ||
               PlayerPrefs.HasKey("PlayerBaseColorA")) {
                var r = PlayerPrefs.GetFloat("PlayerBaseColorR", d.player.customization.baseColor.x);
                var g = PlayerPrefs.GetFloat("PlayerBaseColorG", d.player.customization.baseColor.y);
                var b = PlayerPrefs.GetFloat("PlayerBaseColorB", d.player.customization.baseColor.z);
                var a = PlayerPrefs.GetFloat("PlayerBaseColorA", d.player.customization.baseColor.w);
                d.player.customization.baseColor = new Vector4(r, g, b, a);
            }

            if(PlayerPrefs.HasKey("PlayerSmoothness")) d.player.customization.smoothness = PlayerPrefs.GetFloat("PlayerSmoothness", d.player.customization.smoothness);
            if(PlayerPrefs.HasKey("PlayerMetallic")) d.player.customization.metallic = PlayerPrefs.GetFloat("PlayerMetallic", d.player.customization.metallic);

            if(PlayerPrefs.HasKey("PlayerSpecularColorR") ||
               PlayerPrefs.HasKey("PlayerSpecularColorG") ||
               PlayerPrefs.HasKey("PlayerSpecularColorB") ||
               PlayerPrefs.HasKey("PlayerSpecularColorA")) {
                var r = PlayerPrefs.GetFloat("PlayerSpecularColorR", d.player.customization.specularColor.x);
                var g = PlayerPrefs.GetFloat("PlayerSpecularColorG", d.player.customization.specularColor.y);
                var b = PlayerPrefs.GetFloat("PlayerSpecularColorB", d.player.customization.specularColor.z);
                var a = PlayerPrefs.GetFloat("PlayerSpecularColorA", d.player.customization.specularColor.w);
                d.player.customization.specularColor = new Vector4(r, g, b, a);
            }

            if(PlayerPrefs.HasKey("PlayerHeightStrength")) d.player.customization.heightStrength = PlayerPrefs.GetFloat("PlayerHeightStrength", d.player.customization.heightStrength);
            if(PlayerPrefs.HasKey("PlayerEmissionEnabled")) d.player.customization.emissionEnabled = PlayerPrefs.GetInt("PlayerEmissionEnabled", 0) == 1;

            if(PlayerPrefs.HasKey("PlayerEmissionColorR") ||
               PlayerPrefs.HasKey("PlayerEmissionColorG") ||
               PlayerPrefs.HasKey("PlayerEmissionColorB") ||
               PlayerPrefs.HasKey("PlayerEmissionColorA")) {
                var r = PlayerPrefs.GetFloat("PlayerEmissionColorR", d.player.customization.emissionColor.x);
                var g = PlayerPrefs.GetFloat("PlayerEmissionColorG", d.player.customization.emissionColor.y);
                var b = PlayerPrefs.GetFloat("PlayerEmissionColorB", d.player.customization.emissionColor.z);
                var a = PlayerPrefs.GetFloat("PlayerEmissionColorA", d.player.customization.emissionColor.w);
                d.player.customization.emissionColor = new Vector4(r, g, b, a);
            }

            // Social
            if(PlayerPrefs.HasKey("Social_VoiceInputMode")) d.social.voiceInputMode = PlayerPrefs.GetInt("Social_VoiceInputMode", d.social.voiceInputMode);
            if(PlayerPrefs.HasKey("Social_VoiceVolume")) d.social.voiceVolume = PlayerPrefs.GetFloat("Social_VoiceVolume", d.social.voiceVolume);
            if(PlayerPrefs.HasKey("Social_VoiceInputVolume")) d.social.voiceInputVolume = PlayerPrefs.GetFloat("Social_VoiceInputVolume", d.social.voiceInputVolume);
            if(PlayerPrefs.HasKey("Social_VoiceInputDevice")) d.social.voiceInputDevice = PlayerPrefs.GetString("Social_VoiceInputDevice", d.social.voiceInputDevice);
            if(PlayerPrefs.HasKey("Social_ProfanityFilter")) d.social.profanityFilterEnabled = PlayerPrefs.GetInt("Social_ProfanityFilter", 0) == 1;

            // Social lists (legacy CSV)
            if(PlayerPrefs.HasKey("Social_MutedPlayers")) {
                var csv = PlayerPrefs.GetString("Social_MutedPlayers", "");
                if(!string.IsNullOrWhiteSpace(csv)) {
                    var parts = csv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach(var t in parts) {
                        var id = t.Trim();
                        if(string.IsNullOrWhiteSpace(id)) continue;
                        d.social.mutedPlayers.Add(id);
                    }
                }
            }

            if(PlayerPrefs.HasKey("Social_BlockedPlayers")) {
                var csv = PlayerPrefs.GetString("Social_BlockedPlayers", "");
                if(!string.IsNullOrWhiteSpace(csv)) {
                    var parts = csv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach(var t in parts) {
                        var id = t.Trim();
                        if(string.IsNullOrWhiteSpace(id)) continue;
                        d.social.blockedPlayers.Add(id);
                    }
                }
            }

            // Keybinds (legacy PlayerPrefs keys: Keybind_{name}_{index})
            if(d.keybinds.entries == null) d.keybinds.entries = new List<SettingsData.KeybindEntry>();
            var keybindNames = new[] {
                "forward", "back", "left", "right",
                "jump", "interact", "shoot", "ads",
                "reload", "grapple", "primary", "secondary",
                "nextweapon", "previousweapon", "ptt"
            };

            foreach(var name in keybindNames) {
                var key0 = $"Keybind_{name}_0";
                var key1 = $"Keybind_{name}_1";

                if(!PlayerPrefs.HasKey(key0) && !PlayerPrefs.HasKey(key1)) continue;

                var e = new SettingsData.KeybindEntry {
                    name = name,
                    binding0 = PlayerPrefs.GetString(key0, ""),
                    binding1 = PlayerPrefs.GetString(key1, "")
                };
                d.keybinds.entries.Add(e);
            }
        }
    }
}

