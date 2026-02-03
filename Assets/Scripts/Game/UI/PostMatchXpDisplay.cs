using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;
using Game.Progression;

namespace Game.UI {
    public class PostMatchXpDisplay : MonoBehaviour {
        [SerializeField] private UIDocument uiDocument;
        
        private VisualElement _root;
        private VisualElement _xpContainer;
        private ProgressBar _xpBar;
        private Label _levelLabel;
        private Label _xpGainedLabel;

        private void Awake() {
            if (uiDocument == null) uiDocument = GetComponent<UIDocument>();
        }

        private void Start() {
            if(uiDocument == null) return;
            _root = uiDocument.rootVisualElement;
            CreateXpBarUI();
        }

        private void CreateXpBarUI() {
            if (_root == null) return;

            // Create Container
            _xpContainer = new VisualElement {
                name = "xp-postmatch-container",
                style = {
                    position = Position.Absolute,
                    bottom = 100, // Positioned near bottom
                    width = Length.Percent(40),
                    left = Length.Percent(30),
                    backgroundColor = new StyleColor(new Color(0, 0, 0, 0.7f)),
                    paddingTop = 10,
                    paddingBottom = 10,
                    paddingLeft = 20,
                    paddingRight = 20,
                    borderTopLeftRadius = 10,
                    borderTopRightRadius = 10,
                    borderBottomLeftRadius = 10,
                    borderBottomRightRadius = 10,
                    display = DisplayStyle.None // Hidden by default
                }
            };

            // Level Label
            _levelLabel = new Label("LEVEL 1") {
                style = {
                    color = Color.white,
                    fontSize = 24,
                    unityFontStyleAndWeight = FontStyle.Bold,
                    alignSelf = Align.Center,
                    marginBottom = 5
                }
            };
            _xpContainer.Add(_levelLabel);

            // Progress Bar
            _xpBar = new ProgressBar {
                style = {
                    height = 30
                }
            };
            // Style the progress bar fill (this might require USS for deep customization, using basic color for now)
            // Unity Default ProgressBar is tricky to style just via code without querying children.
            // We'll trust default for now or add a class.
            _xpContainer.Add(_xpBar);

            // Gained Label
            _xpGainedLabel = new Label("+0 XP") {
                style = {
                    color = new StyleColor(new Color(0.2f, 1f, 0.2f)), // Green
                    fontSize = 20,
                    alignSelf = Align.Center,
                    marginTop = 5
                }
            };
            _xpContainer.Add(_xpGainedLabel);

            _root.Add(_xpContainer);
        }

        public void ShowXp(int oldLevel, int oldXp, int currentLevel, int currentXp, int xpGained, int nextLevelXp) {
            if (_xpContainer == null) return;

            _xpContainer.style.display = DisplayStyle.Flex;
            
            // Set Initial State (Old State)
            // If we leveled up, handling the bar animation is tricky.
            // Simplified: Show final state for now, optimize animation later.
            // User requested "filling up".

            StartCoroutine(AnimateXp(oldLevel, oldXp, currentLevel, currentXp, xpGained, nextLevelXp));
        }

        private IEnumerator AnimateXp(int startLevel, int startXp, int endLevel, int endXp, int gained, int nextLevelXp) {
            _levelLabel.text = $"LEVEL {startLevel}";
            _xpGainedLabel.text = $"+{gained} XP";
            
            // Calculate progress 0-1
            // Assumption: startXP and endXP are Total XP? Or generic?
            // ProgressionManager stores CurrentXP (current level progress) and TotalXP.
            // Let's assume passed values are CurrentXP relative to Level.
            
            // Note: If we leveled up, startXP would be high, endXP low.
            // Need max XP for startLevel.
            // We need ProgressionManager to tell us MaxXP for levels.
            
            var maxXp = ProgressionManager.Instance.GetXpRequiredForLevel(startLevel);
            
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
                var maxXpNew = ProgressionManager.Instance.GetXpRequiredForLevel(endLevel);
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
        }

        public void Hide() {
             if (_xpContainer != null) _xpContainer.style.display = DisplayStyle.None;
        }
    }
}
