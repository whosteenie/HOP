namespace Events {
    /// <summary>
    /// Event published when session properties have been refreshed from the server.
    /// Clients can use this to update their local state (e.g., gamemode).
    /// </summary>
    public class SessionPropertiesRefreshedEvent : GameEvent {
    }

    /// <summary>
    /// Event published when front-end session status text changes (or when status UI should refresh).
    /// </summary>
    public class FrontStatusChangedEvent : GameEvent {
        public readonly string Message;

        public FrontStatusChangedEvent(string message) {
            Message = message;
        }
    }
}

