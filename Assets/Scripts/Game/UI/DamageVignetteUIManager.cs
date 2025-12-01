using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI {
    public class DamageVignetteUIManager : MonoBehaviour {
        public static DamageVignetteUIManager Instance { get; private set; }

        [Header("Timing")]
        [SerializeField] private float flashDuration = 0.12f; // time at full alpha
        [SerializeField] private float fadeDuration = 0.3f; // fade-out
        [SerializeField] private float maxAlpha = 0.8f;

        private UIDocument _uiDocument;
        private VisualElement _root;

        // Order: 0=Front,1=FrontRight,2=Right,3=BackRight,4=Back,5=BackLeft,6=Left,7=FrontLeft
        private VisualElement[] _indicators;
        private Coroutine[] _runningCoroutines;

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;

            _uiDocument = GetComponent<UIDocument>();
            _root = _uiDocument.rootVisualElement;

            var container = _root.Q<VisualElement>("damage-vignette-root");
            if(container == null) {
                Debug.LogError("[DamageVignetteUI] damage-vignette-root not found in UXML");
                return;
            }

            _indicators = new[] {
                container.Q<VisualElement>("hit-front"),
                container.Q<VisualElement>("hit-frontRight"),
                container.Q<VisualElement>("hit-right"),
                container.Q<VisualElement>("hit-backRight"),
                container.Q<VisualElement>("hit-back"),
                container.Q<VisualElement>("hit-backLeft"),
                container.Q<VisualElement>("hit-left"),
                container.Q<VisualElement>("hit-frontLeft"),
            };

            _runningCoroutines = new Coroutine[_indicators.Length];

            // Start hidden
            foreach(var t in _indicators) {
                if(t != null)
                    t.style.opacity = 0f;
            }
        }

        /// <summary>
        /// Call on local player to show directional hit from a world-space point.
        /// </summary>
        public void ShowHitFromWorldPoint(Vector3 worldHitPos, Transform cameraTransform, float intensity = 1f) {
            if(_indicators == null || cameraTransform == null)
                return;

            // Direction from camera to hit, flattened on XZ
            var toHit = worldHitPos - cameraTransform.position;
            var flatDir = Vector3.ProjectOnPlane(toHit, Vector3.up);
            if(flatDir.sqrMagnitude < 0.0001f)
                return;

            flatDir.Normalize();

            // SignedAngle:
            // +angle = hit is to the LEFT of forward
            // -angle = hit is to the RIGHT of forward
            var angle = Vector3.SignedAngle(cameraTransform.forward, flatDir, Vector3.up);

            // We want 0° = front, increase clockwise (right)
            var clockwise = angle; // now + is to the right
            if(clockwise < 0f) clockwise += 360f; // 0..360

            // 8 sectors of 45°
            var sector = Mathf.RoundToInt(clockwise / 45f) % 8;
            // 0 = front
            // 1 = front-right
            // 2 = right
            // 3 = back-right
            // 4 = back
            // 5 = back-left
            // 6 = left
            // 7 = front-left

            intensity = Mathf.Clamp01(intensity);
            TriggerIndicator(sector, intensity);
        }

        private void TriggerIndicator(int index, float intensity) {
            if(index < 0 || index >= _indicators.Length) return;
            var ve = _indicators[index];
            if(ve == null) return;

            if(_runningCoroutines[index] != null) {
                StopCoroutine(_runningCoroutines[index]);
            }

            _runningCoroutines[index] = StartCoroutine(FlashRoutine(index, ve, intensity));
        }

        private IEnumerator FlashRoutine(int index, VisualElement ve, float intensity) {
            var targetAlpha = maxAlpha * intensity;

            // pop to full
            ve.style.opacity = targetAlpha;

            var t = 0f;
            while(t < flashDuration) {
                t += Time.deltaTime;
                yield return null;
            }

            // fade out
            t = 0f;
            while(t < fadeDuration) {
                t += Time.deltaTime;
                var f = 1f - (t / fadeDuration);
                ve.style.opacity = targetAlpha * f;
                yield return null;
            }

            ve.style.opacity = 0f;
            _runningCoroutines[index] = null;
        }
    }
}