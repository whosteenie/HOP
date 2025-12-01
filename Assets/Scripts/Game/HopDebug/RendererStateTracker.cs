using System.Diagnostics;
using UnityEngine;

namespace Game.HopDebug {
    /// <summary>
    /// Debug component that tracks when a renderer's enabled state changes and logs who modified it.
    /// Can be assigned in inspector or will auto-find the renderer on the same GameObject.
    /// </summary>
    public class RendererStateTracker : MonoBehaviour {
        [Header("Debug Settings")]
        [Tooltip("Renderer to track. If null, will auto-find on this GameObject.")] [SerializeField]
        private Renderer targetRenderer;

        [Tooltip("Enable tracking on Start")] [SerializeField] private bool autoStartTracking = true;

        private bool _lastEnabledState;
        private bool _isTracking;
        private bool _lastActiveState;
        private bool _lastActiveInHierarchyState;

        private void Awake() {
            // Auto-find renderer if not assigned
            if(targetRenderer == null) {
                targetRenderer = GetComponent<Renderer>();
            }

            if(targetRenderer != null) {
                _lastEnabledState = targetRenderer.enabled;
                _lastActiveState = targetRenderer.gameObject.activeSelf;
                _lastActiveInHierarchyState = targetRenderer.gameObject.activeInHierarchy;

                if(autoStartTracking) {
                    _isTracking = true;
                }
            } else {
                UnityEngine.Debug.LogWarning($"[RendererStateTracker] No renderer found on {gameObject.name}");
            }
        }

        private void LateUpdate() {
            if(!_isTracking || targetRenderer == null) return;

            var currentEnabled = targetRenderer.enabled;
            var currentActive = targetRenderer.gameObject.activeSelf;
            var currentActiveInHierarchy = targetRenderer.gameObject.activeInHierarchy;

            // Check if enabled state changed
            if(currentEnabled != _lastEnabledState) {
                var stackTrace = new StackTrace(true);
                UnityEngine.Debug.LogWarning(
                    $"[RendererStateTracker] Renderer.enabled changed on {gameObject.name} from {_lastEnabledState} to {currentEnabled}\n" +
                    $"GameObject active: {currentActive}, activeInHierarchy: {currentActiveInHierarchy}\n" +
                    $"Stack trace:\n{stackTrace}");
                _lastEnabledState = currentEnabled;
            }

            // Also track GameObject active state changes
            if(currentActive != _lastActiveState || currentActiveInHierarchy != _lastActiveInHierarchyState) {
                UnityEngine.Debug.LogWarning(
                    $"[RendererStateTracker] GameObject active state changed on {gameObject.name}\n" +
                    $"active: {_lastActiveState} -> {currentActive}\n" +
                    $"activeInHierarchy: {_lastActiveInHierarchyState} -> {currentActiveInHierarchy}");
                _lastActiveState = currentActive;
                _lastActiveInHierarchyState = currentActiveInHierarchy;
            }
        }

        public void StartTracking() {
            if(targetRenderer != null) {
                _lastEnabledState = targetRenderer.enabled;
                _lastActiveState = targetRenderer.gameObject.activeSelf;
                _lastActiveInHierarchyState = targetRenderer.gameObject.activeInHierarchy;
                _isTracking = true;
                UnityEngine.Debug.LogWarning($"[RendererStateTracker] Started tracking renderer on {gameObject.name}");
            }
        }

        public void StopTracking() {
            _isTracking = false;
        }
    }
}