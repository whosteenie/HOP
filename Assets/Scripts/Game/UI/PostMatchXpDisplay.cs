using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using Game.Progression;

namespace Game.UI {
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

        protected override void OnInitialize() {
            _xpContainer = QRequired<VisualElement>("xp-postmatch-container");
            _levelLabel = QRequired<Label>("level-label");
            _xpBar = QRequired<ProgressBar>("xp-bar");
            _xpGainedLabel = QRequired<Label>("xp-gained-label");
            if(_xpContainer != null) {
                _xpContainer.style.display = DisplayStyle.None;
                _xpContainer.AddToClassList("hidden");
            }
        }

        protected override Dictionary<string, System.Type> GetRequiredElements() {
            return new Dictionary<string, System.Type> {
                { "xp-postmatch-container", typeof(VisualElement) },
                { "level-label", typeof(Label) },
                { "xp-bar", typeof(ProgressBar) },
                { "xp-gained-label", typeof(Label) }
            };
        }

        public void ShowXp(int oldLevel, int oldXp, int currentLevel, int currentXp, int xpGained, int nextLevelXp) {
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

            _xpAnimationRoutine = StartCoroutine(AnimateXp(oldLevel, oldXp, currentLevel, currentXp, xpGained, nextLevelXp));
        }

        private IEnumerator AnimateXp(int startLevel, int startXp, int endLevel, int endXp, int gained, int nextLevelXp) {
            var progression = ProgressionManager.Instance;
            if(progression == null) {
                Debug.LogWarning("[PostMatchXpDisplay] ProgressionManager is null; cannot animate XP display.");
                _xpAnimationRoutine = null;
                yield break;
            }

            _levelLabel.text = $"LEVEL {startLevel}";
            _xpGainedLabel.text = $"+{gained} XP";

            var maxXp = progression.GetXpRequiredForLevel(startLevel);
            
            _xpBar.lowValue = 0;
            _xpBar.highValue = maxXp;
            _xpBar.value = startXp;

            yield return new WaitForSeconds(1.0f); // Wait before animating

            const float duration = 2.0f;
            var elapsed = 0f;

            // Simple case: No level up
            if (startLevel == endLevel) {
                while (elapsed < duration) {
                    elapsed += Time.deltaTime;
                    var t = elapsed / duration;
                    _xpBar.value = Mathf.Lerp(startXp, endXp, t);
                    yield return null;
                }
            } 
            else {
                // Leveled Up Case
                // 1. Fill to Max
                const float firstLeg = duration / 2f;
                elapsed = 0f;
                while (elapsed < firstLeg) {
                    elapsed += Time.deltaTime;
                    var t = elapsed / firstLeg;
                    _xpBar.value = Mathf.Lerp(startXp, maxXp, t);
                    yield return null;
                }
                
                // 2. Level Up Visuals
                _levelLabel.text = $"LEVEL {endLevel}";
                // Boom/Flash effect here?

                // 3. Fill Remainder
                var maxXpNew = progression.GetXpRequiredForLevel(endLevel);
                _xpBar.highValue = maxXpNew;
                _xpBar.value = 0;
                
                elapsed = 0f;
                while (elapsed < firstLeg) {
                    elapsed += Time.deltaTime;
                    var t = elapsed / firstLeg;
                    _xpBar.value = Mathf.Lerp(0, endXp, t);
                    yield return null;
                }
            }

            _xpBar.value = endXp;
            _xpAnimationRoutine = null;
        }

        public void Hide() {
            if(_xpAnimationRoutine != null) {
                StopCoroutine(_xpAnimationRoutine);
                _xpAnimationRoutine = null;
            }

            if(_xpContainer != null) {
                _xpContainer.style.display = DisplayStyle.None;
                _xpContainer.AddToClassList("hidden");
            }
        }
    }
}
