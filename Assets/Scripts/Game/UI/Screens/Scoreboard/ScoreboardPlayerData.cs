using System.Collections.Generic;
using Game.Match;
using Game.Player.Combat;
using Game.Player.Core;
using UnityEngine;

namespace Game.UI.Screens.Scoreboard {
    /// <summary>Component cache and score/sort logic for scoreboard players (tag, stats, KDR, team kills).</summary>
    internal sealed class ScoreboardPlayerData {
        private readonly Dictionary<PlayerController, PlayerTagController> _tagControllers = new();
        private readonly Dictionary<PlayerController, PlayerStatsController> _statsControllers = new();

        public PlayerTagController GetTagController(PlayerController player) {
            return GetCachedComponent(player, _tagControllers, pc => pc.TagController);
        }

        public PlayerStatsController GetStatsController(PlayerController player) {
            return GetCachedComponent(player, _statsControllers, pc => pc.StatsController);
        }

        public void Clear() {
            _tagControllers.Clear();
            _statsControllers.Clear();
        }

        private void RemovePlayer(PlayerController player) {
            if(player == null) return;
            _tagControllers.Remove(player);
            _statsControllers.Remove(player);
        }

        public void RemovePlayersByClientId(ulong clientId, IReadOnlyCollection<PlayerController> allPlayers) {
            foreach(var p in allPlayers) {
                if(p == null || p.OwnerClientId != clientId) continue;
                RemovePlayer(p);
                return;
            }
        }

        public int GetPlayerScore(PlayerController player, bool isTagMode) {
            if(player == null) return isTagMode ? int.MaxValue : 0;
            if(!isTagMode) return player.Kills.Value;
            var tagCtrl = GetTagController(player);
            return tagCtrl != null ? tagCtrl.TimeTagged.Value : int.MaxValue;
        }

        public string GetAverageVelocityText(PlayerController player) {
            var statsCtrl = GetStatsController(player);
            var avgVelocity = statsCtrl != null ? statsCtrl.AverageVelocity.Value : 0f;
            return $"{avgVelocity:F1} u/s";
        }

        public List<PlayerController> BuildSortedPlayerList(IReadOnlyCollection<PlayerController> players, bool isTagMode) {
            var sorted = new List<PlayerController>();
            foreach(var p in players) {
                if(p == null || !p.IsSpawned) continue;
                sorted.Add(p);
            }
            sorted.Sort((a, b) => ComparePlayers(a, b, isTagMode));
            return sorted;
        }

        public List<(PlayerController player, int score)> BuildSortedScoreList(
            IReadOnlyCollection<PlayerController> players, bool isTagMode) {
            var list = new List<(PlayerController player, int score)>();
            foreach(var p in players) {
                if(p == null || !p.IsSpawned) continue;
                list.Add((p, GetPlayerScore(p, isTagMode)));
            }
            list.Sort((a, b) => isTagMode ? a.score.CompareTo(b.score) : b.score.CompareTo(a.score));
            return list;
        }

        private int ComparePlayers(PlayerController a, PlayerController b, bool isTagMode) {
            var aScore = GetPlayerScore(a, isTagMode);
            var bScore = GetPlayerScore(b, isTagMode);
            return isTagMode ? aScore.CompareTo(bScore) : bScore.CompareTo(aScore);
        }

        public static (int yourScore, int enemyScore) CalculateTeamKillScores(
            IReadOnlyCollection<PlayerController> players, SpawnPoint.Team localTeam) {
            var yourTeamKills = 0;
            var enemyTeamKills = 0;
            foreach(var player in players) {
                if(player == null || !player.IsSpawned) continue;
                var teamMgr = player.TeamManager;
                if(teamMgr == null) continue;
                if(teamMgr.netTeam.Value == localTeam)
                    yourTeamKills += player.Kills.Value;
                else
                    enemyTeamKills += player.Kills.Value;
            }
            return (yourTeamKills, enemyTeamKills);
        }

        public static float CalculateKdr(int kills, int deaths, int assists) {
            if(deaths == 0) return kills + assists;
            return (kills + assists) / (float)deaths;
        }

        public static float CalculateTtr(int tags, int tagged) {
            if(tagged == 0) return tags;
            return tags / (float)tagged;
        }

        private static T GetCachedComponent<T>(PlayerController player, Dictionary<PlayerController, T> cache,
            System.Func<PlayerController, T> directAccessor) where T : Component {
            if(player == null) return null;
            var direct = directAccessor(player);
            if(direct != null) return direct;
            if(cache.TryGetValue(player, out var cached)) return cached;
            var component = player.GetComponent<T>();
            if(component != null) cache[player] = component;
            return component;
        }
    }
}
