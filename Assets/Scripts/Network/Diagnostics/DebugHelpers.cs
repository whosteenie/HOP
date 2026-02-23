using System;
using System.Diagnostics;
using Network.Events;
using Unity.Netcode;
using UnityEngine;

namespace Network.Diagnostics {
    /// <summary>
    /// Helper methods for safe component lookups, network operations, and singleton access.
    /// These methods publish debug events when failures occur, making silent failures visible.
    /// </summary>
    public static class DebugHelpers {
        /// <summary>
        /// Safely gets a component, publishing a debug event if not found.
        /// </summary>
        private static T GetComponentSafe<T>(this GameObject gameObject, string context = null) where T : Component {
            if(gameObject == null) {
                EventBus.Publish(new GameObjectNotFoundEvent("null", context ?? "GetComponentSafe"));
                return null;
            }

            var component = gameObject.GetComponent<T>();
            if(component != null) return component;
            var contextStr = context ?? $"{typeof(T).Name} lookup on {gameObject.name}";
            EventBus.Publish(new ComponentNotFoundEvent(typeof(T).Name, gameObject.name, contextStr));
            return component;
        }

        /// <summary>
        /// Safely gets a component, publishing a debug event if not found.
        /// </summary>
        public static T GetComponentSafe<T>(this Component component, string context = null) where T : Component {
            if(component != null) return component.gameObject.GetComponentSafe<T>(context);
            EventBus.Publish(new GameObjectNotFoundEvent("null", context ?? "GetComponentSafe"));
            return null;
        }

        /// <summary>
        /// Safely gets a component in children, publishing a debug event if not found.
        /// </summary>
        private static T GetComponentInChildrenSafe<T>(this GameObject gameObject, bool includeInactive = false, string context = null) where T : Component {
            if(gameObject == null) {
                EventBus.Publish(new GameObjectNotFoundEvent("null", context ?? "GetComponentInChildrenSafe"));
                return null;
            }

            var component = gameObject.GetComponentInChildren<T>(includeInactive);
            if(component != null) return component;
            var contextStr = context ?? $"{typeof(T).Name} lookup in children of {gameObject.name}";
            EventBus.Publish(new ComponentNotFoundEvent(typeof(T).Name, gameObject.name, contextStr));
            return component;
        }

        /// <summary>
        /// Safely gets a component in children, publishing a debug event if not found.
        /// </summary>
        public static T GetComponentInChildrenSafe<T>(this Component component, bool includeInactive = false, string context = null) where T : Component {
            if(component != null) return component.gameObject.GetComponentInChildrenSafe<T>(includeInactive, context);
            EventBus.Publish(new GameObjectNotFoundEvent("null", context ?? "GetComponentInChildrenSafe"));
            return null;
        }

        /// <summary>
        /// Safely tries to get a NetworkObject from a NetworkObjectReference, publishing a debug event if it fails.
        /// </summary>
        public static bool TryGetNetworkObjectSafe(NetworkObjectReference reference, out NetworkObject networkObject, 
            ulong clientId, string context = null) {
            if(reference.TryGet(out networkObject) && networkObject != null && networkObject.IsSpawned) return true;
            var reason = networkObject == null ? "null" : !networkObject.IsSpawned ? "not spawned" : "TryGet failed";
            EventBus.Publish(new NetworkObjectReferenceFailedEvent(clientId, context ?? "NetworkObjectReference lookup", reason));
            return false;
        }

        /// <summary>
        /// Safely gets a singleton instance, publishing a debug event if not available.
        /// </summary>
        public static T GetSingletonSafe<T>(T instance, string singletonTypeName, string context = null) where T : class {
            if(instance != null) return instance;
            var contextStr = context ?? $"Accessing {singletonTypeName}";
            EventBus.Publish(new SingletonNotAvailableEvent(singletonTypeName, contextStr));
            return null;
        }

        /// <summary>
        /// Safely finds the first object of type, publishing a debug event if not found.
        /// </summary>
        public static T FindFirstObjectByTypeSafe<T>(string context = null) where T : UnityEngine.Object {
            var obj = UnityEngine.Object.FindFirstObjectByType<T>();
            if(obj != null) return obj;
            var contextStr = context ?? $"Finding first {typeof(T).Name}";
            EventBus.Publish(new ComponentNotFoundEvent(typeof(T).Name, "Scene", contextStr));
            return obj;
        }

        /// <summary>
        /// Publishes a critical error event with full context.
        /// </summary>
        public static void PublishCriticalError(string errorMessage, string context, Exception exception = null) {
            var stackTrace = exception != null ? exception.StackTrace : new StackTrace(1, true).ToString();
            EventBus.Publish(new CriticalErrorEvent(errorMessage, stackTrace, context, exception));
        }

        /// <summary>
        /// Publishes a network RPC failure event.
        /// </summary>
        public static void PublishNetworkRpcFailed(string rpcName, ulong targetClientId, string reason) {
            EventBus.Publish(new NetworkRpcFailedEvent(rpcName, targetClientId, reason));
        }
    }
}

