using System.Collections;
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Tests.PlayMode {
    public class SessionDisconnectFlowTests {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private GameObject _networkManagerObject;
        private GameObject _sessionObject;
        private Component _sessionManagerComponent;
        private readonly List<(Behaviour behaviour, bool wasEnabled)> _mutedBackgroundBehaviours = new();

        [SetUp]
        public void SetUp() {
            MuteMainMenuSessionManagers();
        }

        [TearDown]
        public void TearDown() {
            if(_networkManagerObject != null) {
                UnityEngine.Object.DestroyImmediate(_networkManagerObject);
            }

            if(_sessionObject != null) {
                UnityEngine.Object.DestroyImmediate(_sessionObject);
            }

            _networkManagerObject = null;
            _sessionManagerComponent = null;
            _sessionObject = null;

            RestoreMutedBackgroundBehaviours();
        }

        [UnityTest]
        public IEnumerator UnexpectedDisconnect_TransitionsBackToMainMenu() {
            var testScene = SceneManager.CreateScene("UnexpectedDisconnectTestScene");
            Assert.That(SceneManager.SetActiveScene(testScene), Is.True, "Failed to activate temporary gameplay scene.");
            Assert.That(SceneManager.GetActiveScene().name, Is.EqualTo("UnexpectedDisconnectTestScene"));

            var sessionManagerType = CreateSessionManagerForTest("SessionManagerTest");
            InvokePrivate(sessionManagerType, "TriggerUnexpectedDisconnectFlow", "PlayModeTest");

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

            var sessionManagerType = CreateSessionManagerForTest("SessionManagerExpectedDisconnectGuard");
            SetPrivateAutoPropertyBackingField("IsExpectedDisconnect", true);

            InvokePrivate(sessionManagerType, "OnClientStopped", false);
            yield return null;
            yield return null;

            Assert.That(SceneManager.GetActiveScene().name, Is.EqualTo("ExpectedDisconnectGuardScene"),
                "Expected disconnect should not trigger unexpected disconnect flow.");

            Assert.That(GetPrivateField<bool>("_unexpectedDisconnectInFlight"), Is.False,
                "Unexpected disconnect flow should remain idle when disconnect was expected.");
        }

        [UnityTest]
        public IEnumerator OnClientStopped_UnexpectedDisconnect_TransitionsBackToMainMenu() {
            var testScene = SceneManager.CreateScene("UnexpectedDisconnectViaClientStopped");
            Assert.That(SceneManager.SetActiveScene(testScene), Is.True);
            Assert.That(SceneManager.GetActiveScene().name, Is.EqualTo("UnexpectedDisconnectViaClientStopped"));

            var sessionManagerType = CreateSessionManagerForTest("SessionManagerUnexpectedClientStopped");
            SetPrivateAutoPropertyBackingField("IsExpectedDisconnect", false);

            InvokePrivate(sessionManagerType, "OnClientStopped", false);

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
            while(Time.realtimeSinceStartup < timeoutAt && GetPrivateField<bool>("_unexpectedDisconnectInFlight")) {
                yield return null;
            }

            Assert.That(GetPrivateField<bool>("_unexpectedDisconnectInFlight"), Is.False,
                "Unexpected disconnect flow should complete and clear in-flight guard.");
        }

        private Type CreateSessionManagerForTest(string objectName) {
            _networkManagerObject = new GameObject("NetworkManagerTest");
            var networkManager = _networkManagerObject.AddComponent<NetworkManager>();
            Assert.That(networkManager, Is.Not.Null, "Failed to create test NetworkManager.");
            UnityEngine.Object.DontDestroyOnLoad(_networkManagerObject);

            _sessionObject = new GameObject(objectName);
            var sessionManagerType = Type.GetType("Network.Session.SessionManager, Assembly-CSharp");
            Assert.That(sessionManagerType, Is.Not.Null,
                "Could not resolve Network.Session.SessionManager type from Assembly-CSharp.");

            _sessionManagerComponent = _sessionObject.AddComponent(sessionManagerType);
            Assert.That(_sessionManagerComponent, Is.Not.Null, "Failed to add SessionManager test component.");
            UnityEngine.Object.DontDestroyOnLoad(_sessionObject);

            // Avoid Start() bootstrap side effects; these tests only validate disconnect/session flow.
            ((Behaviour)_sessionManagerComponent).enabled = false;
            return sessionManagerType;
        }

        private void InvokePrivate(Type sessionManagerType, string methodName, params object[] args) {
            var method = sessionManagerType.GetMethod(methodName, PrivateInstance);
            Assert.That(method, Is.Not.Null, $"Expected private method '{methodName}'.");
            method.Invoke(_sessionManagerComponent, args);
        }

        private T GetPrivateField<T>(string fieldName) {
            var field = _sessionManagerComponent.GetType().GetField(fieldName, PrivateInstance);
            Assert.That(field, Is.Not.Null, $"Expected private field '{fieldName}'.");
            return (T)field.GetValue(_sessionManagerComponent);
        }

        private void SetPrivateAutoPropertyBackingField(string propertyName, object value) {
            var backingField = $"<{propertyName}>k__BackingField";
            var field = _sessionManagerComponent.GetType().GetField(backingField, PrivateInstance);
            Assert.That(field, Is.Not.Null, $"Expected auto-property backing field '{backingField}'.");
            field.SetValue(_sessionManagerComponent, value);
        }

        private void MuteMainMenuSessionManagers() {
            _mutedBackgroundBehaviours.Clear();
            var mainMenuSessionManagerType = Type.GetType("Game.Menu.MainMenuSessionManager, Assembly-CSharp");
            if(mainMenuSessionManagerType == null) {
                return;
            }

            foreach(var obj in Resources.FindObjectsOfTypeAll(mainMenuSessionManagerType)) {
                if(obj is not Behaviour behaviour || behaviour == null) {
                    continue;
                }

                if(!behaviour.gameObject.scene.IsValid()) {
                    continue;
                }

                _mutedBackgroundBehaviours.Add((behaviour, behaviour.enabled));
                behaviour.enabled = false;
            }
        }

        private void RestoreMutedBackgroundBehaviours() {
            foreach(var (behaviour, wasEnabled) in _mutedBackgroundBehaviours) {
                if(behaviour != null) {
                    behaviour.enabled = wasEnabled;
                }
            }

            _mutedBackgroundBehaviours.Clear();
        }
    }
}
