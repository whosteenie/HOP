using System;
using System.Collections.Generic;
using Game.Progression;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI {
    public static class ChallengeUiRenderer {
        public static VisualElement CreateChallengeCard(
            VisualTreeAsset cardTemplate,
            string title,
            ref bool templateErrorLogged,
            UnityEngine.Object context,
            out Label timerLabel) {
            timerLabel = null;

            if(cardTemplate == null) {
                if(!templateErrorLogged) {
                    Debug.LogError(
                        "[ChallengeUiRenderer] challengeCardTemplate is required. Assign ChallengeCard.uxml in the inspector.",
                        context);
                    templateErrorLogged = true;
                }
                return null;
            }

            var card = cardTemplate.CloneTree();
            var titleLabel = card.Q<Label>("title-label");
            timerLabel = card.Q<Label>("timer-label");
            var listContainer = card.Q<VisualElement>("challenge-list");

            if(titleLabel == null || timerLabel == null || listContainer == null) {
                if(!templateErrorLogged) {
                    Debug.LogError(
                        "[ChallengeUiRenderer] ChallengeCard template is missing required elements: title-label, timer-label, or challenge-list.",
                        context);
                    templateErrorLogged = true;
                }
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

        public static bool RenderChallengeList(
            VisualElement listContainer,
            List<ActiveChallengeData> challenges,
            VisualTreeAsset rowTemplate,
            ProgressionManager progressionManager,
            ref bool templateErrorLogged,
            UnityEngine.Object context,
            bool showEmptyLabel,
            bool includeXpSuffix,
            Action<ProgressBar> progressBarStyler = null) {
            if(listContainer == null) return false;

            listContainer.Clear();

            if(challenges == null || challenges.Count == 0) {
                if(showEmptyLabel) {
                    var emptyLabel = new Label("No challenges available");
                    emptyLabel.AddToClassList("challenge-empty-label");
                    listContainer.Add(emptyLabel);
                }
                return true;
            }

            if(rowTemplate == null) {
                if(!templateErrorLogged) {
                    Debug.LogError("[ChallengeUiRenderer] challengeRowTemplate is required. Assign ChallengeRow.uxml in the inspector.",
                        context);
                    templateErrorLogged = true;
                }
                return false;
            }

            if(progressionManager == null) {
                Debug.LogWarning("[ChallengeUiRenderer] ProgressionManager is null.");
                return false;
            }

            foreach(var activeChallenge in challenges) {
                var def = progressionManager.GetChallengeDefinition(activeChallenge.challengeID);
                if(def == null) continue;

                var row = rowTemplate.CloneTree();
                var descriptionLabel = row.Q<Label>("description-label");
                var xpLabel = row.Q<Label>("xp-label");
                var progressBar = row.Q<ProgressBar>("progress-bar");

                if(descriptionLabel == null || xpLabel == null || progressBar == null) {
                    if(!templateErrorLogged) {
                        Debug.LogError(
                            "[ChallengeUiRenderer] ChallengeRow template is missing required elements: description-label, xp-label, or progress-bar.",
                            context);
                        templateErrorLogged = true;
                    }
                    return false;
                }

                var progress = activeChallenge.currentProgress;
                var target = activeChallenge.targetProgress;
                if(progress > target) progress = target;

                var descText = BuildDescriptionText(def, activeChallenge, target, progressionManager);
                descriptionLabel.text = $"{descText} ({progress}/{target})";
                xpLabel.text = includeXpSuffix ? $"+{activeChallenge.xpReward} XP" : $"+{activeChallenge.xpReward}";

                progressBar.lowValue = 0;
                progressBar.highValue = target;
                progressBar.value = progress;
                progressBarStyler?.Invoke(progressBar);

                listContainer.Add(row);
            }

            return true;
        }

        private static string BuildDescriptionText(
            ChallengeDefinition def,
            ActiveChallengeData activeChallenge,
            int target,
            ProgressionManager progressionManager) {
            var descText = def.Description;
            var filterToUse = !string.IsNullOrEmpty(activeChallenge.filterID) ? activeChallenge.filterID : def.weaponID;
            var displayFilter = progressionManager.GetFilterDisplayName(filterToUse);
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
