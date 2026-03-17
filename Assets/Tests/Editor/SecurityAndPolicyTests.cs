using System;
using System.Reflection;
using Events;
using Game.Settings;
using NUnit.Framework;

namespace Tests.Editor {
    public class SecurityAndPolicyTests {
        private sealed class ContextProbeEvent : GameEvent { }

        [Test]
        public void SecureJsonFile_EncodeDecode_RoundTrips() {
            const string logicalPath = "tests/settings.json";
            const string json = "{\"value\":42,\"name\":\"hop\"}";

            var encoded = SecureJsonFile.Encode(logicalPath, json);
            var result = SecureJsonFile.TryDecode(logicalPath, encoded, out var decoded);

            Assert.That(result, Is.EqualTo(SecureJsonFile.DecodeResult.Success));
            Assert.That(decoded, Is.EqualTo(json));
        }

        [Test]
        public void SecureJsonFile_TamperedPayload_IsRejected() {
            const string logicalPath = "tests/progression.json";
            const string json = "{\"xp\":1234}";

            var encoded = SecureJsonFile.Encode(logicalPath, json);
            var tampered = encoded + "x";

            var result = SecureJsonFile.TryDecode(logicalPath, tampered, out _);
            Assert.That(result, Is.EqualTo(SecureJsonFile.DecodeResult.InvalidOrTampered));
        }

        [Test]
        public void SecureJsonFile_LegacyPlaintext_ReturnsAsPlaintext() {
            const string logicalPath = "tests/legacy.json";
            const string plaintext = "{\"legacy\":true}";

            var result = SecureJsonFile.TryDecode(logicalPath, plaintext, out var decoded);

            Assert.That(result, Is.EqualTo(SecureJsonFile.DecodeResult.LegacyPlaintext));
            Assert.That(decoded, Is.EqualTo(plaintext));
        }

        [Test]
        public void MatchmakerPollingPolicy_ResolveTicketPollDelay_ClampsAndBacksOff() {
            var policyType = GetPolicyType();
            var method = policyType.GetMethod("ResolveTicketPollDelayMs", BindingFlags.Public | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);

            var forNegative = InvokeStaticInt(method, -3);
            var forOneFailure = InvokeStaticInt(method, 1);
            var forManyFailures = InvokeStaticInt(method, 99);

            Assert.That(forNegative, Is.EqualTo(1200));
            Assert.That(forOneFailure, Is.EqualTo(2200));
            Assert.That(forManyFailures, Is.EqualTo(6000));
        }

        [Test]
        public void MatchmakerPollingPolicy_DiscoveryDelay_IsConstant() {
            var policyType = GetPolicyType();
            var method = policyType.GetMethod("ResolveMatchLobbyDiscoveryDelayMs", BindingFlags.Public | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);

            var first = InvokeStaticInt(method, 0);
            var later = InvokeStaticInt(method, 17);

            Assert.That(first, Is.EqualTo(1000));
            Assert.That(later, Is.EqualTo(1000));
        }

        [Test]
        public void EventBusContextValues_CapsEntriesAndValueLength() {
            var evt = new ContextProbeEvent();

            for(var i = 0; i < 30; i++) {
                evt.SetContext($"k{i}", new string('x', 300));
            }

            var entriesField = typeof(EventBusContextValues).GetField("entries", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(entriesField, Is.Not.Null);

            var entries = entriesField.GetValue(evt.ContextValues) as System.Collections.IList;
            Assert.That(entries, Is.Not.Null);
            Assert.That(entries.Count, Is.EqualTo(24));

            var firstEntry = entries[0];
            var valueField = firstEntry.GetType().GetField("value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(valueField, Is.Not.Null);
            var storedValue = valueField.GetValue(firstEntry) as string;
            Assert.That(storedValue, Is.Not.Null);
            Assert.That(storedValue.Length, Is.EqualTo(160));
        }

        private static Type GetPolicyType() {
            var policyType = Type.GetType("Network.Session.MatchmakerPollingPolicy, Assembly-CSharp");
            Assert.That(policyType, Is.Not.Null, "MatchmakerPollingPolicy type should exist in Assembly-CSharp.");
            return policyType;
        }

        private static int InvokeStaticInt(MethodInfo method, int arg) {
            var result = method.Invoke(null, new object[] { arg });
            Assert.That(result, Is.TypeOf<int>());
            return (int)result;
        }
    }
}
