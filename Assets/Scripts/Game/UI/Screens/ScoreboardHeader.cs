using System.Collections.Generic;
using Game.Match;
using UnityEngine;
using UnityEngine.UIElements;
using SessionManager = Network.Session.SessionManager;

namespace Game.UI.Screens {
    /// <summary>Scoreboard title/map resolution and header column labels (Tag vs normal mode).</summary>
    internal sealed class ScoreboardHeader {
        private readonly VisualElement _root;
        private readonly Label _scoreboardTitle;
        private readonly Label _scoreboardMapTitle;
        private readonly Label _tdmScoreboardTitle;
        private readonly Label _tdmScoreboardMapTitle;
        private readonly Object _logContext;
        private VisualElement _headerElement;
        private List<Label> _headerLabels;
        private bool _headerLabelsValid;
        private bool _missingGamemodeTitleLogged;

        public ScoreboardHeader(VisualElement root, Object logContext = null) {
            _root = root;
            _logContext = logContext;
            _scoreboardTitle = root?.Q<Label>("scoreboard-title");
            _scoreboardMapTitle = root?.Q<Label>("scoreboard-map-title");
            _tdmScoreboardTitle = root?.Q<Label>("tdm-scoreboard-title");
            _tdmScoreboardMapTitle = root?.Q<Label>("tdm-scoreboard-map-title");
        }

        public void UpdateTitles(MatchSettingsManager matchSettings, string sceneName) {
            var gamemodeName = ResolveScoreboardTitle(matchSettings);
            var mapName = ResolveScoreboardMapTitle(sceneName);
            if(_scoreboardTitle != null) _scoreboardTitle.text = gamemodeName;
            if(_scoreboardMapTitle != null) _scoreboardMapTitle.text = mapName;
            if(_tdmScoreboardTitle != null) _tdmScoreboardTitle.text = gamemodeName;
            if(_tdmScoreboardMapTitle != null) _tdmScoreboardMapTitle.text = mapName;
        }

        public void UpdateHeaderColumns(bool isTagMode) {
            if(_headerElement == null || !_headerLabelsValid) {
                _headerElement = _root?.Q<VisualElement>("scoreboard-header");
                if(_headerElement != null) {
                    _headerLabels = _headerElement.Query<Label>().ToList();
                    _headerLabelsValid = true;
                } else {
                    return;
                }
            }

            if(_headerLabels == null) return;

            if(isTagMode) {
                foreach(var label in _headerLabels) {
                    var text = label.text;
                    switch(text) {
                        case "K":
                            label.text = "TT";
                            break;
                        case "D":
                            label.text = "Tags";
                            break;
                        case "A":
                            label.text = "Tagged";
                            break;
                        case "KDR":
                            label.text = "TTR";
                            break;
                        case "HS%":
                        case "DMG":
                            label.style.display = DisplayStyle.None;
                            break;
                    }
                }
            } else {
                foreach(var label in _headerLabels) {
                    var text = label.text;
                    label.text = text switch {
                        "TT" => "K",
                        "Tags" => "D",
                        "Tagged" => "A",
                        "TTR" => "KDR",
                        _ => label.text
                    };
                    label.style.display = DisplayStyle.Flex;
                }
            }
        }

        public void InvalidateHeaderCache() {
            _headerLabelsValid = false;
        }

        private string ResolveScoreboardTitle(MatchSettingsManager matchSettings) {
            if(matchSettings == null) {
                if(_missingGamemodeTitleLogged) return "UNKNOWN MODE";
                Debug.LogError(
                    "[ScoreboardManager] MatchSettingsManager.Instance is null while updating scoreboard title.",
                    _logContext);
                _missingGamemodeTitleLogged = true;
                return "UNKNOWN MODE";
            }

            var id = matchSettings.selectedGameModeId;
            if(string.IsNullOrEmpty(id)) {
                if(_missingGamemodeTitleLogged) return "UNKNOWN MODE";
                Debug.LogError("[ScoreboardManager] selectedGameModeId is empty while updating scoreboard title.",
                    _logContext);
                _missingGamemodeTitleLogged = true;
                return "UNKNOWN MODE";
            }

            _missingGamemodeTitleLogged = false;
            return id.ToUpperInvariant();
        }

        private static string ResolveScoreboardMapTitle(string sceneName) {
            var sessionManager = SessionManager.Instance;
            if(sessionManager != null && !string.IsNullOrWhiteSpace(sessionManager.SelectedMapId))
                return FormatMapTitle(sessionManager.SelectedMapId);
            if(TryResolveMapIdFromScene(sceneName, out var mapId))
                return FormatMapTitle(mapId);
            return !string.IsNullOrWhiteSpace(sceneName) ? FormatMapTitle(sceneName) : "UNKNOWN MAP";
        }

        private static bool TryResolveMapIdFromScene(string sceneName, out string mapId) {
            mapId = string.Empty;
            if(string.IsNullOrWhiteSpace(sceneName)) return false;
            var pool = Resources.Load<MapPoolDefinition>("MatchMapPoolDefinition");
            if(pool == null || pool.Maps == null) return false;
            foreach(var map in pool.Maps) {
                if(map == null || string.IsNullOrWhiteSpace(map.SceneName)) continue;
                if(!string.Equals(map.SceneName, sceneName, System.StringComparison.OrdinalIgnoreCase)) continue;
                mapId = string.IsNullOrWhiteSpace(map.MapId) ? map.name : map.MapId;
                return !string.IsNullOrWhiteSpace(mapId);
            }

            return false;
        }

        private static string FormatMapTitle(string value) {
            return string.IsNullOrWhiteSpace(value) ? "UNKNOWN MAP" : value.Trim().ToUpperInvariant();
        }
    }
}