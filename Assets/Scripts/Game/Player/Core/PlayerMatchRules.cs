using Events;
using Game.Match;

namespace Game.Player.Core {
    internal static class PlayerMatchRules {
        public static bool IsPreMatchMovementLocked =>
            MatchTimerManager.Instance != null &&
            MatchTimerManager.Instance.CurrentState is MatchLifecycleState.WaitingForPlayers or MatchLifecycleState.Countdown;

        public static bool IsPostMatchMovementLocked => PostMatchManager.IsPostMatchMovementLockedLocal;

        public static bool IsPostMatchFlowStarted =>
            PostMatchManager.Instance != null && PostMatchManager.Instance.PostMatchFlowStarted;

        public static string CurrentGameModeId => MatchSettingsManager.Instance != null
            ? MatchSettingsManager.Instance.selectedGameModeId
            : string.Empty;

        public static bool IsGunTagMode => MatchSettingsManager.Instance != null &&
                                           MatchSettingsManager.Instance.selectedGameModeId == "Gun Tag";

        public static bool IsTeamBasedMode => MatchSettingsManager.IsTeamBasedMode(CurrentGameModeId);
    }
}
