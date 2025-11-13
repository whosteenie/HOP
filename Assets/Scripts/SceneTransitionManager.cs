using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Network.Singletons {
    public class SceneTransitionManager : MonoBehaviour {
        [SerializeField] private UIDocument transitionDocument;
        [SerializeField] private float fadeDuration = 0.5f;
        
        private VisualElement _transitionOverlay;
        private bool _isTransitioning;

        public static SceneTransitionManager Instance { get; private set; }

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnEnable() {
            if(transitionDocument != null) {
                var root = transitionDocument.rootVisualElement;
                _transitionOverlay = root.Q<VisualElement>("transition-overlay");
            }
        }

        /// <summary>
        /// Fade to black, execute action, then fade back in
        /// </summary>
        public IEnumerator FadeTransition(System.Action duringFade) {
            if(_isTransitioning) yield break;
            
            _isTransitioning = true;

            // Fade to black
            yield return StartCoroutine(FadeOut());

            // Execute action (scene load, etc.)
            duringFade?.Invoke();

            // Wait a frame for scene to load
            yield return null;

            // Fade back in
            yield return StartCoroutine(FadeIn());

            _isTransitioning = false;
        }

        /// <summary>
        /// Fade to black only
        /// </summary>
        public IEnumerator FadeOut() {
            if(_transitionOverlay == null) yield break;

            _transitionOverlay.style.display = DisplayStyle.Flex;
            _transitionOverlay.pickingMode = PickingMode.Position;
            _transitionOverlay.RemoveFromClassList("hidden");
            _transitionOverlay.AddToClassList("visible");

            yield return new WaitForSeconds(fadeDuration);
        }

        /// <summary>
        /// Fade from black to clear
        /// </summary>
        public IEnumerator FadeIn() {
            if(_transitionOverlay == null) yield break;

            yield return new WaitForSeconds(0.1f); // Small delay for scene to settle

            _transitionOverlay.RemoveFromClassList("visible");
            _transitionOverlay.AddToClassList("hidden");

            yield return new WaitForSeconds(fadeDuration);
            
            _transitionOverlay.pickingMode = PickingMode.Ignore;
            _transitionOverlay.style.display = DisplayStyle.None;
        }

        /// <summary>
        /// Quick fade for instant transitions
        /// </summary>
        public void FadeOutImmediate() {
            if(_transitionOverlay == null) return;
            
            _transitionOverlay.style.display = DisplayStyle.Flex;
            _transitionOverlay.pickingMode = PickingMode.Position;
            
            var instantList = new StyleList<TimeValue>(new List<TimeValue> { new TimeValue(0) });
            _transitionOverlay.style.transitionDuration = instantList;

            _transitionOverlay.RemoveFromClassList("hidden");
            _transitionOverlay.AddToClassList("visible");
            
            // Restore normal transition duration after one frame
            StartCoroutine(RestoreTransitionDuration());
        }

        public void FadeInImmediate() {
            if(_transitionOverlay == null) return;
            
            var instantList = new StyleList<TimeValue>(new List<TimeValue> { new TimeValue(0) });
            _transitionOverlay.style.transitionDuration = instantList;
            
            _transitionOverlay.RemoveFromClassList("visible");
            _transitionOverlay.AddToClassList("hidden");
            _transitionOverlay.pickingMode = PickingMode.Ignore;
            _transitionOverlay.style.display = DisplayStyle.None;
            
            StartCoroutine(RestoreTransitionDuration());
        }

        private IEnumerator RestoreTransitionDuration() {
            yield return null;
            if(_transitionOverlay != null) {
                _transitionOverlay.style.transitionDuration = StyleKeyword.Null;
            }
        }
    }
}