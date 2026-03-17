using System.Collections;
using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tests.PlayMode {
    public class DisconnectTransitionControllerTests {
        private GameObject _createdControllerObject;
        private Type _controllerType;

        [SetUp]
        public void SetUp() {
            _controllerType = PlayModeTestUtils.ResolveTypeOrAssert(
                "Game.UI.Misc.DisconnectTransitionController, Assembly-CSharp");
        }

        [TearDown]
        public void TearDown() {
            PlayModeTestUtils.DestroyImmediateIfExists(ref _createdControllerObject);
        }

        [Test]
        public void CaptureDuplicateFpVisuals_WithNullPlayer_ReturnsFalse_AndStaysInactive() {
            var controller = EnsureController();

            var captureMethod = controller.GetType().GetMethod("CaptureDuplicateFpVisuals");
            Assert.That(captureMethod, Is.Not.Null);
            var result = (bool)captureMethod.Invoke(controller, new object[] { null });

            Assert.That(result, Is.False, "Null player should fail fast and use fallback path.");
            Assert.That(PlayModeTestUtils.GetPrivateField<bool>(controller, "_isActive"), Is.False,
                "Controller should remain inactive when capture fails.");
        }

        [Test]
        public void CleanupDuplicate_IsIdempotent_WhenNothingCaptured() {
            var controller = EnsureController();

            Assert.DoesNotThrow(() => controller.GetType().GetMethod("CleanupDuplicate")?.Invoke(controller, null));
            Assert.DoesNotThrow(() => controller.GetType().GetMethod("CleanupDuplicate")?.Invoke(controller, null));
            Assert.That(PlayModeTestUtils.GetPrivateField<GameObject>(controller, "_duplicateFpVisualsRoot"), Is.Null);
            Assert.That(PlayModeTestUtils.GetPrivateField<bool>(controller, "_isActive"), Is.False);
        }

        [UnityTest]
        public IEnumerator DuplicateController_DoesNotReplaceExistingSingleton() {
            var controller = EnsureController();
            var originalInstance = GetSingletonInstance();
            Assert.That(originalInstance, Is.Not.Null);

            var duplicateObject = new GameObject("DisconnectTransitionController_Duplicate");
            duplicateObject.AddComponent(_controllerType);

            yield return null;

            Assert.That(GetSingletonInstance(), Is.SameAs(originalInstance),
                "Duplicate controller should not replace the existing singleton instance.");
            Assert.That(duplicateObject == null || !duplicateObject, Is.True,
                "Duplicate controller object should be destroyed by Awake singleton guard.");

            // Keep reference clean in case Destroy() has not completed before test tear-down.
            _ = controller;
        }

        private Component EnsureController() {
            var existing = GetSingletonInstance();
            if(existing != null) {
                return existing as Component;
            }

            _createdControllerObject = new GameObject("DisconnectTransitionController_Test");
            var controller = _createdControllerObject.AddComponent(_controllerType);
            Assert.That(controller, Is.Not.Null);
            return controller;
        }

        private object GetSingletonInstance() {
            var instanceProperty = _controllerType.GetProperty("Instance");
            Assert.That(instanceProperty, Is.Not.Null, "DisconnectTransitionController.Instance property not found.");
            return instanceProperty.GetValue(null);
        }
    }
}
