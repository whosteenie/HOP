using System.Collections;
using System.Collections.Generic;
using Diagnostics;
using Events;
using Game.Progression;
using Game.UI.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI.Misc {
    public class PostMatchXpDisplay : UIElementBase {
        private VisualElement _xpContainer;
        private ProgressBar _xpBar;
        private Label _levelLabel;
        private Label _xpGainedLabel;
        private Coroutine _xpAnimationRoutine;

        protected override void Awake() {
            base.Awake();
            if(uiDocument == null) uiDocument = GetComponent<UIDocument>();
        }

        protected void OnEnable() {
            EventBus.Subscribe<ShowPostMatchXpEvent>(OnShowPostMatchXp);
            EventBus.Subscribe<HidePostMatchXpEvent>(OnHidePostMatchXp);
        }

        protected void OnDisable() {
            EventBus.Unsubscribe<ShowPostMatchXpEvent>(OnShowPostMatchXp);
            EventBus.Unsubscribe<HidePostMatchXpEvent>(OnHidePostMatchXp);
        }

        protected override void Start() {
            // The shared game UIDocument can still be wiring up on this component's Start.
            // Skip eager base initialization and rely on lazy Initialize() from ShowXp().
            if(uiDocument == null || uiDocument.rootVisualElement == null) return;
            base.Start();
        }

        protected override void OnInitialize() {
            _xpContainer = QRequired<VisualElement>("xp-postmatch-container");
            _levelLabel = QRequired<Label>("level-label");
            _xpBar = QRequired<ProgressBar>("xp-bar");
            _xpGainedLabel = QRequired<Label>("xp-gained-label");
            if(_xpContainer == null) return;
            _xpContainer.style.display = DisplayStyle.None;
            _xpContainer.AddToClassList("hidden");
        }

        protected override Dictionary<string, System.Type> GetRequiredElements() {
            return new Dictionary<string, System.Type> {
                { "xp-postmatch-container", typeof(VisualElement) },
                { "level-label", typeof(Label) },
                { "xp-bar", typeof(ProgressBar) },
                { "xp-gained-label", typeof(Label) }
            };
        }

        private void ShowXp(int oldLevel, int oldXp, int currentLevel, int currentXp, int xpGained) {
            if(!IsInitialized) {
                Initialize();
            }
            if(_xpContainer == null || _xpBar == null || _levelLabel == null || _xpGainedLabel == null) return;

            _xpContainer.style.display = DisplayStyle.Flex;
            _xpContainer.RemoveFromClassList("hidden");

            if(_xpAnimationRoutine != null) {
                StopCoroutine(_xpAnimationRoutine);
                _xpAnimationRoutine = null;
            }

            _xpAnimationRoutine = StartCoroutine(AnimateXp(oldLevel, oldXp, currentLevel, currentXp, xpGained));
        }

        private void OnShowPostMatchXp(ShowPostMatchXpEvent evt) {
            if(evt == null) return;
            ShowXp(evt.OldLevel, evt.OldXp, evt.CurrentLevel, evt.CurrentXp, evt.XpGained);
        }

        private void OnHidePostMatchXp(HidePostMatchXpEvent _) {
            Hide();
        }

        private IEnumerator AnimateXp(int startLevel, int startXp, int endLevel, int endXp, int gained) {
            var progression = ProgressionManager.Instance;
            if(progression == null) {
                DevLog.LogWarning("[PostMatchXpDisplay] ProgressionManager is null; cannot animate XP display.");
                _xpAnimationRoutine = null;
                yield break;
            }

            _levelLabel.text = $"LEVEL {startLevel}";
            _xpGainedLabel.text = $"+{gained} XP";

            var maxXp = progression.GetXpForLevel(startLevel);
            
            _xpBar.lowValue = 0;
            _xpBar.highValue = maxXp;
            _xpBar.value = startXp;

            yield return new WaitForSeconds(1.0f); // Wait before animating

            const float totalDuration = 2.0f;

            if(startLevel == endLevel) {
                yield return AnimateXpSegment(startXp, endXp, totalDuration);
            } else {
                var levelSpan = Mathf.Max(1, endLevel - startLevel);
                var segmentDuration = totalDuration / (levelSpan + 1f);
                var currentLevel = startLevel;
                var currentXp = startXp;

                while(currentLevel < endLevel) {
                    var requiredXpForLevel = progression.GetXpForLevel(currentLevel);
                    _xpBar.highValue = requiredXpForLevel;
                    _levelLabel.text = $"LEVEL {currentLevel}";
                    yield return AnimateXpSegment(currentXp, requiredXpForLevel, segmentDuration);

                    currentLevel++;
                    currentXp = 0;
                    _levelLabel.text = $"LEVEL {currentLevel}";
                    _xpBar.highValue = progression.GetXpForLevel(currentLevel);
                    _xpBar.value = 0;
                    yield return null;
                }

                _xpBar.highValue = progression.GetXpForLevel(endLevel);
                _levelLabel.text = $"LEVEL {endLevel}";
                yield return AnimateXpSegment(0, endXp, segmentDuration);
            }

            _xpBar.value = endXp;
            _xpBar.highValue = progression.GetXpForLevel(endLevel);
            _levelLabel.text = $"LEVEL {endLevel}";
            _xpAnimationRoutine = null;
        }

        private IEnumerator AnimateXpSegment(float fromXp, float toXp, float duration) {
            if(duration <= 0f) {
                _xpBar.value = toXp;
                yield break;
            }

            var elapsed = 0f;
            while(elapsed < duration) {
                elapsed += Time.deltaTime;
                var t = Mathf.Clamp01(elapsed / duration);
                _xpBar.value = Mathf.Lerp(fromXp, toXp, t);
                yield return null;
            }

            _xpBar.value = toXp;
        }

        private void Hide() {
            if(_xpAnimationRoutine != null) {
                StopCoroutine(_xpAnimationRoutine);
                _xpAnimationRoutine = null;
            }

            if(_xpContainer == null) return;
            _xpContainer.style.display = DisplayStyle.None;
            _xpContainer.AddToClassList("hidden");
        }
    }
}
