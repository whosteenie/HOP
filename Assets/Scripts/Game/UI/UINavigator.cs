using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI {
    /// <summary>
    /// Lightweight screen navigator that centralizes showing/hiding VisualElement panels,
    /// with optional cross-fade transitions. Designed to wrap existing panel logic in
    /// managers like MainMenuManager and GameMenuManager without requiring UXML changes.
    /// </summary>
    public class UINavigator {
        private readonly MonoBehaviour _owner;
        private readonly List<VisualElement> _panels;
        private readonly float _fadeDuration;
        private readonly Func<VisualElement, bool> _skipFadePredicate;

        private VisualElement _currentPanel;
        private Coroutine _fadeCoroutine;

        public UINavigator(
            MonoBehaviour owner,
            IEnumerable<VisualElement> panels,
            float fadeDuration,
            Func<VisualElement, bool> skipFadePredicate = null
        ) {
            _owner = owner;
            _panels = new List<VisualElement>();

            if(panels != null) {
                foreach(var p in panels) {
                    if(p == null) continue;
                    _panels.Add(p);
                }
            }

            _fadeDuration = Mathf.Max(0f, fadeDuration);
            _skipFadePredicate = skipFadePredicate;
        }

        /// <summary>
        /// The currently visible panel tracked by this navigator.
        /// </summary>
        public VisualElement CurrentPanel => _currentPanel;

        /// <summary>
        /// Shows the given panel and hides all others managed by this navigator.
        /// Mirrors the previous MainMenuManager / GameMenuManager behavior:
        /// - First panel shown snaps in without fading.
        /// - Subsequent transitions can cross-fade, with optional per-panel fade skip.
        /// </summary>
        public void Show(VisualElement targetPanel) {
            if(targetPanel == null || _owner == null) return;

            // First-time init: hide everything except target and snap visible.
            if(_currentPanel == null) {
                foreach(var p in _panels) {
                    if(p != null && p != targetPanel) {
                        HideImmediate(p);
                    }
                }

                ShowImmediate(targetPanel);
                _currentPanel = targetPanel;
                return;
            }

            // Already showing this panel.
            if(targetPanel == _currentPanel) return;

            if(_fadeCoroutine != null) {
                _owner.StopCoroutine(_fadeCoroutine);
                _fadeCoroutine = null;
            }

            var needFadeOut = !ShouldSkipFade(_currentPanel);
            var needFadeIn = !ShouldSkipFade(targetPanel);
            var requiresFade = needFadeOut || needFadeIn;

            if(!requiresFade || _fadeDuration <= 0f) {
                HideImmediate(_currentPanel);
                ShowImmediate(targetPanel);
                _currentPanel = targetPanel;
                return;
            }

            _fadeCoroutine = _owner.StartCoroutine(FadeBetweenPanels(_currentPanel, targetPanel));
        }

        private bool ShouldSkipFade(VisualElement panel) {
            if(panel == null) return true;
            if(_skipFadePredicate == null) return false;
            return _skipFadePredicate(panel);
        }

        private void HideImmediate(VisualElement panel) {
            if(panel == null) return;
            panel.AddToClassList("hidden");
            panel.style.display = StyleKeyword.Null;
            panel.style.opacity = new StyleFloat(1f);
        }

        private void ShowImmediate(VisualElement panel) {
            if(panel == null) return;
            panel.RemoveFromClassList("hidden");
            panel.style.display = DisplayStyle.Flex;
            panel.style.opacity = new StyleFloat(1f);
            panel.BringToFront();
        }

        private System.Collections.IEnumerator FadeBetweenPanels(VisualElement oldPanel, VisualElement newPanel) {
            foreach(var p in _panels) {
                if(p == null || p == oldPanel || p == newPanel) continue;
                HideImmediate(p);
            }

            var fadeOutPanel = ShouldSkipFade(oldPanel) ? null : oldPanel;
            var fadeInPanel = ShouldSkipFade(newPanel) ? null : newPanel;

            if(fadeInPanel != null) {
                fadeInPanel.RemoveFromClassList("hidden");
                fadeInPanel.style.display = DisplayStyle.Flex;
                fadeInPanel.style.opacity = new StyleFloat(0f);
                fadeInPanel.BringToFront();
            } else if(newPanel != null) {
                ShowImmediate(newPanel);
            }

            var elapsed = 0f;
            while(elapsed < _fadeDuration) {
                elapsed += Time.deltaTime;
                var t = Mathf.Clamp01(elapsed / _fadeDuration);

                if(fadeOutPanel != null) {
                    fadeOutPanel.style.opacity = new StyleFloat(1f - t);
                }

                if(fadeInPanel != null) {
                    fadeInPanel.style.opacity = new StyleFloat(t);
                }

                yield return null;
            }

            if(fadeOutPanel != null) {
                HideImmediate(fadeOutPanel);
            }

            if(fadeInPanel != null) {
                fadeInPanel.style.opacity = new StyleFloat(1f);
                fadeInPanel.RemoveFromClassList("hidden");
                fadeInPanel.style.display = DisplayStyle.Flex;
                fadeInPanel.BringToFront();
            }

            _currentPanel = newPanel;
            _fadeCoroutine = null;
        }
    }
}

