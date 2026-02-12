using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Game.Progression;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Game.UI {
    public static class ChallengeUiRenderer {
        private sealed class ChallengeRowBinding {
            public VisualElement Root;
            public Label DescriptionLabel;
            public Label XpLabel;
            public ProgressBar ProgressBar;
        }

        private sealed class ChallengeListRenderState {
            public bool Initialized;
            public readonly List<ChallengeRowBinding> Rows = new();
            public Label EmptyLabel;
        }

        private static readonly ConditionalWeakTable<VisualElement, ChallengeListRenderState> ListRenderStates = new();

        public static VisualElement CreateChallengeCard(
            VisualTreeAsset cardTemplate,
            string title,
            ref bool templateErrorLogged,
            Object context,
            out Label timerLabel) {
            timerLabel = null;

            if(cardTemplate == null) {
                if(templateErrorLogged) return null;
                Debug.LogError(
                    "[ChallengeUiRenderer] challengeCardTemplate is required. Assign ChallengeCard.uxml in the inspector.",
                    context);
                templateErrorLogged = true;
                return null;
            }

            var card = cardTemplate.CloneTree();
            var titleLabel = card.Q<Label>("title-label");
            timerLabel = card.Q<Label>("timer-label");
            var listContainer = card.Q<VisualElement>("challenge-list");

            if(titleLabel == null || timerLabel == null || listContainer == null) {
                if(templateErrorLogged) return null;
                Debug.LogError(
                    "[ChallengeUiRenderer] ChallengeCard template is missing required elements: title-label, timer-label, or challenge-list.",
                    context);
                templateErrorLogged = true;
                return null;
            }

            titleLabel.text = title;
            timerLabel.text = "--:--:--";
            timerLabel.style.display = DisplayStyle.Flex;
            SetOfflineState(card, false);
            return card;
        }

        public static void SetOfflineState(VisualElement card, bool isOffline) {
            if(card == null) return;

            var separatorContainer = card.Q<VisualElement>("separator-container");
            var challengeList = card.Q<VisualElement>("challenge-list");
            var offlinePlaceholder = card.Q<VisualElement>("offline-placeholder");

            if(separatorContainer != null) {
                separatorContainer.style.display = DisplayStyle.Flex;
            }

            if(challengeList != null) {
                challengeList.EnableInClassList("hidden", isOffline);
            }

            if(offlinePlaceholder != null) {
                offlinePlaceholder.EnableInClassList("hidden", !isOffline);
            }
        }

        public static void SetOfflineTimer(Label timerLabel) {
            if(timerLabel == null) return;
            timerLabel.text = "Offline...";
            timerLabel.RemoveFromClassList("challenge-card-timer--long");
        }

        public static void SetDailyResetTimer(Label timerLabel, TimeSpan timeUntilReset) {
            if(timerLabel == null) return;
            timerLabel.text = $"{timeUntilReset.Hours:D2}:{timeUntilReset.Minutes:D2}:{timeUntilReset.Seconds:D2}";
            timerLabel.RemoveFromClassList("challenge-card-timer--long");
        }

        public static void SetWeeklyResetTimer(Label timerLabel, TimeSpan timeUntilReset) {
            if(timerLabel == null) return;
            if(timeUntilReset.TotalDays >= 1) {
                timerLabel.text = $"{(int)timeUntilReset.TotalDays} days remaining";
                timerLabel.AddToClassList("challenge-card-timer--long");
            } else {
                timerLabel.text =
                    $"{(int)timeUntilReset.TotalHours:D2}:{timeUntilReset.Minutes:D2}:{timeUntilReset.Seconds:D2}";
                timerLabel.RemoveFromClassList("challenge-card-timer--long");
            }
        }

        public static void RenderChallengeList(VisualElement listContainer,
            List<ActiveChallengeData> challenges,
            VisualTreeAsset rowTemplate,
            ProgressionManager progressionManager,
            ref bool templateErrorLogged,
            Object context,
            bool showEmptyLabel,
            bool includeXpSuffix,
            Action<ProgressBar> progressBarStyler = null) {
            if(listContainer == null) return;
            var state = ListRenderStates.GetValue(listContainer, _ => new ChallengeListRenderState());
            if(!state.Initialized) {
                // Reset unknown tree content once, then keep stable row instances.
                listContainer.Clear();
                state.Rows.Clear();
                state.EmptyLabel = null;
                state.Initialized = true;
            }

            if(challenges == null || challenges.Count == 0) {
                HideAllRows(state);
                ToggleEmptyLabel(state, listContainer, showEmptyLabel);

                return;
            }

            if(rowTemplate == null) {
                if(!templateErrorLogged) {
                    Debug.LogError("[ChallengeUiRenderer] challengeRowTemplate is required. Assign ChallengeRow.uxml in the inspector.",
                        context);
                    templateErrorLogged = true;
                }
                HideAllRows(state);
                ToggleEmptyLabel(state, listContainer, false);

                return;
            }

            if(progressionManager == null) {
                Debug.LogWarning("[ChallengeUiRenderer] ProgressionManager is null.");
                HideAllRows(state);
                ToggleEmptyLabel(state, listContainer, false);
                return;
            }

            var visibleRowCount = 0;
            foreach(var activeChallenge in challenges) {
                var def = progressionManager.GetChallengeDefinition(activeChallenge.challengeID);
                if(def == null) continue;

                var row = EnsureRowBinding(state, visibleRowCount, rowTemplate, listContainer, ref templateErrorLogged, context);
                if(row == null) return;
                if(row.DescriptionLabel == null || row.XpLabel == null || row.ProgressBar == null) {
                    if(templateErrorLogged) return;
                    Debug.LogError(
                        "[ChallengeUiRenderer] ChallengeRow template is missing required elements: description-label, xp-label, or progress-bar.",
                        context);
                    templateErrorLogged = true;

                    return;
                }

                var progress = activeChallenge.currentProgress;
                var target = activeChallenge.targetProgress;
                if(progress > target) progress = target;

                var descText = BuildDescriptionText(def, activeChallenge, target);
                row.DescriptionLabel.text = $"{descText} ({progress}/{target})";
                row.XpLabel.text = includeXpSuffix ? $"+{activeChallenge.xpReward} XP" : $"+{activeChallenge.xpReward}";

                row.ProgressBar.lowValue = 0;
                row.ProgressBar.highValue = target;
                row.ProgressBar.value = progress;
                progressBarStyler?.Invoke(row.ProgressBar);

                row.Root.style.display = DisplayStyle.Flex;
                visibleRowCount++;
            }

            for(var i = visibleRowCount; i < state.Rows.Count; i++) {
                var row = state.Rows[i];
                if(row?.Root != null) {
                    row.Root.style.display = DisplayStyle.None;
                }
            }

            ToggleEmptyLabel(state, listContainer, showEmptyLabel && visibleRowCount == 0);
        }

        private static ChallengeRowBinding EnsureRowBinding(
            ChallengeListRenderState state,
            int index,
            VisualTreeAsset rowTemplate,
            VisualElement listContainer,
            ref bool templateErrorLogged,
            Object context) {
            while(index >= state.Rows.Count) {
                var root = rowTemplate.CloneTree();
                var binding = new ChallengeRowBinding {
                    Root = root,
                    DescriptionLabel = root.Q<Label>("description-label"),
                    XpLabel = root.Q<Label>("xp-label"),
                    ProgressBar = root.Q<ProgressBar>("progress-bar")
                };

                if(binding.DescriptionLabel == null || binding.XpLabel == null || binding.ProgressBar == null) {
                    if(!templateErrorLogged) {
                        Debug.LogError(
                            "[ChallengeUiRenderer] ChallengeRow template is missing required elements: description-label, xp-label, or progress-bar.",
                            context);
                        templateErrorLogged = true;
                    }
                    return null;
                }

                state.Rows.Add(binding);
                listContainer.Add(root);
            }

            var row = state.Rows[index];
            if(row?.Root != null && row.Root.parent != listContainer) {
                listContainer.Add(row.Root);
            }
            return row;
        }

        private static void HideAllRows(ChallengeListRenderState state) {
            foreach(var row in state.Rows) {
                if(row?.Root != null) {
                    row.Root.style.display = DisplayStyle.None;
                }
            }
        }

        private static void ToggleEmptyLabel(ChallengeListRenderState state, VisualElement listContainer, bool show) {
            if(show) {
                if(state.EmptyLabel == null) {
                    state.EmptyLabel = new Label("No challenges available");
                    state.EmptyLabel.AddToClassList("challenge-empty-label");
                }

                if(state.EmptyLabel.parent != listContainer) {
                    listContainer.Add(state.EmptyLabel);
                }

                state.EmptyLabel.style.display = DisplayStyle.Flex;
                return;
            }

            if(state.EmptyLabel?.parent != listContainer) return;
            if(state.EmptyLabel != null) state.EmptyLabel.style.display = DisplayStyle.None;
        }

        private static string BuildDescriptionText(ChallengeDefinition def, ActiveChallengeData activeChallenge, int target) {
            var filterToUse = !string.IsNullOrEmpty(activeChallenge.filterID) ? activeChallenge.filterID : def.weaponID;
            var displayFilter = ProgressionManager.GetFilterDisplayName(filterToUse);
            if(string.IsNullOrEmpty(displayFilter)) {
                displayFilter = def.type switch {
                    ChallengeType.MatchesPlayed => "Any Mode",
                    ChallengeType.WeaponKill => "Any Weapon",
                    _ => "Unknown"
                };
            }

            try {
                return string.Format(def.Description, target, displayFilter);
            } catch(FormatException ex) {
                Debug.LogError(
                    $"[ChallengeUiRenderer] Invalid challenge description format for `{def.id}`: " +
                    $"\"{def.Description}\" ({ex.Message})");
                if(!string.IsNullOrEmpty(def.Description)) {
                    return def.Description
                        .Replace("{0}", target.ToString())
                        .Replace("{1}", displayFilter);
                }
                return string.Empty;
            }
        }
    }
}
