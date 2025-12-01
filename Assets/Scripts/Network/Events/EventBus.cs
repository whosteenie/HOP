using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;

namespace Network.Events {
    /// <summary>
    /// Central event bus for decoupled communication between systems.
    /// Provides type-safe event publishing and subscription with comprehensive debugging features.
    /// </summary>
    public static class EventBus {
        private static readonly Dictionary<Type, List<Delegate>> _subscribers = new();
        
        #if UNITY_EDITOR || DEVELOPMENT_BUILD
        private static readonly List<string> _eventHistory = new();
        private static readonly Dictionary<string, float> _handlerTimings = new();
        private static bool _loggingEnabled = true;
        #endif

        /// <summary>
        /// Subscribe to an event type. The handler will be called whenever this event is published.
        /// </summary>
        public static void Subscribe<T>(Action<T> handler) where T : GameEvent {
            var eventType = typeof(T);
            if(!_subscribers.ContainsKey(eventType)) {
                _subscribers[eventType] = new List<Delegate>();
            }
            
            if(!_subscribers[eventType].Contains(handler)) {
                _subscribers[eventType].Add(handler);
            }
        }

        /// <summary>
        /// Unsubscribe from an event type.
        /// </summary>
        public static void Unsubscribe<T>(Action<T> handler) where T : GameEvent {
            var eventType = typeof(T);
            if(_subscribers.ContainsKey(eventType)) {
                _subscribers[eventType].Remove(handler);
                if(_subscribers[eventType].Count == 0) {
                    _subscribers.Remove(eventType);
                }
            }
        }

        /// <summary>
        /// Unsubscribe all handlers for a specific subscriber object.
        /// Useful for MonoBehaviour cleanup.
        /// </summary>
        public static void UnsubscribeAll(object subscriber) {
            var toRemove = new List<KeyValuePair<Type, Delegate>>();
            
            foreach(var kvp in _subscribers) {
                foreach(var handler in kvp.Value) {
                    if(handler.Target == subscriber) {
                        toRemove.Add(new KeyValuePair<Type, Delegate>(kvp.Key, handler));
                    }
                }
            }
            
            foreach(var pair in toRemove) {
                _subscribers[pair.Key].Remove(pair.Value);
                if(_subscribers[pair.Key].Count == 0) {
                    _subscribers.Remove(pair.Key);
                }
            }
        }

        /// <summary>
        /// Publish an event to all subscribers.
        /// Includes comprehensive debugging: missing subscriber detection, caller information, exception handling.
        /// </summary>
        public static void Publish<T>(T gameEvent) where T : GameEvent {
            var eventType = typeof(T);
            
            #if UNITY_EDITOR || DEVELOPMENT_BUILD
            if(_loggingEnabled) {
                // Missing subscriber detection
                if(!_subscribers.ContainsKey(eventType) || _subscribers[eventType].Count == 0) {
                    Debug.LogWarning($"[EventBus] {eventType.Name} published but NO SUBSCRIBERS! " +
                                   $"Is {eventType.Name} handler missing?");
                }
                
                // Caller information
                var stackTrace = new StackTrace(1, true);
                var caller = stackTrace.GetFrame(0)?.GetMethod();
                var callerInfo = caller != null 
                    ? $"{caller.DeclaringType?.Name}.{caller.Name}()" 
                    : "Unknown";
                
                var subscriberCount = _subscribers.ContainsKey(eventType) 
                    ? _subscribers[eventType].Count 
                    : 0;
                
                // Event history (keep last 100)
                var logEntry = $"[Frame {Time.frameCount}] {eventType.Name} from {callerInfo} → {subscriberCount} subscriber(s)";
                _eventHistory.Add(logEntry);
                if(_eventHistory.Count > 100) {
                    _eventHistory.RemoveAt(0);
                }
                
                Debug.Log($"[EventBus] Publishing {eventType.Name} from {callerInfo} " +
                         $"to {subscriberCount} subscriber(s)");
            }
            #endif
            
            // Publish with exception handling
            if(_subscribers.ContainsKey(eventType)) {
                // Create a copy of the list to avoid modification during iteration
                var handlers = _subscribers[eventType].ToArray();
                
                foreach(var handler in handlers) {
                    #if UNITY_EDITOR || DEVELOPMENT_BUILD
                    var startTime = _loggingEnabled ? Time.realtimeSinceStartup : 0f;
                    #endif
                    
                    try {
                        if(handler is Action<T> typedHandler) {
                            typedHandler(gameEvent);
                        }
                    } catch(Exception ex) {
                        #if UNITY_EDITOR || DEVELOPMENT_BUILD
                        if(_loggingEnabled) {
                            var stackTrace = new StackTrace(1, true);
                            var caller = stackTrace.GetFrame(0)?.GetMethod();
                            var callerInfo = caller != null 
                                ? $"{caller.DeclaringType?.Name}.{caller.Name}()" 
                                : "Unknown";
                            
                            Debug.LogError($"[EventBus] Exception in {eventType.Name} handler:\n" +
                                          $"Event: {gameEvent}\n" +
                                          $"Handler: {handler.GetType().Name}\n" +
                                          $"Publisher: {callerInfo}\n" +
                                          $"Exception: {ex}");
                        }
                        #endif
                    } finally {
                        #if UNITY_EDITOR || DEVELOPMENT_BUILD
                        if(_loggingEnabled) {
                            var duration = Time.realtimeSinceStartup - startTime;
                            if(duration > 0.01f) { // Log slow handlers (>10ms)
                                var handlerName = handler.GetType().Name;
                                Debug.LogWarning($"[EventBus] Slow handler: {handlerName} took {duration * 1000:F2}ms");
                            }
                        }
                        #endif
                    }
                }
            }
        }

        /// <summary>
        /// Clear all subscriptions. Useful for testing or scene transitions.
        /// </summary>
        public static void Clear() {
            _subscribers.Clear();
            #if UNITY_EDITOR || DEVELOPMENT_BUILD
            _eventHistory.Clear();
            _handlerTimings.Clear();
            #endif
        }

        #if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>
        /// Log all current subscriptions. Useful for debugging.
        /// </summary>
        public static void LogSubscriptions() {
            Debug.Log("=== Event Bus Subscriptions ===");
            if(_subscribers.Count == 0) {
                Debug.Log("No active subscriptions.");
                return;
            }
            
            foreach(var kvp in _subscribers) {
                Debug.Log($"{kvp.Key.Name}: {kvp.Value.Count} subscriber(s)");
                foreach(var handler in kvp.Value) {
                    var method = handler.GetType().GetMethod("Invoke");
                    Debug.Log($"  - {method?.DeclaringType?.Name}.{method?.Name}");
                }
            }
        }

        /// <summary>
        /// Print the last 100 events. Useful for debugging.
        /// </summary>
        public static void PrintEventHistory() {
            Debug.Log("=== Event Bus History (Last 100) ===");
            if(_eventHistory.Count == 0) {
                Debug.Log("No events in history.");
                return;
            }
            
            foreach(var entry in _eventHistory) {
                Debug.Log(entry);
            }
        }

        /// <summary>
        /// Clear the event history.
        /// </summary>
        public static void ClearEventHistory() {
            _eventHistory.Clear();
            Debug.Log("[EventBus] Event history cleared.");
        }

        /// <summary>
        /// Enable or disable event logging. Useful for performance or reducing log spam.
        /// </summary>
        public static void SetLoggingEnabled(bool enabled) {
            _loggingEnabled = enabled;
            Debug.Log($"[EventBus] Logging {(enabled ? "enabled" : "disabled")}.");
        }

        /// <summary>
        /// Get the current logging state.
        /// </summary>
        public static bool IsLoggingEnabled() {
            return _loggingEnabled;
        }
        #endif
    }
}

