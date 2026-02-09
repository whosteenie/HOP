namespace Network.Diagnostics {
    /// <summary>
    /// Stable event IDs for structured flow logging.
    /// </summary>
    public static class FlowEventIds {
        // Boot / auth
        public const string BootStart = "BOOT_001";
        public const string AuthResult = "AUTH_001";

        // Session / party / queue
        public const string PartyLifecycle = "PARTY_001";
        public const string ModeSelect = "MODE_SELECT_001";
        public const string QueueStarted = "QUEUE_001";
        public const string QueueAssigned = "QUEUE_002";
        public const string SyncStateTransition = "SYNC_001";
        public const string ModeApply = "MODE_APPLY_001";
        public const string SceneLoaded = "SCENE_001";
        public const string PlayerSpawned = "SPAWN_PLAYER_001";
        public const string ObjectiveSpawned = "SPAWN_OBJECTIVE_001";
        public const string MatchStateTransition = "MATCH_STATE_001";
        public const string SessionExit = "SESSION_EXIT_001";

        // Player state
        public const string PlayerLethal = "PLAYER_LIFE_001";
        public const string PlayerDeathEntered = "PLAYER_LIFE_002";
        public const string PlayerRagdollState = "PLAYER_LIFE_003";
        public const string PlayerRespawnStarted = "PLAYER_RESPAWN_001";
        public const string PlayerRespawnCompleted = "PLAYER_RESPAWN_002";
        public const string PlayerControlState = "PLAYER_CONTROL_001";

        // Hopball flow
        public const string HopballPickupRequested = "HOPBALL_FLOW_001";
        public const string HopballPickupCommitted = "HOPBALL_FLOW_002";
        public const string HopballHoldStateChanged = "HOPBALL_FLOW_003";
        public const string HopballDropCommitted = "HOPBALL_FLOW_004";
        public const string HopballForcedDrop = "HOPBALL_FLOW_005";
        public const string HopballDissolveStarted = "HOPBALL_FLOW_006";
        public const string HopballDissolveCompleted = "HOPBALL_FLOW_007";
        public const string HopballRespawnExecuted = "HOPBALL_FLOW_008";
        public const string HopballOobRecovery = "HOPBALL_FLOW_009";

        // Anomalies
        public const string AnomalyModeMismatch = "ANOM_MODE_001";
        public const string AnomalyRespawnInvariant = "ANOM_RESPAWN_001";
        public const string AnomalyHopballMismatch = "ANOM_HOPBALL_001";
        public const string AnomalyDeathRespawnTimeout = "ANOM_PLAYER_001";
        public const string AnomalyHopballDivergence = "ANOM_HOPBALL_002";
        public const string AnomalySessionStuck = "ANOM_SESSION_001";
    }
}
