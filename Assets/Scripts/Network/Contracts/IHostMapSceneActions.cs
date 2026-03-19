namespace Network.Contracts {
    /// <summary>
    /// Host-only actions for map selection and loading the gameplay scene.
    /// Used by SessionSceneFlowService when the local player is starting as host.
    /// </summary>
    public interface IHostMapSceneActions {
        bool TryGetNetworkManager(string context);
        void SetSelectedMap(string mapId, string sceneName);
        /// <summary>Sets the map from a private match draft (map id). Skips random selection when loading the gameplay scene.</summary>
        void SetSelectedMapFromId(string mapId);
        bool ConsumePrivateMatchMapPreset();
        void LoadScene(string sceneName);
        void SetSteamLobbyMapIfOwner(string mapId, string sceneName);
    }
}
