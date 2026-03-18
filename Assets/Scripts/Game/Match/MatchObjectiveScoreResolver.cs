using Events;

namespace Game.Match {
    internal static class MatchObjectiveScoreResolver {
        public static bool TryGetLocalizedTeamScores(string gameModeId, SpawnPoint.Team localTeam,
            out int yourScore, out int enemyScore) {
            yourScore = 0;
            enemyScore = 0;

            if(!TryGetTeamScores(gameModeId, out var teamAScore, out var teamBScore)) {
                return false;
            }

            (yourScore, enemyScore) = localTeam == SpawnPoint.Team.TeamA
                ? (teamAScore, teamBScore)
                : (teamBScore, teamAScore);
            return true;
        }

        public static bool TryGetTeamScores(string gameModeId, out int teamAScore, out int teamBScore) {
            teamAScore = 0;
            teamBScore = 0;
            var request = new ObjectiveTeamScoresRequestedEvent(gameModeId);
            EventBus.Publish(request);
            if(!request.HasScores) return false;

            teamAScore = request.TeamAScore;
            teamBScore = request.TeamBScore;
            return true;
        }
    }
}
