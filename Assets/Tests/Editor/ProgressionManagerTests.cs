using System.Collections.Generic;
using System.Reflection;
using Game.Progression;
using NUnit.Framework;
using UnityEngine;

namespace Tests.Editor {
    public class ProgressionManagerTests {
        private GameObject _go;
        private ProgressionManager _manager;
        private FieldInfo _challengePoolField;
        private FieldInfo _baseXpField;
        private FieldInfo _xpMultiplierField;
        private FieldInfo _dataBackingField;
        private FieldInfo _instanceBackingField;
        private MethodInfo _normalizeTargetsMethod;
        private MethodInfo _checkLevelUpMethod;

        [SetUp]
        public void SetUp() {
            _go = new GameObject("ProgressionManagerTests");
            _manager = _go.AddComponent<ProgressionManager>();

            var type = typeof(ProgressionManager);
            _challengePoolField = type.GetField("challengePool", BindingFlags.NonPublic | BindingFlags.Instance);
            _baseXpField = type.GetField("baseXp", BindingFlags.NonPublic | BindingFlags.Instance);
            _xpMultiplierField = type.GetField("xpMultiplier", BindingFlags.NonPublic | BindingFlags.Instance);
            _dataBackingField = type.GetField("<Data>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            _instanceBackingField = type.GetField("<Instance>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static);
            _normalizeTargetsMethod = type.GetMethod("NormalizeChallengeTargets", BindingFlags.NonPublic | BindingFlags.Instance);
            _checkLevelUpMethod = type.GetMethod("CheckLevelUp", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(_challengePoolField, Is.Not.Null);
            Assert.That(_baseXpField, Is.Not.Null);
            Assert.That(_xpMultiplierField, Is.Not.Null);
            Assert.That(_dataBackingField, Is.Not.Null);
            Assert.That(_instanceBackingField, Is.Not.Null);
            Assert.That(_normalizeTargetsMethod, Is.Not.Null);
            Assert.That(_checkLevelUpMethod, Is.Not.Null);
        }

        [TearDown]
        public void TearDown() {
            if(_go != null) {
                Object.DestroyImmediate(_go);
            }

            _instanceBackingField.SetValue(null, null);
        }

        [Test]
        public void NormalizeChallengeTargets_ClampsProgressAndCompletionState() {
            var definition = ScriptableObject.CreateInstance<ChallengeDefinition>();
            definition.id = "kill_test";
            definition.type = ChallengeType.Kill;
            definition.minTarget = 10;
            definition.maxTarget = 20;
            definition.weeklyMinTarget = 30;
            definition.weeklyMaxTarget = 50;

            _challengePoolField.SetValue(_manager, new List<ChallengeDefinition> { definition });

            var challenges = new List<ActiveChallengeData> {
                new() {
                    challengeID = "kill_test",
                    targetProgress = 5, // below min
                    currentProgress = -4, // below 0
                    isCompleted = true // should become false
                },
                new() {
                    challengeID = "kill_test",
                    targetProgress = 999, // above max
                    currentProgress = 999, // above target
                    isCompleted = false // should become true
                }
            };

            var changed = (bool)_normalizeTargetsMethod.Invoke(_manager, new object[] { challenges, false });
            Assert.That(changed, Is.True);

            Assert.That(challenges[0].targetProgress, Is.EqualTo(10));
            Assert.That(challenges[0].currentProgress, Is.EqualTo(0));
            Assert.That(challenges[0].isCompleted, Is.False);

            Assert.That(challenges[1].targetProgress, Is.EqualTo(20));
            Assert.That(challenges[1].currentProgress, Is.EqualTo(20));
            Assert.That(challenges[1].isCompleted, Is.True);

            Object.DestroyImmediate(definition);
        }

        [Test]
        public void CheckLevelUp_HandlesMultipleLevelTransitions() {
            _baseXpField.SetValue(_manager, 100);
            _xpMultiplierField.SetValue(_manager, 2f);
            _dataBackingField.SetValue(_manager, new PlayerProgressionData {
                level = 1,
                currentXp = 350,
                totalXp = 350
            });

            _checkLevelUpMethod.Invoke(_manager, null);

            var data = (PlayerProgressionData)_dataBackingField.GetValue(_manager);
            Assert.That(data.level, Is.EqualTo(3));
            Assert.That(data.currentXp, Is.EqualTo(50));
            Assert.That(data.totalXp, Is.EqualTo(350));
        }

        [Test]
        public void GetXpRequiredForLevel_UsesConfiguredCurve() {
            _baseXpField.SetValue(_manager, 200);
            _xpMultiplierField.SetValue(_manager, 1.5f);

            Assert.That(_manager.GetXpRequiredForLevel(1), Is.EqualTo(200));
            Assert.That(_manager.GetXpRequiredForLevel(2), Is.EqualTo(300));
            Assert.That(_manager.GetXpRequiredForLevel(3), Is.EqualTo(450));
        }
    }
}
