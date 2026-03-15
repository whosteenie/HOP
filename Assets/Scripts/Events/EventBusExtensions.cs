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

        /// <summary>
        /// Adds publisher context key/value metadata to an event in a fluent style.
        /// </summary>
        public static T WithContext<T>(this T gameEvent, string key, string value) where T : GameEvent {
            if(gameEvent == null) return gameEvent;
            gameEvent.SetContext(key, value);
            return gameEvent;
        }

        public static T WithContext<T>(this T gameEvent, string key, int value) where T : GameEvent {
            if(gameEvent == null) return gameEvent;
            gameEvent.SetContext(key, value);
            return gameEvent;
        }

        public static T WithContext<T>(this T gameEvent, string key, long value) where T : GameEvent {
            if(gameEvent == null) return gameEvent;
            gameEvent.SetContext(key, value);
            return gameEvent;
        }

        public static T WithContext<T>(this T gameEvent, string key, bool value) where T : GameEvent {
            if(gameEvent == null) return gameEvent;
            gameEvent.SetContext(key, value);
            return gameEvent;
        }

        public static T WithContext<T>(this T gameEvent, string key, float value) where T : GameEvent {
            if(gameEvent == null) return gameEvent;
            gameEvent.SetContext(key, value);
            return gameEvent;
        }

        /// <summary>
        /// Adds common Unity object metadata to event publisher context.
        /// </summary>
        public static T WithSourceContext<T>(this T gameEvent, string prefix, Object source) where T : GameEvent {
            if(gameEvent == null || source == null) return gameEvent;
            gameEvent.ContextValues.AddUnityObject(prefix, source);
            return gameEvent;
        }

        /// <summary>
        /// Applies the current EventBus correlation values to an outbound event.
        /// Useful for RPC forwarding helpers.
        /// </summary>
        public static T WithCurrentCorrelation<T>(this T gameEvent) where T : GameEvent {
            if(gameEvent == null) return gameEvent;
            EventBus.AttachCurrentCorrelation(gameEvent);
            return gameEvent;
        }
    }
}

