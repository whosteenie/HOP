using System.IO;
using Unity.MLAgents.Demonstrations;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace Game.AI {
    /// <summary>
    /// Helper component that manages demonstration recording based on scene and user input.
    /// Attach this to the same GameObject as DemonstrationRecorder.
    /// Only records when in Game scene, prevents menu/lobby recording.
    /// </summary>
    [RequireComponent(typeof(DemonstrationRecorder))]
    public class DemoRecordingHelper : MonoBehaviour {
        [Header("Settings")]
        [Tooltip("Hot key to toggle recording on/off during gameplay. Currently hardcoded to F9.")]
        [SerializeField] private bool enableHotkey = true;
        
        [Header("Debug")]
        [Tooltip("Show recording status in console.")]
        [SerializeField] private bool showDebugLogs = true;
        
        private DemonstrationRecorder _recorder;
        private bool _isRecordingEnabled;
        private string _cachedSceneName;
        
        private void Awake() {
            _recorder = GetComponent<DemonstrationRecorder>();
            if(_recorder == null) {
                Debug.LogError("[DemoRecordingHelper] DemonstrationRecorder component not found!");
                enabled = false;
                return;
            }

            ConfigureRecorderPath();
            
            UpdateCachedSceneName();
            
            // Subscribe to scene changes
            SceneManager.sceneLoaded += OnSceneLoaded;
        }
        
        private void OnDestroy() {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }
        
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
            UpdateCachedSceneName();
            UpdateRecordingState();
        }
        
        private void UpdateCachedSceneName() {
            var activeScene = SceneManager.GetActiveScene();
            if(activeScene.IsValid()) {
                _cachedSceneName = activeScene.name;
            }
        }
        
        private void Update() {
            // Check for toggle key press using new Input System
            if(enableHotkey && Keyboard.current != null && Keyboard.current.f9Key.wasPressedThisFrame) {
                ToggleRecording();
            }
            
            // Update recording state based on scene
            UpdateRecordingState();
        }
        
        private void ToggleRecording() {
            _isRecordingEnabled = !_isRecordingEnabled;
            
            if(showDebugLogs) {
                Debug.Log($"[DemoRecordingHelper] Recording {(_isRecordingEnabled ? "ENABLED" : "DISABLED")} " +
                          "(Press F9 to toggle)");
            }
            
            UpdateRecordingState();
        }
        
        private void UpdateRecordingState() {
            if(_recorder == null) return;
            
            // Only record in Game scene
            var isInGameScene = _cachedSceneName != null && _cachedSceneName.Contains("Game");
            
            // Final recording state: enabled by user AND in game scene
            var shouldRecord = _isRecordingEnabled && isInGameScene;

            if(_recorder.Record == shouldRecord) return;
            _recorder.Record = shouldRecord;

            switch(showDebugLogs) {
                case true when shouldRecord:
                    Debug.Log($"[DemoRecordingHelper] Recording STARTED in {_cachedSceneName} scene");
                    break;
                case true when !shouldRecord && _isRecordingEnabled:
                    Debug.Log($"[DemoRecordingHelper] Recording PAUSED (not in Game scene: {_cachedSceneName})");
                    break;
            }
        }

        private void ConfigureRecorderPath() {
            if(_recorder == null) return;

            if(Application.isEditor) {
                // Keep whatever directory is set in the editor (default: Assets/Demonstrations)
                return;
            }

            var demoRoot = Path.Combine(Application.persistentDataPath, "ML Demo");
            try {
                if(!Directory.Exists(demoRoot)) {
                    Directory.CreateDirectory(demoRoot);
                }
                _recorder.DemonstrationDirectory = demoRoot;
                if(!string.IsNullOrEmpty(_recorder.DemonstrationName)) return;
                var behavior = GetComponent<Unity.MLAgents.Policies.BehaviorParameters>();
                var baseName = behavior != null ? behavior.BehaviorName : "HopMovement";
                _recorder.DemonstrationName = $"{baseName}_{System.DateTime.Now:yyyyMMdd_HHmmss}";
            } catch(IOException ioEx) {
                Debug.LogError($"[DemoRecordingHelper] Failed to configure demo directory at {demoRoot}: {ioEx.Message}");
            }
        }
        
        /// <summary>
        /// Enable recording programmatically.
        /// </summary>
        public void EnableRecording() {
            _isRecordingEnabled = true;
            UpdateRecordingState();
        }
        
        /// <summary>
        /// Disable recording programmatically.
        /// </summary>
        public void DisableRecording() {
            _isRecordingEnabled = false;
            UpdateRecordingState();
        }
        
        private void OnGUI() {
            // Show recording indicator in top-right corner
            if(_recorder == null || !_recorder.Record) return;
            var style = new GUIStyle(GUI.skin.label) {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.red }
            };
                
            GUI.Label(new Rect(Screen.width - 200, 10, 200, 30), "● RECORDING", style);
        }
    }
}

