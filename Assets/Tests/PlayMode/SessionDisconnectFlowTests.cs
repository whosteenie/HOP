using System.Collections;
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

        [TearDown]
        public void TearDown() {
            if(_networkManagerObject != null) {
                Object.DestroyImmediate(_networkManagerObject);
            }

            if(_sessionObject != null) {
                Object.DestroyImmediate(_sessionObject);
            }

            _networkManagerObject = null;
            _sessionManagerComponent = null;
            _sessionObject = null;
        }

        [UnityTest]
        public IEnumerator UnexpectedDisconnect_TransitionsBackToMainMenu() {
            var testScene = SceneManager.CreateScene("UnexpectedDisconnectTestScene");
            Assert.That(SceneManager.SetActiveScene(testScene), Is.True, "Failed to activate temporary gameplay scene.");
            Assert.That(SceneManager.GetActiveScene().name, Is.EqualTo("UnexpectedDisconnectTestScene"));

            _networkManagerObject = new GameObject("NetworkManagerTest");
            var networkManager = _networkManagerObject.AddComponent<NetworkManager>();
            Assert.That(networkManager, Is.Not.Null, "Failed to create test NetworkManager.");

            _sessionObject = new GameObject("SessionManagerTest");
            var sessionManagerType = System.Type.GetType("Network.Session.SessionManager, Assembly-CSharp");
            Assert.That(sessionManagerType, Is.Not.Null,
                "Could not resolve Network.Session.SessionManager type from Assembly-CSharp.");

            _sessionManagerComponent = _sessionObject.AddComponent(sessionManagerType);
            Assert.That(_sessionManagerComponent, Is.Not.Null);

            // Avoid running Start() bootstrap side effects; this test only validates disconnect-to-menu flow.
            ((Behaviour)_sessionManagerComponent).enabled = false;

            var triggerMethod = sessionManagerType.GetMethod("TriggerUnexpectedDisconnectFlow", PrivateInstance);
            Assert.That(triggerMethod, Is.Not.Null, "Expected private TriggerUnexpectedDisconnectFlow method.");

            triggerMethod.Invoke(_sessionManagerComponent, new object[] { "PlayModeTest" });

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
    }
}
