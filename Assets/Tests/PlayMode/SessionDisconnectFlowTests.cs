using System.Collections;
using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Tests.PlayMode {
    public class SessionDisconnectFlowTests {
        private GameObject _networkManagerObject;
        private GameObject _sessionObject;
        private Component _sessionManagerComponent;
        private List<PlayModeTestUtils.BehaviourToggleState> _mutedBackgroundBehaviours;

        [SetUp]
        public void SetUp() {
            _mutedBackgroundBehaviours = PlayModeTestUtils.MuteSceneBehaviours(
                "Game.Menu.MainMenuSessionManager, Assembly-CSharp");
        }

        [TearDown]
        public void TearDown() {
            PlayModeTestUtils.DestroyImmediateIfExists(ref _networkManagerObject);
            PlayModeTestUtils.DestroyImmediateIfExists(ref _sessionObject);
            _sessionManagerComponent = null;
            PlayModeTestUtils.RestoreBehaviours(_mutedBackgroundBehaviours);
            _mutedBackgroundBehaviours = null;
        }

        [UnityTest]
        public IEnumerator UnexpectedDisconnect_TransitionsBackToMainMenu() {
            var testScene = SceneManager.CreateScene("UnexpectedDisconnectTestScene");
            Assert.That(SceneManager.SetActiveScene(testScene), Is.True, "Failed to activate temporary gameplay scene.");
            Assert.That(SceneManager.GetActiveScene().name, Is.EqualTo("UnexpectedDisconnectTestScene"));

            _ = CreateSessionManagerForTest("SessionManagerTest");
            PlayModeTestUtils.InvokePrivate(_sessionManagerComponent, "TriggerUnexpectedDisconnectFlow", "PlayModeTest");

            var timeoutAt = Time.realtimeSinceStartup + 20f;
            while(Time.realtimeSinceStartup < timeoutAt) {
                if(SceneManager.GetActiveScene().name == "MainMenu") {
                    break;
                }
                yield return null;
            }

            Assert.That(SceneManager.GetActiveScene().name, Is.EqualTo("MainMenu"),
                "Unexpected disconnect flow should return client to MainMenu.");
        }

        [UnityTest]
        public IEnumerator OnClientStopped_ExpectedDisconnect_DoesNotTriggerUnexpectedFlow() {
            var testScene = SceneManager.CreateScene("ExpectedDisconnectGuardScene");
            Assert.That(SceneManager.SetActiveScene(testScene), Is.True);
            Assert.That(SceneManager.GetActiveScene().name, Is.EqualTo("ExpectedDisconnectGuardScene"));

            _ = CreateSessionManagerForTest("SessionManagerExpectedDisconnectGuard");
            PlayModeTestUtils.SetAutoPropertyBackingField(_sessionManagerComponent, "IsExpectedDisconnect", true);

            PlayModeTestUtils.InvokePrivate(_sessionManagerComponent, "OnClientStopped", false);
            yield return null;
            yield return null;

            Assert.That(SceneManager.GetActiveScene().name, Is.EqualTo("ExpectedDisconnectGuardScene"),
                "Expected disconnect should not trigger unexpected disconnect flow.");

            Assert.That(PlayModeTestUtils.GetPrivateField<bool>(_sessionManagerComponent, "_unexpectedDisconnectInFlight"), Is.False,
                "Unexpected disconnect flow should remain idle when disconnect was expected.");
        }

        [UnityTest]
        public IEnumerator OnClientStopped_UnexpectedDisconnect_TransitionsBackToMainMenu() {
            var testScene = SceneManager.CreateScene("UnexpectedDisconnectViaClientStopped");
            Assert.That(SceneManager.SetActiveScene(testScene), Is.True);
            Assert.That(SceneManager.GetActiveScene().name, Is.EqualTo("UnexpectedDisconnectViaClientStopped"));

            _ = CreateSessionManagerForTest("SessionManagerUnexpectedClientStopped");
            PlayModeTestUtils.SetAutoPropertyBackingField(_sessionManagerComponent, "IsExpectedDisconnect", false);

            PlayModeTestUtils.InvokePrivate(_sessionManagerComponent, "OnClientStopped", false);

            var timeoutAt = Time.realtimeSinceStartup + 20f;
            while(Time.realtimeSinceStartup < timeoutAt) {
                if(SceneManager.GetActiveScene().name == "MainMenu") {
                    break;
                }
                yield return null;
            }

            Assert.That(SceneManager.GetActiveScene().name, Is.EqualTo("MainMenu"),
                "Unexpected OnClientStopped path should return client to MainMenu.");

            timeoutAt = Time.realtimeSinceStartup + 10f;
            while(Time.realtimeSinceStartup < timeoutAt &&
                  PlayModeTestUtils.GetPrivateField<bool>(_sessionManagerComponent, "_unexpectedDisconnectInFlight")) {
                yield return null;
            }

            Assert.That(PlayModeTestUtils.GetPrivateField<bool>(_sessionManagerComponent, "_unexpectedDisconnectInFlight"), Is.False,
                "Unexpected disconnect flow should complete and clear in-flight guard.");
        }

        private Type CreateSessionManagerForTest(string objectName) {
            _networkManagerObject = new GameObject("NetworkManagerTest");
            var networkManager = _networkManagerObject.AddComponent<NetworkManager>();
            Assert.That(networkManager, Is.Not.Null, "Failed to create test NetworkManager.");
            UnityEngine.Object.DontDestroyOnLoad(_networkManagerObject);

            _sessionObject = new GameObject(objectName);
            var sessionManagerType = PlayModeTestUtils.ResolveTypeOrAssert("Network.Session.SessionManager, Assembly-CSharp");

            _sessionManagerComponent = _sessionObject.AddComponent(sessionManagerType);
            Assert.That(_sessionManagerComponent, Is.Not.Null, "Failed to add SessionManager test component.");
            UnityEngine.Object.DontDestroyOnLoad(_sessionObject);

            // Avoid Start() bootstrap side effects; these tests only validate disconnect/session flow.
            ((Behaviour)_sessionManagerComponent).enabled = false;
            return sessionManagerType;
        }
    }
}
