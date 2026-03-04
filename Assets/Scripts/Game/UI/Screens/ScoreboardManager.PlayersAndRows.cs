using System;
using System.Collections.Generic;
using Game.Hopball;
using Game.Match;
using Game.Player;
using Game.Spawning;
using Network.Steam;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UIElements;
using Color = UnityEngine.Color;

namespace Game.UI {
    public partial class ScoreboardManager {
        private string GetAverageVelocityText(PlayerController player) {
            var statsCtrl = GetCachedStatsController(player);
            var avgVelocity = statsCtrl != null ? statsCtrl.averageVelocity.Value : 0f;
            return $"{avgVelocity:F1} u/s";
        }

        private int GetPlayerScore(PlayerController player, bool isTagMode) {
            if(player == null) return isTagMode ? int.MaxValue : 0;
            if(!isTagMode) return player.Kills.Value;

            var tagCtrl = GetCachedTagController(player);
            return tagCtrl != null ? tagCtrl.timeTagged.Value : int.MaxValue;
        }

        private List<PlayerController> BuildSortedPlayerList(IReadOnlyCollection<PlayerController> players,
            bool isTagMode) {
            var sortedPlayers = new List<PlayerController>();
            foreach(var player in players) {
                if(player == null || !player.IsSpawned) continue;
                sortedPlayers.Add(player);
            }

            sortedPlayers.Sort((a, b) => ComparePlayers(a, b, isTagMode));
            return sortedPlayers;
        }

        private List<(PlayerController player, int score)> BuildSortedScoreList(
            IReadOnlyCollection<PlayerController> players, bool isTagMode) {
            var list = new List<(PlayerController player, int score)>();
            foreach(var player in players) {
                if(player == null || !player.IsSpawned) continue;
                list.Add((player, GetPlayerScore(player, isTagMode)));
            }

            list.Sort((a, b) => isTagMode ? a.score.CompareTo(b.score) : b.score.CompareTo(a.score));
            return list;
        }

        private int ComparePlayers(PlayerController a, PlayerController b, bool isTagMode) {
            var aScore = GetPlayerScore(a, isTagMode);
            var bScore = GetPlayerScore(b, isTagMode);
            return isTagMode ? aScore.CompareTo(bScore) : bScore.CompareTo(aScore);
        }

        // Player Registry
        private readonly HashSet<PlayerController> _allPlayersRegistry = new();

        private void RegisterPlayer(PlayerController player) {
            if(player == null || !_allPlayersRegistry.Add(player)) return;
            // Subscribe to profile changes used by scoreboard row content.
            player.playerName.OnValueChanged += OnPlayerProfileChanged;
            player.playerBaseColor.OnValueChanged += OnPlayerProfileChanged;

            // Force an update immediately so the scoreboard reflects the new player.
            UpdateScoreboard();
        }

        private void UnregisterPlayer(PlayerController player) {
            if(player == null || !_allPlayersRegistry.Contains(player)) return;

            player.playerName.OnValueChanged -= OnPlayerProfileChanged;
            player.playerBaseColor.OnValueChanged -= OnPlayerProfileChanged;

            _allPlayersRegistry.Remove(player);
            UpdateScoreboard();
        }

        private void OnPlayerProfileChanged<T>(T oldValue, T newValue) {
            // Clear cache to force full rebuild.
            _previousPlayerIds.Clear();
            _previousSortValues.Clear();
            UpdateScoreboard();
        }

        private void OnPlayerProfileChanged(Unity.Collections.FixedString64Bytes oldValue,
            Unity.Collections.FixedString64Bytes newValue) {
            // Clear cache to force full rebuild.
            _previousPlayerIds.Clear();
            _previousSortValues.Clear();
            UpdateScoreboard();
        }

        private IReadOnlyCollection<PlayerController> GetAllPlayerControllers() {
            // Clean up any nulls that might have slipped in (destroyed objects).
            _allPlayersRegistry.RemoveWhere(p => p == null);
            return _allPlayersRegistry;
        }

        private static (int yourScore, int enemyScore) CalculateTeamKillScores(
            IReadOnlyCollection<PlayerController> players, SpawnPoint.Team localTeam) {
            var yourTeamKills = 0;
            var enemyTeamKills = 0;

            foreach(var player in players) {
                if(player == null || !player.IsSpawned) continue;
                var teamMgr = player.TeamManager;
                if(teamMgr == null) continue;

                if(teamMgr.netTeam.Value == localTeam) {
                    yourTeamKills += player.Kills.Value;
                } else {
                    enemyTeamKills += player.Kills.Value;
                }
            }

            return (yourTeamKills, enemyTeamKills);
        }

        [Header("UI Templates")]
        [SerializeField] private VisualTreeAsset scoreboardRowTemplate;

        private bool _missingScoreboardTemplateLogged;
        private bool _invalidScoreboardTemplateLogged;

        private bool EnsureScoreboardRowTemplateAssigned() {
            if(scoreboardRowTemplate != null) {
                return true;
            }

            if(_missingScoreboardTemplateLogged) {
                return false;
            }

            _missingScoreboardTemplateLogged = true;
            Debug.LogError(
                "[ScoreboardManager] Missing `scoreboardRowTemplate` assignment. " +
                "Assign a scoreboard row VisualTreeAsset in the inspector.",
                this);
            return false;
        }

        private bool TryGetRequiredRowElements(VisualElement row, out Label pingLabel, out VisualElement avatar,
            out VisualElement speakingIndicator, out Label nameLabel, out Label[] statLabels) {
            pingLabel = row?.Q<Label>("ping-label");
            avatar = row?.Q<VisualElement>("avatar");
            speakingIndicator = avatar?.Q<VisualElement>("speaking-indicator");
            nameLabel = row?.Q<Label>("name-label");
            statLabels = new Label[7];

            var missingRequiredElements =
                row == null || pingLabel == null || avatar == null || speakingIndicator == null || nameLabel == null;
            for(var i = 0; i < statLabels.Length; i++) {
                statLabels[i] = row?.Q<Label>($"stat-{i}");
                if(statLabels[i] == null) {
                    missingRequiredElements = true;
                }
            }

            if(!missingRequiredElements) {
                return true;
            }

            if(_invalidScoreboardTemplateLogged) return false;
            _invalidScoreboardTemplateLogged = true;
            Debug.LogError(
                "[ScoreboardManager] `scoreboardRowTemplate` is missing required elements. " +
                "Expected: `ping-label`, `avatar`, `speaking-indicator`, `name-label`, and `stat-0` through `stat-6`.",
                this);

            return false;
        }

        private Label[] GetRowStatLabels(VisualElement row) {
            if(row?.userData is Label[] { Length: 7 } cachedLabels) {
                return cachedLabels;
            }

            if(!TryGetRequiredRowElements(row, out _, out _, out _, out _, out var statLabels)) {
                return null;
            }

            if(row != null) row.userData = statLabels;
            return statLabels;
        }

        /// <summary>
        /// Creates the base row structure (row element, ping, avatar, name) shared by all scoreboard rows.
        /// </summary>
        private VisualElement CreatePlayerRowBase(PlayerController player, VisualElement parentContainer,
            bool isYourTeam = false) {
            if(!EnsureScoreboardRowTemplateAssigned()) {
                return null;
            }

            var row = scoreboardRowTemplate.CloneTree();
            var rowRoot = row.Q<VisualElement>("scoreboard-row-root") ?? row;
            if(!TryGetRequiredRowElements(row, out var pingLabel, out var avatar, out var speakingIndicator,
                   out var nameLabel,
                   out var statLabels)) {
                return null;
            }

            row.userData = statLabels;

            // Highlight local player
            if(player.IsOwner) {
                rowRoot.AddToClassList("player-row-local");
                if(isYourTeam) {
                    rowRoot.AddToClassList("player-row-local-your-team");
                }
            }

            // Add to parent container
            parentContainer.Add(row);

            // Ping
            if(pingLabel != null) {
                pingLabel.text = GetPingText(player);
                var pingClass = GetPingColorClass(player);
                if(pingClass != string.Empty) {
                    pingLabel.AddToClassList(pingClass);
                }
            }

            // Avatar (player icon based on color)
            if(avatar != null) {
                ApplyFallbackAvatar(player, avatar);
                if(player != null && player.steamId.Value != 0) {
                    LoadSteamAvatar(player.steamId.Value, avatar).Forget();
                }
            }

            // Speaking indicator is template-owned and cached per player for state updates.
            if(speakingIndicator != null && player != null) {
                _cachedSpeakingIndicators[player.OwnerClientId] = speakingIndicator;
            }

            // Name

            if(nameLabel != null) {
                nameLabel.text = player.PlayerName.Value.ToString();
            }

            // Register click handler for context menu
            rowRoot.RegisterCallback<PointerDownEvent>(evt => {
                // Handle right-click (button 1 is right mouse button)
                if(evt.button != 1 || player == null || player.IsOwner ||
                   InGameContextMenuManager.Instance == null) return;
                Vector2 worldPos = evt.position;
                InGameContextMenuManager.Instance.Show(player.steamId.Value, worldPos);
            });

            return row;
        }

        /// <summary>
        /// Adds normal mode stats (K, D, A, KDR, DMG, HS%, AV) to a player row.
        /// </summary>
        private void AddNormalModeStats(VisualElement row, PlayerController player) {
            var statLabels = GetRowStatLabels(row);
            if(statLabels == null) return;

            // Ensure all stat columns are visible in non-tag modes.
            foreach(var t in statLabels) {
                t.style.display = DisplayStyle.Flex;
            }

            statLabels[0].text = player.Kills.Value.ToString();
            statLabels[1].text = player.Deaths.Value.ToString();
            statLabels[2].text = player.Assists.Value.ToString();

            var kda = CalculateKdr(player.Kills.Value, player.Deaths.Value, player.Assists.Value);
            statLabels[3].text = kda.ToString("F2");
            if(kda >= 2.0f) {
                statLabels[3].AddToClassList("player-stat-highlight");
            } else {
                statLabels[3].RemoveFromClassList("player-stat-highlight");
            }

            var damage = Mathf.RoundToInt(player.DamageDealt.Value);
            statLabels[4].text = $"{damage:N0}";
            statLabels[5].text = "0%";
            statLabels[6].text = GetAverageVelocityText(player);
        }

        private VisualElement CreatePlayerRow(PlayerController player, VisualElement parentContainer, bool isTagMode) {
            var row = CreatePlayerRowBase(player, parentContainer);
            if(row == null) {
                return null;
            }

            var statLabels = GetRowStatLabels(row);
            if(statLabels == null) {
                return row;
            }

            if(isTagMode) {
                // Tag mode stats: TT, Tags, Tagged, TTR, AV (5 stats, skip DMG and HS%)
                var tagCtrl = GetCachedTagController(player);
                var timeTaggedVal = tagCtrl != null ? tagCtrl.timeTagged.Value : 0;
                var tagsVal = tagCtrl != null ? tagCtrl.tags.Value : 0;
                var taggedVal = tagCtrl != null ? tagCtrl.tagged.Value : 0;

                // Show only TT/Tags/Tagged/TTR/AV stat slots in Gun Tag.
                statLabels[0].style.display = DisplayStyle.Flex;
                statLabels[1].style.display = DisplayStyle.Flex;
                statLabels[2].style.display = DisplayStyle.Flex;
                statLabels[3].style.display = DisplayStyle.Flex;
                statLabels[4].style.display = DisplayStyle.None; // DMG column hidden in tag mode
                statLabels[5].style.display = DisplayStyle.None; // HS% column hidden in tag mode
                statLabels[6].style.display = DisplayStyle.Flex;

                statLabels[0].text = timeTaggedVal.ToString();
                statLabels[1].text = tagsVal.ToString();
                statLabels[2].text = taggedVal.ToString();

                var ttr = CalculateTtr(tagsVal, taggedVal);
                statLabels[3].text = ttr.ToString("F2");
                if(ttr >= 2.0f) {
                    statLabels[3].AddToClassList("player-stat-highlight");
                } else {
                    statLabels[3].RemoveFromClassList("player-stat-highlight");
                }

                statLabels[4].text = string.Empty;
                statLabels[5].text = string.Empty;
                statLabels[6].text = GetAverageVelocityText(player);
            } else {
                // Normal mode stats
                AddNormalModeStats(row, player);
            }

            return row;
        }

        // Overload for TDM (includes K, D, A, KDR, DMG, HS%, AV)
        private VisualElement CreatePlayerRow(PlayerController player, VisualElement parentContainer,
            bool simplifiedStats,
            bool isYourTeam) {
            if(!simplifiedStats) {
                // Call the FFA version with isTagMode = false
                return CreatePlayerRow(player, parentContainer, isTagMode: false);
            }

            var row = CreatePlayerRowBase(player, parentContainer, isYourTeam);
            if(row == null) {
                return null;
            }

            AddNormalModeStats(row, player);
            return row;
        }

        private VisualElement CreateEmptyRow(VisualElement parentContainer, bool isTagMode) {
            if(!EnsureScoreboardRowTemplateAssigned()) {
                return null;
            }

            var row = scoreboardRowTemplate.CloneTree();
            var rowRoot = row.Q<VisualElement>("scoreboard-row-root") ?? row;
            if(!TryGetRequiredRowElements(row, out var pingLabel, out _, out _, out var nameLabel,
                   out var statLabels)) {
                return null;
            }

            row.userData = statLabels;

            rowRoot.AddToClassList("player-row-empty");

            parentContainer.Add(row);

            if(pingLabel != null) pingLabel.text = "-";
            if(nameLabel != null) nameLabel.text = "-";

            foreach(var t in statLabels) {
                t.text = "-";
            }

            if(isTagMode) {
                statLabels[4].style.display = DisplayStyle.None;
                statLabels[5].style.display = DisplayStyle.None;
            } else {
                statLabels[4].style.display = DisplayStyle.Flex;
                statLabels[5].style.display = DisplayStyle.Flex;
            }

            return row;
        }

        private static string GetPingText(PlayerController player) {
            var ping = 0;
            if(player != null) {
                ping = player.PingMs;
            }

            return $"{ping}ms";
        }

        private static string GetPingColorClass(PlayerController player) {
            var ping = 0;
            if(player != null) {
                ping = player.PingMs;
            }

            return ping switch {
                > 100 => "player-ping-critical",
                > 50 => "player-ping-high",
                _ => ""
            };
        }

        private static float CalculateKdr(int kills, int deaths, int assists) {
            if(deaths == 0) return kills + assists;
            return (kills + assists) / (float)deaths;
        }

        private static float CalculateTtr(int tags, int tagged) {
            if(tagged == 0) return tags;
            return tags / (float)tagged;
        }

        /// <summary>
        /// Gets cached PlayerTagController for a player, or retrieves and caches it if not found.
        /// </summary>
        private PlayerTagController GetCachedTagController(PlayerController player) {
            return GetCachedComponent(player, _cachedTagControllers, pc => pc.TagController);
        }

        /// <summary>
        /// Gets cached PlayerStatsController for a player, or retrieves and caches it if not found.
        /// </summary>
        private PlayerStatsController GetCachedStatsController(PlayerController player) {
            return GetCachedComponent(player, _cachedStatsControllers, pc => pc.StatsController);
        }

        private static T GetCachedComponent<T>(PlayerController player, Dictionary<PlayerController, T> cache,
            Func<PlayerController, T> directAccessor) where T : Component {
            if(player == null) return null;

            var direct = directAccessor(player);
            if(direct != null) return direct;

            if(cache.TryGetValue(player, out var cached)) return cached;
            var component = player.GetComponent<T>();
            if(component != null) {
                cache[player] = component;
            }

            return component;
        }

        /// <summary>
        /// Gets the player icon sprite based on the player's material index.
        /// Material index order: 0=white, 1=red, 2=orange, 3=yellow, 4=green, 5=blue, 6=purple.
        /// </summary>
        private Sprite GetPlayerIconSprite(Color baseColor) {
            if(playerIconSprites == null || playerIconSprites.Length == 0) {
                return null;
            }

            var paletteIndex = GetClosestIconIndex(baseColor);
            var clampedIndex = Mathf.Clamp(paletteIndex, 0, playerIconSprites.Length - 1);
            return playerIconSprites[clampedIndex];
        }

        private int GetClosestIconIndex(Color baseColor) {
            if(playerIconSprites == null || playerIconSprites.Length == 0) return 0;

            // Use the legacy palette order: white, red, orange, yellow, green, blue, purple.
            var palette = new[] {
                new Color(1f, 1f, 1f),
                new Color(1f, 0f, 0f),
                new Color(1f, 0.5f, 0f),
                new Color(1f, 1f, 0f),
                new Color(0f, 1f, 0f),
                new Color(0f, 0f, 1f),
                new Color(0.5f, 0f, 1f)
            };

            var bestIndex = 0;
            var bestDistance = float.MaxValue;

            for(var i = 0; i < palette.Length; i++) {
                var diff = palette[i] - baseColor;
                var distSq = diff.r * diff.r + diff.g * diff.g + diff.b * diff.b;

                if(!(distSq < bestDistance)) continue;
                bestDistance = distSq;
                bestIndex = i;
            }

            return bestIndex;
        }

        /// <summary>
        /// Updates the score display next to the timer based on game mode.
        /// </summary>
        private void UpdateScoreDisplay() {
            if(_leftScoreContainer == null || _rightScoreContainer == null ||
               _leftScoreValue == null || _rightScoreValue == null) {
                return;
            }

            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings == null) return;

            var isTeamBased = MatchSettingsManager.IsTeamBasedMode(matchSettings.selectedGameModeId);

            var controllers = GetAllPlayerControllers();

            if(isTeamBased) {
                UpdateTeamBasedScore(controllers);
            } else {
                UpdateFfaScore(controllers);
            }
        }

        /// <summary>
        /// Updates score display for team-based modes.
        /// </summary>
        private void UpdateTeamBasedScore(IReadOnlyCollection<PlayerController> allControllers = null) {
            if(allControllers == null) throw new ArgumentNullException(nameof(allControllers));
            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings == null) return;

            int yourScore;
            int enemyScore;

            // Get local player's team
            var networkManager = NetworkManager.Singleton;
            if(networkManager == null) return;
            if(networkManager.LocalClient == null) return;
            var localPlayer = networkManager.LocalClient.PlayerObject;
            if(localPlayer == null) return;

            var localController = localPlayer.GetComponent<PlayerController>();
            PlayerTeamManager localTeamMgr = null;
            if(localController != null) {
                localTeamMgr = localController.TeamManager;
            }

            if(localTeamMgr == null) return;

            var localTeam = localTeamMgr.netTeam.Value;

            switch(matchSettings.selectedGameModeId) {
                // Get scores based on game mode
                case "Hopball" when HopballSpawnManager.Instance != null: {
                    var teamAScore = HopballSpawnManager.Instance.GetTeamAScore();
                    var teamBScore = HopballSpawnManager.Instance.GetTeamBScore();

                    if(localTeam == SpawnPoint.Team.TeamA) {
                        yourScore = teamAScore;
                        enemyScore = teamBScore;
                    } else {
                        yourScore = teamBScore;
                        enemyScore = teamAScore;
                    }

                    break;
                }
                case "KOTH" when KingOfTheHillManager.Instance != null: {
                    var teamAScore = KingOfTheHillManager.Instance.GetTeamAScore();
                    var teamBScore = KingOfTheHillManager.Instance.GetTeamBScore();

                    if(localTeam == SpawnPoint.Team.TeamA) {
                        yourScore = teamAScore;
                        enemyScore = teamBScore;
                    } else {
                        yourScore = teamBScore;
                        enemyScore = teamAScore;
                    }

                    break;
                }
                default:
                    // For other team modes, use total kills
                    allControllers = GetAllPlayerControllers();
                    (yourScore, enemyScore) = CalculateTeamKillScores(allControllers, localTeam);
                    break;
            }

            _leftScoreValue.text = yourScore.ToString();
            _rightScoreValue.text = enemyScore.ToString();
        }

        /// <summary>
        /// Updates score display for FFA modes (Deathmatch, Gun Tag, etc.).
        /// </summary>
        private void UpdateFfaScore(IReadOnlyCollection<PlayerController> allControllers = null) {
            if(allControllers == null) throw new ArgumentNullException(nameof(allControllers));
            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings == null) return;

            var isTagMode = matchSettings.selectedGameModeId == "Gun Tag";

            // Get local player
            if(_localController == null) {
                FindLocalController();
            }

            if(_localController == null) return;

            // Get local player's score
            var localScore = GetPlayerScore(_localController, isTagMode);

            // Get all players and find the next highest (or highest if local is not first)
            allControllers = GetAllPlayerControllers();
            var sortedPlayers = BuildSortedScoreList(allControllers, isTagMode);

            // Find next highest/lowest score
            var nextScore = 0;
            var foundNext = false;

            if(isTagMode) {
                // For Gun Tag, find next LOWEST (or lowest if local is lowest)
                for(var i = 0; i < sortedPlayers.Count; i++) {
                    if(sortedPlayers[i].player != _localController) continue;
                    // If we're the lowest (1st place), show the next lowest (2nd place)
                    if(i == 0) {
                        // Show 2nd place (next lowest)
                        nextScore = sortedPlayers.Count > 1 ? sortedPlayers[1].score : 0; // Only one player

                        foundNext = true;
                        break;
                    }

                    // Otherwise show the lowest (1st place)
                    nextScore = sortedPlayers[0].score;
                    foundNext = true;
                    break;
                }
            } else {
                // For Deathmatch, find next HIGHEST (or highest if local is highest)
                for(var i = 0; i < sortedPlayers.Count; i++) {
                    if(sortedPlayers[i].player != _localController) continue;
                    // If we're the highest (1st place), show the next highest (2nd place)
                    if(i == 0) {
                        // Show 2nd place (next highest)
                        nextScore = sortedPlayers.Count > 1 ? sortedPlayers[1].score : 0; // Only one player

                        foundNext = true;
                        break;
                    }

                    // Otherwise show the highest (1st place)
                    nextScore = sortedPlayers[0].score;
                    foundNext = true;
                    break;
                }
            }

            if(!foundNext && sortedPlayers.Count > 0) {
                // Fallback: use first place score
                nextScore = sortedPlayers[0].score;
            }

            _leftScoreValue.text = localScore.ToString();
            _rightScoreValue.text = nextScore.ToString();
        }

        private void HideScoreDisplay() {
            if(_leftScoreContainer != null)
                _leftScoreContainer.style.display = DisplayStyle.None;
            if(_rightScoreContainer != null)
                _rightScoreContainer.style.display = DisplayStyle.None;
        }

        private void ShowScoreDisplay() {
            if(_leftScoreContainer != null)
                _leftScoreContainer.style.display = DisplayStyle.Flex;
            if(_rightScoreContainer != null)
                _rightScoreContainer.style.display = DisplayStyle.Flex;
        }

        private static async Cysharp.Threading.Tasks.UniTaskVoid LoadSteamAvatar(ulong steamId, VisualElement avatarElement) {
            if(!Steamworks.SteamClient.IsValid || !Steamworks.SteamClient.IsLoggedOn) return;
            if(SteamManager.Instance == null) return;

            try {
                var texture = await SteamManager.Instance.GetAvatarAsync(steamId);
                if(texture == null) {
                    return;
                }

                if(avatarElement != null) {
                    avatarElement.style.backgroundImage = new StyleBackground(texture);
                }
            } catch(Exception ex) {
                Debug.LogWarning($"[ScoreboardManager] Steam avatar fetch failed for {steamId}: {ex.Message}");
            }
        }

        private void ApplyFallbackAvatar(PlayerController player, VisualElement avatarElement) {
            if(avatarElement == null) return;

            var baseColor = player != null ? player.CurrentBaseColor : Color.white;
            var playerIcon = GetPlayerIconSprite(baseColor);
            if(playerIcon != null) {
                avatarElement.style.backgroundImage = new StyleBackground(playerIcon);
            }
        }
    }
}
