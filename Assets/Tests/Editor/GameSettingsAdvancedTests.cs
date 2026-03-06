using System.Collections.Generic;
using System.Reflection;
using Game.Settings;
using NUnit.Framework;
using UnityEngine;

namespace Tests.Editor {
    public class GameSettingsAdvancedTests {
        private static MethodInfo _validateAndClampMethod;
        private static MethodInfo _migrateFromPlayerPrefsMethod;

        [OneTimeSetUp]
        public void OneTimeSetUp() {
            var gameSettingsType = typeof(GameSettings);
            _validateAndClampMethod = gameSettingsType.GetMethod("ValidateAndClamp", BindingFlags.NonPublic | BindingFlags.Static);
            _migrateFromPlayerPrefsMethod = gameSettingsType.GetMethod("MigrateFromPlayerPrefsIfPresent", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.That(_validateAndClampMethod, Is.Not.Null);
            Assert.That(_migrateFromPlayerPrefsMethod, Is.Not.Null);
        }

        [SetUp]
        public void SetUp() {
            PlayerPrefs.DeleteAll();
        }

        [TearDown]
        public void TearDown() {
            PlayerPrefs.DeleteAll();
        }

        [Test]
        public void ValidateAndClamp_RehydratesAndClampsCriticalValues() {
            var data = new SettingsData {
                version = 2,
                audio = new SettingsData.AudioSettings {
                    masterVolumeDb = 100f,
                    musicVolumeDb = -100f,
                    sfxVolumeDb = 999f
                },
                controls = new SettingsData.ControlsSettings {
                    sensitivity = 100f,
                    grappleIndicator = 99,
                    crosshairStyle = 99,
                    crosshairColor = -9
                },
                video = new SettingsData.VideoSettings {
                    mainMenuBackgroundSelection = " "
                },
                social = new SettingsData.SocialSettings {
                    voiceInputMode = 9,
                    voiceVolume = 9f,
                    voiceInputVolume = -2f,
                    voiceInputDevice = " ",
                    mutedPlayers = new List<string>(),
                    blockedPlayers = new List<string>()
                },
                player = new SettingsData.PlayerSettings {
                    primaryWeaponIndex = -2,
                    secondaryWeaponIndex = -10,
                    tertiaryWeaponIndex = -1,
                    customization = new SettingsData.CustomizationSettings {
                        materialPacketIndex = -4,
                        smoothness = 7f,
                        metallic = -8f,
                        heightStrength = 100f
                    }
                },
                keybinds = new SettingsData.KeybindSettings {
                    entries = new List<SettingsData.KeybindEntry> {
                        null,
                        new() { name = "jump", binding0 = null, binding1 = null },
                        new() { name = "jump", binding0 = "<Keyboard>/j", binding1 = "" },
                        new() { name = " ", binding0 = "<Keyboard>/space", binding1 = "" }
                    }
                }
            };

            for(var i = 0; i < 205; i++) {
                data.social.mutedPlayers.Add($"m{i}");
                data.social.blockedPlayers.Add($"b{i}");
            }

            _validateAndClampMethod.Invoke(null, new object[] { data });

            Assert.That(data.audio.masterVolumeDb, Is.EqualTo(20f));
            Assert.That(data.audio.musicVolumeDb, Is.EqualTo(-80f));
            Assert.That(data.audio.sfxVolumeDb, Is.EqualTo(20f));

            Assert.That(data.controls.sensitivity, Is.EqualTo(5f));
            Assert.That(data.controls.grappleIndicator, Is.EqualTo(2));
            Assert.That(data.controls.crosshairStyle, Is.EqualTo(1));
            Assert.That(data.controls.crosshairColor, Is.EqualTo(0));

            Assert.That(data.video.mainMenuBackgroundSelection, Is.EqualTo("Random"));
            Assert.That(data.social.voiceInputMode, Is.EqualTo(1));
            Assert.That(data.social.voiceVolume, Is.EqualTo(1f));
            Assert.That(data.social.voiceInputVolume, Is.EqualTo(0f));
            Assert.That(data.social.voiceInputDevice, Is.EqualTo("Default"));

            Assert.That(data.social.mutedPlayers.Count, Is.EqualTo(200));
            Assert.That(data.social.blockedPlayers.Count, Is.EqualTo(200));
            Assert.That(data.social.mutedPlayers[0], Is.EqualTo("m5"));
            Assert.That(data.social.blockedPlayers[0], Is.EqualTo("b5"));

            Assert.That(data.player.primaryWeaponIndex, Is.EqualTo(0));
            Assert.That(data.player.secondaryWeaponIndex, Is.EqualTo(0));
            Assert.That(data.player.tertiaryWeaponIndex, Is.EqualTo(0));
            Assert.That(data.player.customization.materialPacketIndex, Is.EqualTo(0));
            Assert.That(data.player.customization.smoothness, Is.EqualTo(1f));
            Assert.That(data.player.customization.metallic, Is.EqualTo(0f));
            Assert.That(data.player.customization.heightStrength, Is.EqualTo(0.08f));

            Assert.That(data.keybinds.entries.Count, Is.EqualTo(1));
            Assert.That(data.keybinds.entries[0].name, Is.EqualTo("jump"));
            Assert.That(data.keybinds.entries[0].binding0, Is.EqualTo("<Keyboard>/j"));
            Assert.That(data.keybinds.entries[0].binding1, Is.EqualTo(string.Empty));

            Assert.That(data.social.EventBusDiagnosticsEnabled, Is.True);
            Assert.That(data.version, Is.EqualTo(SettingsData.CurrentVersion));
        }

        [Test]
        public void MigrateFromPlayerPrefs_UsesLegacyKeysIncludingFallbackSensitivity() {
            PlayerPrefs.SetFloat("MasterVolume", -12f);
            PlayerPrefs.SetFloat("MusicVolume", -15f);
            PlayerPrefs.SetFloat("SFXVolume", -3f);
            PlayerPrefs.SetFloat("SensitivityX", 1.75f);
            PlayerPrefs.SetInt("CrosshairColor", 2);
            PlayerPrefs.SetString("Social_MutedPlayers", " 123 ,456,, 789 ");
            PlayerPrefs.SetString("Social_BlockedPlayers", "abc, def");
            PlayerPrefs.SetString("Keybind_forward_0", "<Keyboard>/w");
            PlayerPrefs.SetString("Keybind_forward_1", "<Keyboard>/upArrow");
            PlayerPrefs.Save();

            var data = new SettingsData();
            _migrateFromPlayerPrefsMethod.Invoke(null, new object[] { data });

            Assert.That(data.audio.masterVolumeDb, Is.EqualTo(-12f));
            Assert.That(data.audio.musicVolumeDb, Is.EqualTo(-15f));
            Assert.That(data.audio.sfxVolumeDb, Is.EqualTo(-3f));
            Assert.That(data.controls.sensitivity, Is.EqualTo(1.75f));
            Assert.That(data.controls.crosshairColor, Is.EqualTo(2));

            CollectionAssert.AreEqual(new[] { "123", "456", "789" }, data.social.mutedPlayers);
            CollectionAssert.AreEqual(new[] { "abc", "def" }, data.social.blockedPlayers);

            Assert.That(data.keybinds.entries.Count, Is.EqualTo(1));
            Assert.That(data.keybinds.entries[0].name, Is.EqualTo("forward"));
            Assert.That(data.keybinds.entries[0].binding0, Is.EqualTo("<Keyboard>/w"));
            Assert.That(data.keybinds.entries[0].binding1, Is.EqualTo("<Keyboard>/upArrow"));
        }
    }
}
