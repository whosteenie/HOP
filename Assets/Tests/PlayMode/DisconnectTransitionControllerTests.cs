using System.Collections;
using Network;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tests.PlayMode {
    public class DisconnectTransitionControllerTests {
        private GameObject _createdControllerObject;

        [TearDown]
        public void TearDown() {
            PlayModeTestUtils.DestroyImmediateIfExists(ref _createdControllerObject);
        }

        [Test]
        public void CaptureAndShowDuplicateFpVisuals_WithNullPlayer_ReturnsFalse_AndStaysInactive() {
            var controller = EnsureController();

            var result = controller.CaptureAndShowDuplicateFpVisuals(null);

            Assert.That(result, Is.False, "Null player should fail fast and use fallback path.");
            Assert.That(PlayModeTestUtils.GetPrivateField<bool>(controller, "_isActive"), Is.False,
                "Controller should remain inactive when capture fails.");
        }

        [Test]
        public void CleanupDuplicate_IsIdempotent_WhenNothingCaptured() {
            var controller = EnsureController();

            Assert.DoesNotThrow(() => controller.CleanupDuplicate());
            Assert.DoesNotThrow(() => controller.CleanupDuplicate());
            Assert.That(PlayModeTestUtils.GetPrivateField<GameObject>(controller, "_duplicateFpVisualsRoot"), Is.Null);
            Assert.That(PlayModeTestUtils.GetPrivateField<bool>(controller, "_isActive"), Is.False);
        }

        [UnityTest]
        public IEnumerator DuplicateController_DoesNotReplaceExistingSingleton() {
            var controller = EnsureController();
            var originalInstance = DisconnectTransitionController.Instance;
            Assert.That(originalInstance, Is.Not.Null);

            var duplicateObject = new GameObject("DisconnectTransitionController_Duplicate");
            duplicateObject.AddComponent<DisconnectTransitionController>();

            yield return null;

            Assert.That(DisconnectTransitionController.Instance, Is.SameAs(originalInstance),
                "Duplicate controller should not replace the existing singleton instance.");
            Assert.That(duplicateObject == null || !duplicateObject, Is.True,
                "Duplicate controller object should be destroyed by Awake singleton guard.");

            // Keep reference clean in case Destroy() has not completed before test tear-down.
            duplicateObject = null;
            _ = controller;
        }

        private DisconnectTransitionController EnsureController() {
            if(DisconnectTransitionController.Instance != null) {
                return DisconnectTransitionController.Instance;
            }

            _createdControllerObject = new GameObject("DisconnectTransitionController_Test");
            var controller = _createdControllerObject.AddComponent<DisconnectTransitionController>();
            Assert.That(controller, Is.Not.Null);
            return controller;
        }
    }
}
