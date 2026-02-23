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
        public const string RandomSelectionOption = "Random";

        [Serializable]
        public class MapBackgroundEntry {
            public string mapId = "Map";
            public GameObject mapGeometryRoot;
            public List<GameObject> mannequinSetups = new();
        }

        private sealed class SetupSelection {
            public string DisplayName;
            public int MapIndex;
            public int SetupIndex;
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
        private readonly List<SetupSelection> _cachedSelections = new();
        private readonly Dictionary<string, SetupSelection> _cachedSelectionLookup = new(StringComparer.OrdinalIgnoreCase);
        private bool _selectionCacheDirty = true;

        private void Awake() {
            _selectionCacheDirty = true;
        }

        private void OnValidate() {
            _selectionCacheDirty = true;
        }

        private void RandomizeForMainMenuEntry() {
            if(!TryGetRandomSelection(out var mapIndex, out var mannequinIndex)) {
                DeactivateAllRegisteredObjects();
                return;
            }

            ActivateSelection(mapIndex, mannequinIndex);
        }

        public void ApplySelectionForMainMenuEntry(string selectionName) {
            if(IsRandomSelection(selectionName)) {
                RandomizeForMainMenuEntry();
                return;
            }

            if(TryGetSelection(selectionName, out var selection)) {
                ActivateSelection(selection.MapIndex, selection.SetupIndex);
                return;
            }

            RandomizeForMainMenuEntry();
        }

        public IReadOnlyList<string> GetAvailableSelectionNames() {
            RebuildSelectionCacheIfNeeded();
            var names = new List<string>(_cachedSelections.Count);
            foreach(var t in _cachedSelections) {
                names.Add(t.DisplayName);
            }

            return names;
        }

        public static bool IsRandomSelection(string selectionName) {
            return string.IsNullOrWhiteSpace(selectionName) ||
                   string.Equals(selectionName, RandomSelectionOption, StringComparison.OrdinalIgnoreCase);
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

            var resolvedSetups = GetResolvedMannequinSetups(mapEntry);
            if(mannequinIndex < 0 || mannequinIndex >= resolvedSetups.Count) {
                return;
            }

            var selectedSetup = resolvedSetups[mannequinIndex];
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

            RebuildSelectionCacheIfNeeded();
            if(_cachedSelections.Count == 0) return false;

            var selection = _cachedSelections[UnityEngine.Random.Range(0, _cachedSelections.Count)];
            selectedMapIndex = selection.MapIndex;
            selectedMannequinIndex = selection.SetupIndex;
            return true;
        }

        private void DeactivateAllRegisteredObjects() {
            if(mapEntries == null) return;

            foreach(var entry in mapEntries) {
                if(entry == null) continue;

                if(entry.mapGeometryRoot != null && entry.mapGeometryRoot.activeSelf) {
                    entry.mapGeometryRoot.SetActive(false);
                }

                var resolvedSetups = GetResolvedMannequinSetups(entry);
                foreach(var setup in resolvedSetups) {
                    if(setup != null && setup.activeSelf) {
                        setup.SetActive(false);
                    }
                }
            }
        }

        private void SetAllRegisteredMannequinCamerasEnabled(bool enabled) {
            if(mapEntries == null) return;
            foreach(var entry in mapEntries) {
                if(entry == null) continue;

                var resolvedSetups = GetResolvedMannequinSetups(entry);
                foreach(var t in resolvedSetups) {
                    SetSetupCamerasEnabled(t, enabled);
                }
            }
        }

        private static void SetSetupCamerasEnabled(GameObject setupRoot, bool enabled) {
            if(setupRoot == null) return;
            var cameras = setupRoot.GetComponentsInChildren<Camera>(true);
            foreach(var cam in cameras) {
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

        private bool TryGetSelection(string selectionName, out SetupSelection selection) {
            selection = null;
            RebuildSelectionCacheIfNeeded();

            return !string.IsNullOrWhiteSpace(selectionName) && _cachedSelectionLookup.TryGetValue(selectionName, out selection);
        }

        private void RebuildSelectionCacheIfNeeded() {
            if(!_selectionCacheDirty) {
                return;
            }

            _selectionCacheDirty = false;
            _cachedSelections.Clear();
            _cachedSelectionLookup.Clear();

            if(mapEntries == null || mapEntries.Count == 0) {
                return;
            }

            for(var mapIndex = 0; mapIndex < mapEntries.Count; mapIndex++) {
                var entry = mapEntries[mapIndex];
                if(entry == null || entry.mapGeometryRoot == null) {
                    continue;
                }

                var resolvedSetups = GetResolvedMannequinSetups(entry);
                for(var setupIndex = 0; setupIndex < resolvedSetups.Count; setupIndex++) {
                    var setup = resolvedSetups[setupIndex];
                    if(setup == null) {
                        continue;
                    }

                    var displayName = BuildUniqueSelectionName(setup.name, entry.mapId, _cachedSelectionLookup);
                    var selection = new SetupSelection {
                        DisplayName = displayName,
                        MapIndex = mapIndex,
                        SetupIndex = setupIndex
                    };

                    _cachedSelections.Add(selection);
                    _cachedSelectionLookup[displayName] = selection;
                }
            }
        }

        private static List<GameObject> GetResolvedMannequinSetups(MapBackgroundEntry entry) {
            var resolved = new List<GameObject>();
            if(entry == null) {
                return resolved;
            }

            var seen = new HashSet<int>();
            AddSetupsFromManualList(entry.mannequinSetups, resolved, seen);
            AddSetupsFromMannequinsContainer(entry.mapGeometryRoot, resolved, seen);
            return resolved;
        }

        private static void AddSetupsFromManualList(List<GameObject> manualSetups, ICollection<GameObject> resolved, ISet<int> seen) {
            if(manualSetups == null) {
                return;
            }

            foreach(var t in manualSetups) {
                TryAddSetup(t, resolved, seen);
            }
        }

        private static void AddSetupsFromMannequinsContainer(GameObject mapGeometryRoot, ICollection<GameObject> resolved, ISet<int> seen) {
            if(mapGeometryRoot == null) {
                return;
            }

            var mannequinsRoot = FindNamedChildRecursive(mapGeometryRoot.transform, "MANNEQUINS");
            if(mannequinsRoot == null) {
                mannequinsRoot = FindNamedChildRecursive(mapGeometryRoot.transform, "MANNEQUIN");
            }

            if(mannequinsRoot == null) {
                return;
            }

            for(var i = 0; i < mannequinsRoot.childCount; i++) {
                TryAddSetup(mannequinsRoot.GetChild(i).gameObject, resolved, seen);
            }
        }

        private static Transform FindNamedChildRecursive(Transform root, string targetName) {
            if(root == null || string.IsNullOrWhiteSpace(targetName)) {
                return null;
            }

            if(string.Equals(root.name, targetName, StringComparison.OrdinalIgnoreCase)) {
                return root;
            }

            for(var i = 0; i < root.childCount; i++) {
                var found = FindNamedChildRecursive(root.GetChild(i), targetName);
                if(found != null) {
                    return found;
                }
            }

            return null;
        }

        private static void TryAddSetup(GameObject setup, ICollection<GameObject> resolved, ISet<int> seen) {
            if(setup == null || resolved == null || seen == null) {
                return;
            }

            var id = setup.GetInstanceID();
            if(!seen.Add(id)) {
                return;
            }

            resolved.Add(setup);
        }

        private static string BuildUniqueSelectionName(string setupName, string mapId, IDictionary<string, SetupSelection> existing) {
            var baseName = string.IsNullOrWhiteSpace(setupName) ? "Setup" : setupName.Trim();
            if(!existing.ContainsKey(baseName)) {
                return baseName;
            }

            var mapName = string.IsNullOrWhiteSpace(mapId) ? "Map" : mapId.Trim();
            var withMapName = $"{baseName} ({mapName})";
            if(!existing.ContainsKey(withMapName)) {
                return withMapName;
            }

            var suffix = 2;
            while(true) {
                var candidate = $"{baseName} ({mapName} {suffix})";
                if(!existing.ContainsKey(candidate)) {
                    return candidate;
                }

                suffix++;
            }
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
                foreach(var animator in animators) {
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
            foreach(var config in mannequinConfigs) {
                if(config == null || !config.enabled || !config.gameObject.activeInHierarchy) continue;
                config.ApplyNow(forceAnimationPoseRefresh: true);
                if(Application.isPlaying) {
                    config.RecalibrateRuntimeLookOffset();
                }
            }
        }
    }

}
