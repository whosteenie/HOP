using System.Collections;
using System.Collections.Generic;
using Events;
using Game.UI.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI.HUD {
    public class DamageVignetteUIManager : UIElementBase {
        [Header("Timing")]
        [SerializeField] private float flashDuration = 0.12f; // time at full alpha
        [SerializeField] private float fadeDuration = 0.3f; // fade-out
        [SerializeField] private float maxAlpha = 0.8f;

        // Order: 0=Front,1=FrontRight,2=Right,3=BackRight,4=Back,5=BackLeft,6=Left,7=FrontLeft
        private VisualElement[] _indicators;
        private Coroutine[] _runningCoroutines;

        protected override void Awake() {
            if(uiDocument == null) {
                uiDocument = GetComponent<UIDocument>();
            }
            base.Awake();
        }

        protected override void OnInitialize() {
            var container = QRequired<VisualElement>("damage-vignette-root");

            _indicators = new[] {
                container.Q<VisualElement>("hit-front"),
                container.Q<VisualElement>("hit-frontRight"),
                container.Q<VisualElement>("hit-right"),
                container.Q<VisualElement>("hit-backRight"),
                container.Q<VisualElement>("hit-back"),
                container.Q<VisualElement>("hit-backLeft"),
                container.Q<VisualElement>("hit-left"),
                container.Q<VisualElement>("hit-frontLeft")
            };

            _runningCoroutines = new Coroutine[_indicators.Length];

            // Start hidden
            foreach(var t in _indicators) {
                if(t != null)
                    t.style.opacity = 0f;
            }

            EventBus.Unsubscribe<ShowDamageVignetteFromWorldHitEvent>(OnShowDamageVignetteFromWorldHit);
            EventBus.Subscribe<ShowDamageVignetteFromWorldHitEvent>(OnShowDamageVignetteFromWorldHit);
        }

        protected override Dictionary<string, System.Type> GetRequiredElements() {
            return new Dictionary<string, System.Type> {
                { "damage-vignette-root", typeof(VisualElement) }
            };
        }

        protected override void OnCleanup() {
            EventBus.Unsubscribe<ShowDamageVignetteFromWorldHitEvent>(OnShowDamageVignetteFromWorldHit);
            base.OnCleanup();
        }

        private void OnShowDamageVignetteFromWorldHit(ShowDamageVignetteFromWorldHitEvent evt) {
            if(evt == null || _indicators == null) return;

            var toHit = evt.WorldHitPos - evt.CameraPosition;
            var flatDir = Vector3.ProjectOnPlane(toHit, Vector3.up);
            if(flatDir.sqrMagnitude < 0.0001f) return;
            flatDir.Normalize();

            var cameraForward = Vector3.ProjectOnPlane(evt.CameraForward, Vector3.up);
            if(cameraForward.sqrMagnitude < 0.0001f) return;
            cameraForward.Normalize();

            var angle = Vector3.SignedAngle(cameraForward, flatDir, Vector3.up);
            var clockwise = angle;
            if(clockwise < 0f) clockwise += 360f;

            var sector = Mathf.RoundToInt(clockwise / 45f) % 8;
            TriggerIndicator(sector, Mathf.Clamp01(evt.Intensity));
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
                var f = 1f - t / fadeDuration;
                ve.style.opacity = targetAlpha * f;
                yield return null;
            }

            ve.style.opacity = 0f;
            _runningCoroutines[index] = null;
        }
    }
}
