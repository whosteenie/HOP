using System;
using System.Collections;
using System.Collections.Generic;
using Game.Player;
using UnityEngine;

namespace Game.Menu {
    /// <summary>
    /// Picks one map geometry and one mannequin setup at random for main menu background presentation.
    /// All non-selected registered objects are disabled.
    /// </summary>
    [DisallowMultipleComponent]
    public class MainMenuBackgroundRandomizer : MonoBehaviour {
        [Serializable]
        public class MapBackgroundEntry {
            public string mapId = "Map";
            public GameObject mapGeometryRoot;
            public List<GameObject> mannequinSetups = new();
        }

        [Header("Registered Backgrounds")]
        [SerializeField] private List<MapBackgroundEntry> mapEntries = new();

        [Header("Behavior")]
        [SerializeField] private bool enforceSingleActiveMannequinCamera = true;
        [SerializeField] private bool logSelection;

        [Header("Debug")]
        [SerializeField] private string lastSelectedMap;
        [SerializeField] private string lastSelectedSetup;
        private Coroutine _finalizeSelectionCoroutine;

        public void RandomizeForMainMenuEntry() {
            if(!TryGetRandomSelection(out var mapIndex, out var mannequinIndex)) {
                DeactivateAllRegisteredObjects();
                return;
            }

            ActivateSelection(mapIndex, mannequinIndex);
        }

        [ContextMenu("Activate Random Background")]
        public void ActivateRandomBackgroundContextMenu() {
            RandomizeForMainMenuEntry();
        }

        [ContextMenu("Deactivate All Registered Background Objects")]
        public void DeactivateAllRegisteredObjectsContextMenu() {
            DeactivateAllRegisteredObjects();
        }

        private void ActivateSelection(int mapIndex, int mannequinIndex) {
            DeactivateAllRegisteredObjects();

            if(mapIndex < 0 || mapIndex >= mapEntries.Count) return;
            var mapEntry = mapEntries[mapIndex];
            if(mapEntry == null) return;

            if(mapEntry.mapGeometryRoot != null) {
                mapEntry.mapGeometryRoot.SetActive(true);
            }

            if(mapEntry.mannequinSetups == null || mannequinIndex < 0 || mannequinIndex >= mapEntry.mannequinSetups.Count) {
                return;
            }

            var selectedSetup = mapEntry.mannequinSetups[mannequinIndex];
            if(selectedSetup != null) {
                selectedSetup.SetActive(true);
            }

            if(enforceSingleActiveMannequinCamera) {
                SetAllRegisteredMannequinCamerasEnabled(false);
                SetSetupCamerasEnabled(selectedSetup, true);
            }

            lastSelectedMap = string.IsNullOrWhiteSpace(mapEntry.mapId) ? $"Map {mapIndex + 1}" : mapEntry.mapId;
            lastSelectedSetup = selectedSetup != null ? selectedSetup.name : "(none)";

            if(logSelection) {
                Debug.Log($"[MainMenuBackgroundRandomizer] Selected map='{lastSelectedMap}', setup='{lastSelectedSetup}'.", this);
            }

            QueueFinalizeSelection(selectedSetup);
        }

        private bool TryGetRandomSelection(out int selectedMapIndex, out int selectedMannequinIndex) {
            selectedMapIndex = -1;
            selectedMannequinIndex = -1;

            if(mapEntries == null || mapEntries.Count == 0) return false;

            var eligibleMapIndices = new List<int>(mapEntries.Count);
            for(var i = 0; i < mapEntries.Count; i++) {
                var entry = mapEntries[i];
                if(entry == null || entry.mapGeometryRoot == null || entry.mannequinSetups == null) continue;

                var validCount = 0;
                for(var j = 0; j < entry.mannequinSetups.Count; j++) {
                    if(entry.mannequinSetups[j] != null) validCount++;
                }

                if(validCount > 0) {
                    eligibleMapIndices.Add(i);
                }
            }

            if(eligibleMapIndices.Count == 0) return false;

            selectedMapIndex = eligibleMapIndices[UnityEngine.Random.Range(0, eligibleMapIndices.Count)];
            var selectedEntry = mapEntries[selectedMapIndex];

            var eligibleMannequinIndices = new List<int>(selectedEntry.mannequinSetups.Count);
            for(var i = 0; i < selectedEntry.mannequinSetups.Count; i++) {
                if(selectedEntry.mannequinSetups[i] != null) {
                    eligibleMannequinIndices.Add(i);
                }
            }

            if(eligibleMannequinIndices.Count == 0) {
                selectedMapIndex = -1;
                return false;
            }

            selectedMannequinIndex = eligibleMannequinIndices[UnityEngine.Random.Range(0, eligibleMannequinIndices.Count)];
            return true;
        }

        private void DeactivateAllRegisteredObjects() {
            if(mapEntries == null) return;

            for(var i = 0; i < mapEntries.Count; i++) {
                var entry = mapEntries[i];
                if(entry == null) continue;

                if(entry.mapGeometryRoot != null && entry.mapGeometryRoot.activeSelf) {
                    entry.mapGeometryRoot.SetActive(false);
                }

                if(entry.mannequinSetups == null) continue;
                for(var j = 0; j < entry.mannequinSetups.Count; j++) {
                    var setup = entry.mannequinSetups[j];
                    if(setup != null && setup.activeSelf) {
                        setup.SetActive(false);
                    }
                }
            }
        }

        private void SetAllRegisteredMannequinCamerasEnabled(bool enabled) {
            if(mapEntries == null) return;
            for(var i = 0; i < mapEntries.Count; i++) {
                var entry = mapEntries[i];
                if(entry?.mannequinSetups == null) continue;

                for(var j = 0; j < entry.mannequinSetups.Count; j++) {
                    SetSetupCamerasEnabled(entry.mannequinSetups[j], enabled);
                }
            }
        }

        private static void SetSetupCamerasEnabled(GameObject setupRoot, bool enabled) {
            if(setupRoot == null) return;
            var cameras = setupRoot.GetComponentsInChildren<Camera>(true);
            for(var i = 0; i < cameras.Length; i++) {
                var cam = cameras[i];
                if(cam != null) {
                    cam.enabled = enabled;
                }
            }
        }

        private void QueueFinalizeSelection(GameObject selectedSetup) {
            if(_finalizeSelectionCoroutine != null) {
                StopCoroutine(_finalizeSelectionCoroutine);
                _finalizeSelectionCoroutine = null;
            }

            if(selectedSetup == null) return;
            _finalizeSelectionCoroutine = StartCoroutine(FinalizeSelectionCoroutine(selectedSetup));
        }

        private IEnumerator FinalizeSelectionCoroutine(GameObject selectedSetup) {
            // Immediate pass after activation.
            ForceApplySetupPose(selectedSetup, rebindAnimators: true);

            // Next update and end-of-frame passes help lock animator state/time reliably.
            // Do not rebind again here; repeated rebinds can reset back to controller default state.
            yield return null;
            ForceApplySetupPose(selectedSetup, rebindAnimators: false);

            yield return new WaitForEndOfFrame();
            ForceApplySetupPose(selectedSetup, rebindAnimators: false);

            _finalizeSelectionCoroutine = null;
        }

        private static void ForceApplySetupPose(GameObject setupRoot, bool rebindAnimators) {
            if(setupRoot == null || !setupRoot.activeInHierarchy) return;

            if(rebindAnimators) {
                var animators = setupRoot.GetComponentsInChildren<Animator>(true);
                for(var i = 0; i < animators.Length; i++) {
                    var animator = animators[i];
                    if(animator == null
                       || !animator.enabled
                       || !animator.gameObject.activeInHierarchy
                       || animator.runtimeAnimatorController == null) {
                        continue;
                    }

                    animator.Rebind();
                    animator.Update(0f);
                }
            }

            var mannequinConfigs = setupRoot.GetComponentsInChildren<PlayerMannequinConfig>(true);
            for(var i = 0; i < mannequinConfigs.Length; i++) {
                var config = mannequinConfigs[i];
                if(config == null || !config.enabled || !config.gameObject.activeInHierarchy) continue;
                config.ApplyNow(forceAnimationPoseRefresh: true);
            }
        }
    }

}
