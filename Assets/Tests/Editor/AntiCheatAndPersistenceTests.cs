using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game.Progression;
using Game.Settings;
using Network.AntiCheat;
using NUnit.Framework;
using UnityEngine;

namespace Tests.Editor {
    public class AntiCheatAndPersistenceTests {
        private static readonly string SettingsPath = Path.Combine(Application.persistentDataPath, "settings.json");
        private static readonly string SettingsBackupPath = Path.Combine(Application.persistentDataPath, "settings.bak.json");
        private static readonly string ProgressionPath = Path.Combine(Application.persistentDataPath, "progression.json");

        [SetUp]
        public void SetUp() {
            Directory.CreateDirectory(Application.persistentDataPath);
            CleanupSettingsArtifacts();
            CleanupProgressionArtifacts();
            ClearRateLimiterCache();
        }

        [TearDown]
        public void TearDown() {
            CleanupSettingsArtifacts();
            CleanupProgressionArtifacts();
            ClearRateLimiterCache();
        }

        [Test]
        public void RpcRateLimiter_BlocksAfterLimitWithinWindow() {
            const ulong clientId = 1001;
            const string key = RpcRateLimiter.Keys.Damage;

            Assert.That(RpcRateLimiter.TryConsume(new RpcRateLimiter.RpcRateLimitRequest {
                ClientId = clientId,
                Key = key,
                MaxCalls = 2,
                WindowSeconds = 10f
            }), Is.True);
            Assert.That(RpcRateLimiter.TryConsume(new RpcRateLimiter.RpcRateLimitRequest {
                ClientId = clientId,
                Key = key,
                MaxCalls = 2,
                WindowSeconds = 10f
            }), Is.True);
            Assert.That(RpcRateLimiter.TryConsume(new RpcRateLimiter.RpcRateLimitRequest {
                ClientId = clientId,
                Key = key,
                MaxCalls = 2,
                WindowSeconds = 10f
            }), Is.False);
        }

        [Test]
        public void RpcRateLimiter_TracksKeysIndependentlyPerClient() {
            const ulong clientId = 1002;

            Assert.That(RpcRateLimiter.TryConsume(new RpcRateLimiter.RpcRateLimitRequest {
                ClientId = clientId,
                Key = RpcRateLimiter.Keys.Damage,
                MaxCalls = 1,
                WindowSeconds = 10f
            }), Is.True);
            Assert.That(RpcRateLimiter.TryConsume(new RpcRateLimiter.RpcRateLimitRequest {
                ClientId = clientId,
                Key = RpcRateLimiter.Keys.Damage,
                MaxCalls = 1,
                WindowSeconds = 10f
            }), Is.False);

            // Different key should still be allowed.
            Assert.That(RpcRateLimiter.TryConsume(new RpcRateLimiter.RpcRateLimitRequest {
                ClientId = clientId,
                Key = RpcRateLimiter.Keys.WorldSfx,
                MaxCalls = 1,
                WindowSeconds = 10f
            }), Is.True);
        }

        [Test]
        public void RpcRateLimiter_AllowsAgainAfterWindowReset() {
            const ulong clientId = 1003;
            const string key = RpcRateLimiter.Keys.WeaponSwitch;

            Assert.That(RpcRateLimiter.TryConsume(new RpcRateLimiter.RpcRateLimitRequest {
                ClientId = clientId,
                Key = key,
                MaxCalls = 1,
                WindowSeconds = 1f
            }), Is.True);
            Assert.That(RpcRateLimiter.TryConsume(new RpcRateLimiter.RpcRateLimitRequest {
                ClientId = clientId,
                Key = key,
                MaxCalls = 1,
                WindowSeconds = 1f
            }), Is.False);

            ForceLimiterWindowExpired(clientId, key, windowSeconds: 1f);

            Assert.That(RpcRateLimiter.TryConsume(new RpcRateLimiter.RpcRateLimitRequest {
                ClientId = clientId,
                Key = key,
                MaxCalls = 1,
                WindowSeconds = 1f
            }), Is.True);
        }

        [Test]
        public void SettingsFile_SaveLoadAndBackup_RoundTrips() {
            var first = new SettingsData();
            first.controls.sensitivity = 1.25f;
            first.video.mainMenuBackgroundSelection = "Arena";

            SettingsFile.Save(first);
            Assert.That(File.Exists(SettingsPath), Is.True);
            Assert.That(File.Exists(SettingsBackupPath), Is.False, "Backup should not exist on first save.");

            var second = new SettingsData();
            second.controls.sensitivity = 2.5f;
            second.video.mainMenuBackgroundSelection = "Random";
            second.social.voiceInputDevice = "Mic A";

            SettingsFile.Save(second);
            Assert.That(File.Exists(SettingsBackupPath), Is.True, "Second save should create/update backup.");

            var loadedOk = SettingsFile.TryLoad(out var loaded);
            Assert.That(loadedOk, Is.True);
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded.controls.sensitivity, Is.EqualTo(2.5f));
            Assert.That(loaded.social.voiceInputDevice, Is.EqualTo("Mic A"));
        }

        [Test]
        public void SettingsFile_QuarantineCorrupt_MovesCurrentSettings() {
            Directory.CreateDirectory(Application.persistentDataPath);
            File.WriteAllText(SettingsPath, "corrupt_payload");

            var existing = new HashSet<string>(Directory.GetFiles(Application.persistentDataPath, "settings.corrupt.*.json"));
            SettingsFile.QuarantineCorrupt();

            Assert.That(File.Exists(SettingsPath), Is.False);
            var updated = new HashSet<string>(Directory.GetFiles(Application.persistentDataPath, "settings.corrupt.*.json"));
            updated.ExceptWith(existing);
            Assert.That(updated.Count, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void ProgressionStore_SaveAndLoad_RoundTrips() {
            var data = new PlayerProgressionData {
                level = 7,
                currentXp = 123,
                totalXp = 456
            };
            data.stats.kills = 9;
            data.stats.deaths = 2;

            ProgressionStore.Save(data);
            Assert.That(File.Exists(ProgressionPath), Is.True);

            var loaded = ProgressionStore.Load();
            Assert.That(loaded.level, Is.EqualTo(7));
            Assert.That(loaded.currentXp, Is.EqualTo(123));
            Assert.That(loaded.totalXp, Is.EqualTo(456));
            Assert.That(loaded.stats.kills, Is.EqualTo(9));
            Assert.That(loaded.stats.deaths, Is.EqualTo(2));
        }

        [Test]
        public void ProgressionStore_TamperedPayload_ResetsAndQuarantines() {
            var data = new PlayerProgressionData {
                level = 4,
                currentXp = 99
            };
            ProgressionStore.Save(data);
            Assert.That(File.Exists(ProgressionPath), Is.True);

            var existing = new HashSet<string>(Directory.GetFiles(Application.persistentDataPath, "progression.corrupt.*.json"));
            File.AppendAllText(ProgressionPath, "tamper");

            var loaded = ProgressionStore.Load();
            Assert.That(loaded.level, Is.EqualTo(1));
            Assert.That(loaded.currentXp, Is.EqualTo(0));

            var updated = new HashSet<string>(Directory.GetFiles(Application.persistentDataPath, "progression.corrupt.*.json"));
            updated.ExceptWith(existing);
            Assert.That(updated.Count, Is.GreaterThanOrEqualTo(1));
            Assert.That(File.Exists(ProgressionPath), Is.False, "Corrupt progression file should be moved out of place.");
        }

        private static void CleanupSettingsArtifacts() {
            TryDelete(SettingsPath);
            TryDelete(SettingsBackupPath);

            if(Directory.Exists(Application.persistentDataPath)) {
                var quarantineFiles = Directory.GetFiles(Application.persistentDataPath, "settings.corrupt.*.json");
                foreach(var file in quarantineFiles) {
                    TryDelete(file);
                }
            }
        }

        private static void CleanupProgressionArtifacts() {
            TryDelete(ProgressionPath);

            if(Directory.Exists(Application.persistentDataPath)) {
                var quarantineFiles = Directory.GetFiles(Application.persistentDataPath, "progression.corrupt.*.json");
                foreach(var file in quarantineFiles) {
                    TryDelete(file);
                }
            }
        }

        private static void TryDelete(string path) {
            if(File.Exists(path)) {
                File.Delete(path);
            }
        }

        private static void ClearRateLimiterCache() {
            var limiterType = typeof(RpcRateLimiter);
            var cacheField = limiterType.GetField("Cache", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(cacheField, Is.Not.Null);

            if(cacheField.GetValue(null) is IDictionary cache) {
                cache.Clear();
            }
        }

        private static void ForceLimiterWindowExpired(ulong clientId, string key, float windowSeconds) {
            var limiterType = typeof(RpcRateLimiter);
            var cacheField = limiterType.GetField("Cache", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(cacheField, Is.Not.Null);

            var cache = cacheField.GetValue(null) as IDictionary;
            Assert.That(cache, Is.Not.Null);
            Assert.That(cache.Contains(clientId), Is.True);

            var bucket = cache[clientId] as IDictionary;
            Assert.That(bucket, Is.Not.Null);
            Assert.That(bucket.Contains(key), Is.True);

            var entry = bucket[key];
            var entryType = entry.GetType();
            var windowStartField = entryType.GetField("WindowStart", BindingFlags.Public | BindingFlags.Instance);
            Assert.That(windowStartField, Is.Not.Null);

            windowStartField.SetValue(entry, Time.unscaledTime - windowSeconds - 0.1f);
        }
    }
}
