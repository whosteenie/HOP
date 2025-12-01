using UnityEngine;

namespace Network.Events {
    /// <summary>
    /// Extension methods for easier EventBus cleanup in MonoBehaviours.
    /// </summary>
    public static class EventBusExtensions {
        /// <summary>
        /// Unsubscribe from all events when the MonoBehaviour is destroyed.
        /// Call this in OnDestroy() to automatically clean up subscriptions.
        /// </summary>
        public static void UnsubscribeFromEventBus(this MonoBehaviour behaviour) {
            EventBus.UnsubscribeAll(behaviour);
        }
    }
}

