namespace Network.SessionContracts {
    /// <summary>
    /// Result of attempting to join a distributed authority session.
    /// Lives in SessionContracts so contract interfaces remain one-way.
    /// </summary>
    public enum DaSessionJoinResult {
        Success,
        RateLimited,
        Failed
    }
}
