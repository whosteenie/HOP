using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
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
            var sceneFlowService = GetSceneFlowService();
            Assert.That(sceneFlowService, Is.Not.Null, "SessionManager should have a SceneFlowService.");
            InvokeTriggerUnexpectedDisconnectFlow(sceneFlowService, "PlayModeTest");

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
            var networkManager = _networkManagerObject.GetComponent<NetworkManager>();
            PlayModeTestUtils.SetAutoPropertyBackingField(_sessionManagerComponent, "IsExpectedDisconnect", true);

            RunOnClientStoppedLogic(_sessionManagerComponent, networkManager, null);
            yield return null;
            yield return null;

            Assert.That(SceneManager.GetActiveScene().name, Is.EqualTo("ExpectedDisconnectGuardScene"),
                "Expected disconnect should not trigger unexpected disconnect flow.");

            var sceneFlowService = GetSceneFlowService();
            Assert.That(GetUnexpectedDisconnectInFlight(sceneFlowService), Is.False,
                "Unexpected disconnect flow should remain idle when disconnect was expected.");
        }

        [UnityTest]
        public IEnumerator OnClientStopped_UnexpectedDisconnect_TransitionsBackToMainMenu() {
            var testScene = SceneManager.CreateScene("UnexpectedDisconnectViaClientStopped");
            Assert.That(SceneManager.SetActiveScene(testScene), Is.True);
            Assert.That(SceneManager.GetActiveScene().name, Is.EqualTo("UnexpectedDisconnectViaClientStopped"));

            _ = CreateSessionManagerForTest("SessionManagerUnexpectedClientStopped");
            var networkManager = _networkManagerObject.GetComponent<NetworkManager>();
            var sceneFlowService = GetSceneFlowService();
            Assert.That(sceneFlowService, Is.Not.Null);
            PlayModeTestUtils.SetAutoPropertyBackingField(_sessionManagerComponent, "IsExpectedDisconnect", false);

            RunOnClientStoppedLogic(_sessionManagerComponent, networkManager, sceneFlowService);

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
            while(Time.realtimeSinceStartup < timeoutAt && GetUnexpectedDisconnectInFlight(sceneFlowService)) {
                yield return null;
            }

            Assert.That(GetUnexpectedDisconnectInFlight(sceneFlowService), Is.False,
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

        private object GetSceneFlowService() =>
            PlayModeTestUtils.GetPrivateField<object>(_sessionManagerComponent, "_sceneFlow");

        private void InvokeTriggerUnexpectedDisconnectFlow(object sceneFlowService, string source) {
            var method = sceneFlowService.GetType().GetMethod(
                "TriggerUnexpectedDisconnectFlow",
                BindingFlags.Public | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "TriggerUnexpectedDisconnectFlow(ISessionContext, ISceneFlowActions, string)");
            method.Invoke(sceneFlowService, new object[] { _sessionManagerComponent, _sessionManagerComponent, source });
        }

        private void RunOnClientStoppedLogic(object sessionManager, NetworkManager networkManager, object sceneFlowService) {
            var lifecycleType = Type.GetType("Network.Session.SessionNetworkLifecycle, Assembly-CSharp");
            Assert.That(lifecycleType, Is.Not.Null, "SessionNetworkLifecycle type");
            var method = lifecycleType.GetMethod("RunOnClientStoppedLogic", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, "RunOnClientStoppedLogic");
            var trigger = sceneFlowService != null
                ? (Action<string>)(source => InvokeTriggerUnexpectedDisconnectFlow(sceneFlowService, source))
                : _ => { };
            method.Invoke(null, new[] { sessionManager, networkManager, (Func<bool>)HasActiveSession, trigger });
            return;
            bool HasActiveSession() =>
                (bool)lifecycleType.GetProperty("HasActiveSession", BindingFlags.Public | BindingFlags.Static)
                    .GetValue(null);
        }

        private static bool GetUnexpectedDisconnectInFlight(object sceneFlowService) {
            Assert.That(sceneFlowService, Is.Not.Null, "Scene flow service should not be null.");
            return PlayModeTestUtils.GetPrivateField<bool>(sceneFlowService, "_unexpectedDisconnectInFlight");
        }
    }
}
