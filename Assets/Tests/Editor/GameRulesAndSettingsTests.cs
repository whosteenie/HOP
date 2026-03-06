using Game.Match;
using Game.Settings;
using Game.Social;
using NUnit.Framework;
using UnityEngine;

namespace Tests.Editor {
    public class GameRulesAndSettingsTests {
        [TestCase("Team Deathmatch")]
        [TestCase("Hopball")]
        [TestCase("KOTH")]
        [TestCase("CTF")]
        [TestCase("Oddball")]
        public void IsTeamBasedMode_ReturnsTrue_ForTeamModes(string modeId) {
            Assert.That(MatchSettingsManager.IsTeamBasedMode(modeId), Is.True);
        }

        [TestCase("Deathmatch")]
        [TestCase("Gun Tag")]
        [TestCase("Unknown")]
        [TestCase("")]
        [TestCase(null)]
        public void IsTeamBasedMode_ReturnsFalse_ForNonTeamModes(string modeId) {
            Assert.That(MatchSettingsManager.IsTeamBasedMode(modeId), Is.False);
        }

        [Test]
        public void PlayerIconPicker_HideMode_AlwaysReturnsWhite() {
            var iconId = PlayerIconPicker.PickIconIdFromBaseColor(new Vector4(1f, 0f, 0f, 1f), true);
            Assert.That(iconId, Is.EqualTo(PlayerIconPicker.White));
        }

        [Test]
        public void PlayerIconPicker_ColorDistance_PicksClosestCandidate() {
            var iconId = PlayerIconPicker.PickIconIdFromBaseColor(new Vector4(0.23f, 0.61f, 0.98f, 1f), false);
            Assert.That(iconId, Is.EqualTo("blue"));
        }

        [Test]
        public void PlayerIconPicker_DeterministicSeed_IsStable() {
            var iconA = PlayerIconPicker.PickDeterministicIconId(1337ul, false);
            var iconB = PlayerIconPicker.PickDeterministicIconId(1337ul, false);

            Assert.That(iconA, Is.EqualTo(iconB));
        }

        [Test]
        public void SettingsData_Defaults_AreInitialized() {
            var data = new SettingsData();

            Assert.That(data.version, Is.EqualTo(SettingsData.CurrentVersion));
            Assert.That(data.audio, Is.Not.Null);
            Assert.That(data.controls, Is.Not.Null);
            Assert.That(data.video, Is.Not.Null);
            Assert.That(data.social, Is.Not.Null);
            Assert.That(data.player, Is.Not.Null);
            Assert.That(data.player.customization, Is.Not.Null);
            Assert.That(data.keybinds, Is.Not.Null);
        }

        [Test]
        public void SettingsData_EventBusDiagnosticsAlias_RoundTripsToLegacyField() {
            var social = new SettingsData.SocialSettings();

            social.EventBusDiagnosticsEnabled = false;
            Assert.That(social.analyticsEnabled, Is.False);

            social.EventBusDiagnosticsEnabled = true;
            Assert.That(social.analyticsEnabled, Is.True);
        }
    }
}
