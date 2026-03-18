using Events;
using UnityEngine;

namespace Diagnostics {
    /// <summary>
    /// Subscribes to all debug events and logs them with context.
    /// Add this component to a GameObject in your scene to enable debug event logging.
    /// </summary>
    public class DebugEventLogger : MonoBehaviour {
        [Header("Logging Settings")]
        [SerializeField] private bool logComponentNotFound = true;
        [SerializeField] private bool logNetworkObjectReferenceFailed = true;
        [SerializeField] private bool logSingletonNotAvailable = true;
        [SerializeField] private bool logCriticalError = true;
        [SerializeField] private bool logNetworkRpcFailed = true;
        [SerializeField] private bool logGameObjectNotFound = true;

        private void OnEnable() {
            // Subscribe to all debug events
            EventBus.Subscribe<ComponentNotFoundEvent>(OnComponentNotFound);
            EventBus.Subscribe<NetworkObjectReferenceFailedEvent>(OnNetworkObjectReferenceFailed);
            EventBus.Subscribe<SingletonNotAvailableEvent>(OnSingletonNotAvailable);
            EventBus.Subscribe<CriticalErrorEvent>(OnCriticalError);
            EventBus.Subscribe<NetworkRpcFailedEvent>(OnNetworkRpcFailed);
            EventBus.Subscribe<GameObjectNotFoundEvent>(OnGameObjectNotFound);
        }

        private void OnDisable() {
            // Unsubscribe from all debug events
            EventBus.Unsubscribe<ComponentNotFoundEvent>(OnComponentNotFound);
            EventBus.Unsubscribe<NetworkObjectReferenceFailedEvent>(OnNetworkObjectReferenceFailed);
            EventBus.Unsubscribe<SingletonNotAvailableEvent>(OnSingletonNotAvailable);
            EventBus.Unsubscribe<CriticalErrorEvent>(OnCriticalError);
            EventBus.Unsubscribe<NetworkRpcFailedEvent>(OnNetworkRpcFailed);
            EventBus.Unsubscribe<GameObjectNotFoundEvent>(OnGameObjectNotFound);
        }

        #region Event Handlers

        private void OnComponentNotFound(ComponentNotFoundEvent evt) {
            if(!logComponentNotFound) return;
            DevLog.LogError($"[DebugEventLogger] Component Not Found: {evt.ComponentType} on {evt.GameObjectName} " +
                            $"(Context: {evt.Context})");
        }

        private void OnNetworkObjectReferenceFailed(NetworkObjectReferenceFailedEvent evt) {
            if(!logNetworkObjectReferenceFailed) return;
            DevLog.LogWarning($"[DebugEventLogger] NetworkObjectReference Failed: ClientId={evt.ClientId}, " +
                              $"Reason={evt.Reason} (Context: {evt.Context})");
        }

        private void OnSingletonNotAvailable(SingletonNotAvailableEvent evt) {
            if(!logSingletonNotAvailable) return;
            DevLog.LogWarning($"[DebugEventLogger] Singleton Not Available: {evt.SingletonType} " +
                              $"(Context: {evt.Context})");
        }

        private void OnCriticalError(CriticalErrorEvent evt) {
            if(!logCriticalError) return;
            var message = $"[DebugEventLogger] Critical Error: {evt.ErrorMessage} (Context: {evt.Context})";
            if(evt.Exception != null) {
                DevLog.LogError(message + $"\nException: {evt.Exception}", this);
            } else {
                DevLog.LogError(message + $"\nStackTrace: {evt.StackTrace}", this);
            }
        }

        private void OnNetworkRpcFailed(NetworkRpcFailedEvent evt) {
            if(!logNetworkRpcFailed) return;
            DevLog.LogWarning($"[DebugEventLogger] Network RPC Failed: {evt.RpcName} to ClientId={evt.TargetClientId}, " +
                              $"Reason={evt.Reason}");
        }

        private void OnGameObjectNotFound(GameObjectNotFoundEvent evt) {
            if(!logGameObjectNotFound) return;
            DevLog.LogError($"[DebugEventLogger] GameObject Not Found: {evt.GameObjectName} " +
                            $"(Context: {evt.Context})");
        }

        #endregion
    }
}
