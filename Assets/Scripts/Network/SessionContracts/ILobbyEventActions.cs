namespace Network.SessionContracts {
    /// <summary>
    /// Actions used by SessionMatchLobbyService when unsubscribing or when match lobby is deleted/kicked.
    /// Implemented by SessionManager.
    /// </summary>
    public interface ILobbyEventActions {
        void CompletePlayersReadyWaiter(bool result);
    }
}
