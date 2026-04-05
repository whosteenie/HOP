using System;
using System.Collections.Generic;
using Events;
using Game.Match;
using Game.Player.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI.Screens.Scoreboard {
    /// <summary>Match timer label and compact score display (left/right values next to timer).</summary>
    internal sealed class ScoreboardTopBar {
        private readonly Label _matchTimerLabel;
        private readonly VisualElement _leftScoreContainer;
        private readonly VisualElement _rightScoreContainer;
        private readonly Label _leftScoreValue;
        private readonly Label _rightScoreValue;

        public ScoreboardTopBar(Label matchTimerLabel, VisualElement leftScoreContainer,
            VisualElement rightScoreContainer,
            Label leftScoreValue, Label rightScoreValue) {
            _matchTimerLabel = matchTimerLabel;
            _leftScoreContainer = leftScoreContainer;
            _rightScoreContainer = rightScoreContainer;
            _leftScoreValue = leftScoreValue;
            _rightScoreValue = rightScoreValue;
        }

        public void SetMatchTime(int secondsRemaining, bool playTickSfx = true) {
            if(_matchTimerLabel == null) return;
            if(secondsRemaining < 0) {
                _matchTimerLabel.text = "INFINITE";
                return;
            }

            var minutes = secondsRemaining / 60;
            var seconds = secondsRemaining % 60;
            _matchTimerLabel.text = $"{minutes:00}:{seconds:00}";
            if(!playTickSfx || minutes != 0 || seconds is > 5 or < 1) return;
            EventBus.Publish(new PlayLocalSoundIdEvent("ui.timer"));
        }

        public void ApplyInitialTimerState() {
            if(_matchTimerLabel == null) return;
            const int defaultPreMatchSeconds = 5;
            const int defaultMatchSeconds = 600;
            var matchTimer = MatchTimerManager.Instance;
            var matchSettings = MatchSettingsManager.Instance;

            if(matchTimer != null) {
                switch(matchTimer.CurrentState) {
                    case MatchLifecycleState.WaitingForPlayers or MatchLifecycleState.Countdown: {
                        var preMatchSeconds = matchTimer.PreMatchCountdownSeconds;
                        if(preMatchSeconds <= 0)
                            preMatchSeconds = matchSettings != null
                                ? matchSettings.GetPreMatchCountdownSeconds()
                                : defaultPreMatchSeconds;
                        SetMatchTime(Mathf.Max(0, preMatchSeconds), false);
                        return;
                    }
                    case MatchLifecycleState.Active when matchSettings != null && matchSettings.IsInfiniteMatchTimer():
                        SetMatchTime(-1, false);
                        return;
                }

                var activeSeconds = matchTimer.TimeRemainingSeconds;
                if(activeSeconds > 0) {
                    SetMatchTime(activeSeconds, false);
                    return;
                }

                var fallback = matchSettings != null ? matchSettings.GetMatchDurationSeconds() : defaultMatchSeconds;
                SetMatchTime(Mathf.Max(0, fallback), false);
                return;
            }

            if(matchSettings != null) {
                if(matchSettings.IsPreMatchCountdownEnabled()) {
                    SetMatchTime(Mathf.Max(0, matchSettings.GetPreMatchCountdownSeconds()), false);
                    return;
                }

                if(matchSettings.IsInfiniteMatchTimer()) {
                    SetMatchTime(-1, false);
                    return;
                }

                SetMatchTime(Mathf.Max(0, matchSettings.GetMatchDurationSeconds()), false);
                return;
            }

            SetMatchTime(defaultMatchSeconds, false);
        }

        public void UpdateScoreDisplay(ScoreboardPlayerRegistry registry, ScoreboardPlayerData playerData,
            MatchSettingsManager matchSettings, PlayerController localController) {
            if(_leftScoreContainer == null || _rightScoreContainer == null || _leftScoreValue == null ||
               _rightScoreValue == null)
                return;
            if(matchSettings == null) return;
            var isTeamBased = MatchSettingsManager.IsTeamBasedMode(matchSettings.selectedGameModeId);
            var controllers = registry.GetAllPlayers();
            if(isTeamBased)
                UpdateTeamBasedScore(controllers, matchSettings, localController);
            else
                UpdateFfaScore(controllers, playerData, matchSettings, localController);
        }

        public void ShowScoreDisplay() {
            if(_leftScoreContainer != null) _leftScoreContainer.style.display = DisplayStyle.Flex;
            if(_rightScoreContainer != null) _rightScoreContainer.style.display = DisplayStyle.Flex;
        }

        public void HideScoreDisplay() {
            if(_leftScoreContainer != null) _leftScoreContainer.style.display = DisplayStyle.None;
            if(_rightScoreContainer != null) _rightScoreContainer.style.display = DisplayStyle.None;
        }

        private void UpdateTeamBasedScore(IReadOnlyCollection<PlayerController> allControllers,
            MatchSettingsManager matchSettings, PlayerController localController) {
            if(allControllers == null) throw new ArgumentNullException(nameof(allControllers));
            if(matchSettings == null || localController == null) return;
            var localTeamMgr = localController != null ? localController.TeamManager : null;

            if(localTeamMgr == null) return;
            var localTeam = localTeamMgr.netTeam.Value;

            if(!MatchObjectiveScoreResolver.TryGetLocalizedTeamScores(matchSettings.selectedGameModeId, localTeam,
                   out var yourScore, out var enemyScore)) {
                (yourScore, enemyScore) = ScoreboardPlayerData.CalculateTeamKillScores(allControllers, localTeam);
            }

            _leftScoreValue.text = yourScore.ToString();
            _rightScoreValue.text = enemyScore.ToString();
        }

        private void UpdateFfaScore(IReadOnlyCollection<PlayerController> allControllers,
            ScoreboardPlayerData playerData,
            MatchSettingsManager matchSettings, PlayerController localController) {
            if(allControllers == null) throw new ArgumentNullException(nameof(allControllers));
            if(matchSettings == null || localController == null) return;
            var isTagMode = matchSettings.selectedGameModeId == "Gun Tag";
            var localScore = playerData.GetPlayerScore(localController, isTagMode);
            var sortedPlayers = playerData.BuildSortedScoreList(allControllers, isTagMode);
            var nextScore = 0;
            var foundNext = false;
            for(var i = 0; i < sortedPlayers.Count; i++) {
                if(sortedPlayers[i].player != localController) continue;
                nextScore = i == 0
                    ? sortedPlayers.Count > 1 ? sortedPlayers[1].score : 0
                    : sortedPlayers[0].score;
                foundNext = true;
                break;
            }
            if(!foundNext && sortedPlayers.Count > 0) nextScore = sortedPlayers[0].score;
            _leftScoreValue.text = localScore.ToString();
            _rightScoreValue.text = nextScore.ToString();
        }
    }
}
