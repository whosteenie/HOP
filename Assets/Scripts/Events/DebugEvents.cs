using System;

namespace Network.Events {
    /// <summary>
    /// Event published when a component lookup fails.
    /// </summary>
    public class ComponentNotFoundEvent : GameEvent {
        public string ComponentType { get; }
        public string GameObjectName { get; }
        public string Context { get; }
        public ComponentNotFoundEvent(string componentType, string gameObjectName, string context) {
            ComponentType = componentType;
            GameObjectName = gameObjectName;
            Context = context;
        }
    }

    /// <summary>
    /// Event published when a NetworkObjectReference.TryGet fails.
    /// </summary>
    public class NetworkObjectReferenceFailedEvent : GameEvent {
        public ulong ClientId { get; }
        public string Context { get; }
        public string Reason { get; }
        public NetworkObjectReferenceFailedEvent(ulong clientId, string context, string reason) {
            ClientId = clientId;
            Context = context;
            Reason = reason;
        }
    }

    /// <summary>
    /// Event published when a required singleton is not available.
    /// </summary>
    public class SingletonNotAvailableEvent : GameEvent {
        public string SingletonType { get; }
        public string Context { get; }
        public SingletonNotAvailableEvent(string singletonType, string context) {
            SingletonType = singletonType;
            Context = context;
        }
    }

    /// <summary>
    /// Event published when a critical error occurs that should be logged.
    /// </summary>
    public class CriticalErrorEvent : GameEvent {
        public string ErrorMessage { get; }
        public string StackTrace { get; }
        public string Context { get; }
        public Exception Exception { get; }
        public CriticalErrorEvent(string errorMessage, string stackTrace, string context, Exception exception = null) {
            ErrorMessage = errorMessage;
            StackTrace = stackTrace;
            Context = context;
            Exception = exception;
        }
    }

    /// <summary>
    /// Event published when a network RPC fails.
    /// </summary>
    public class NetworkRpcFailedEvent : GameEvent {
        public string RpcName { get; }
        public ulong TargetClientId { get; }
        public string Reason { get; }
        public NetworkRpcFailedEvent(string rpcName, ulong targetClientId, string reason) {
            RpcName = rpcName;
            TargetClientId = targetClientId;
            Reason = reason;
        }
    }

    /// <summary>
    /// Event published when a required GameObject is null or missing.
    /// </summary>
    public class GameObjectNotFoundEvent : GameEvent {
        public string GameObjectName { get; }
        public string Context { get; }
        public GameObjectNotFoundEvent(string gameObjectName, string context) {
            GameObjectName = gameObjectName;
            Context = context;
        }
    }
}



