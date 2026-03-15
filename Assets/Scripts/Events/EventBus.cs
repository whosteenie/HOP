using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;

namespace Events {
    /// <summary>
    /// Central event bus for decoupled communication between systems.
    /// Provides type-safe event publishing and subscription with comprehensive debugging features.
    /// </summary>
    public static class EventBus {
        private static readonly Dictionary<Type, List<Delegate>> Subscribers = new();
        private static long correlationSequence;

        [ThreadStatic] private static CorrelationContext currentCorrelationContext;

        // Log settings available in all builds (but only used in editor/dev)
        private static EventBusLogSettings logSettings;

        private struct CorrelationContext {
            public string CorrelationId;
            public string ParentCorrelationId;
            public int Depth;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private static readonly List<string> EventHistory = new();
        private static readonly Dictionary<string, float> HandlerTimings = new();
        private static bool loggingEnabled = true;

        // Events that are optional (for future systems like analytics) - no warning if no subscribers
        private static readonly HashSet<Type> OptionalEvents = new() {
            typeof(PlayerDiedEvent),
            typeof(PlayerDamagedEvent),
            typeof(PlayerRespawnedEvent),
            typeof(WeaponSwitchedEvent),
            typeof(GrappleStartedEvent),
            typeof(GrappleEndedEvent),
            typeof(GrappleAnimFirstFrameEvent),
            typeof(GrappleAnimHideEvent),
            typeof(HopballPickedUpEvent),
            typeof(HopballDroppedEvent),
            typeof(MatchStartedEvent),
            typeof(MatchEndedEvent),
            typeof(PreMatchCountdownEvent),
            typeof(PreMatchWaitingForPlayersEvent),
            typeof(MatchTimeUpdatedEvent),
            typeof(PostMatchStartedEvent),
            typeof(GameMenuReadyEvent),
            typeof(MatchTimerReadyEvent),
            typeof(SessionPropertiesRefreshedEvent),
            typeof(FrontStatusChangedEvent),
            typeof(ResolutionChangedEvent),
            typeof(GameSettingsChangedEvent),
            typeof(SocialSettingsChangedEvent),
            typeof(PlayerMuteChangedEvent),
            typeof(BindingsAppliedEvent),
            typeof(ChallengesUpdatedEvent),
            typeof(HideGrappleUIEvent),
            typeof(ShowGrappleUIEvent),
            typeof(VoiceParticipantSpeechChangedEvent),
            typeof(VoiceParticipantRemovedEvent),
            typeof(VoiceLocalPttStateChangedEvent),
            typeof(VoiceOverlayResetEvent),
            typeof(ChatMessageReceivedEvent),
            typeof(IndicatorTargetStateChangedEvent)
        };

        // Editor window access
        public static List<string> GetEventHistory() => EventHistory;
        public static Dictionary<Type, List<Delegate>> GetSubscribers() => Subscribers;
        public static Dictionary<string, float> GetHandlerTimings() => HandlerTimings;
#endif

        /// <summary>
        /// Subscribe to an event type. The handler will be called whenever this event is published.
        /// </summary>
        public static void Subscribe<T>(Action<T> handler) where T : GameEvent {
            var eventType = typeof(T);
            if(!Subscribers.ContainsKey(eventType)) {
                Subscribers[eventType] = new List<Delegate>();
            }

            if(Subscribers[eventType].Contains(handler)) return;
            Subscribers[eventType].Add(handler);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if(loggingEnabled) {
                // Subscription validation - warn if subscribing to events that might not have publishers
                ValidateSubscription(handler);
            }
#endif
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>
        /// Validates that a subscription makes sense (e.g., manager is initialized).
        /// This is a best-effort check and may not catch all cases.
        /// </summary>
        private static void ValidateSubscription<T>(Action<T> handler) where T : GameEvent {
            // Check if handler's target is a MonoBehaviour that might not be initialized
            if(handler.Target is not MonoBehaviour monoBehaviour) return;
            if(monoBehaviour == null || !monoBehaviour.gameObject.activeInHierarchy) {
                Debug.LogWarning($"[EventBus] Subscribing to {typeof(T).Name} from inactive/destroyed MonoBehaviour: " +
                                 $"{handler.Target.GetType().Name}. Handler may not receive events.");
            }
        }
#endif

        /// <summary>
        /// Unsubscribe from an event type.
        /// </summary>
        public static void Unsubscribe<T>(Action<T> handler) where T : GameEvent {
            var eventType = typeof(T);
            if(!Subscribers.TryGetValue(eventType, out var subscriber)) return;
            subscriber.Remove(handler);
            if(Subscribers[eventType].Count == 0) {
                Subscribers.Remove(eventType);
            }
        }

        /// <summary>
        /// Unsubscribe all handlers for a specific subscriber object.
        /// Useful for MonoBehaviour cleanup.
        /// </summary>
        public static void UnsubscribeAll(object subscriber) {
            var toRemove = new List<KeyValuePair<Type, Delegate>>();

            foreach(var kvp in Subscribers) {
                foreach(var handler in kvp.Value) {
                    if(handler.Target == subscriber) {
                        toRemove.Add(new KeyValuePair<Type, Delegate>(kvp.Key, handler));
                    }
                }
            }

            foreach(var pair in toRemove) {
                Subscribers[pair.Key].Remove(pair.Value);
                if(Subscribers[pair.Key].Count == 0) {
                    Subscribers.Remove(pair.Key);
                }
            }
        }

        /// <summary>
        /// Publish an event to all subscribers.
        /// Includes comprehensive debugging: missing subscriber detection, caller information, exception handling.
        /// </summary>
        public static void Publish<T>(T gameEvent,
            [CallerMemberName] string callerMember = "",
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0) where T : GameEvent {
            var eventType = typeof(T);
            var previousCorrelationContext = BeginPublishContext(gameEvent);

            try {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                // Check if logging is enabled for this specific event type
                var shouldLog = loggingEnabled && ShouldLogEvent(eventType);

                if(shouldLog) {
                    // Missing subscriber detection (only warn for non-optional events)
                    if(!Subscribers.ContainsKey(eventType) || Subscribers[eventType].Count == 0) {
                        // Only warn if this event is not marked as optional
                        if(!OptionalEvents.Contains(eventType)) {
                            Debug.LogWarning($"[EventBus] {eventType.Name} published but NO SUBSCRIBERS! " +
                                             $"Is {eventType.Name} handler missing?");
                        }
                    }

                    // Caller information
                    var callerInfo = BuildCallerInfo(callerMember, callerFile, callerLine);

                    var subscriberCount = 0;
                    if(Subscribers.TryGetValue(eventType, out var subscriber)) {
                        if(subscriber != null) {
                            subscriberCount = subscriber.Count;
                        }
                    }

                    // Event history (keep last 100)
                    var logEntry =
                        $"[Frame {Time.frameCount}] {eventType.Name} from {callerInfo} -> {subscriberCount} subscriber(s) " +
                        $"(corr={GetCurrentCorrelationId()})";
                    EventHistory.Add(logEntry);
                    if(EventHistory.Count > 100) {
                        EventHistory.RemoveAt(0);
                    }

                    Debug.Log($"[EventBus] Publishing {eventType.Name} from {callerInfo} " +
                              $"to {subscriberCount} subscriber(s) (corr={GetCurrentCorrelationId()})");
                }
#endif

                // Publish with exception handling
                if(!Subscribers.ContainsKey(eventType)) return;
                {
                    // Create a copy of the list to avoid modification during iteration
                    var handlers = Subscribers[eventType].ToArray();

                    foreach(var handler in handlers) {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                        shouldLog = loggingEnabled && ShouldLogEvent(eventType);
                        var startTime = shouldLog ? Time.realtimeSinceStartup : 0f;
#endif

                        try {
                            if(handler is Action<T> typedHandler) {
                                typedHandler(gameEvent);
                            }
                        } catch(Exception ex) {
                            var shouldRethrow = EventBusFailureDiagnostics.RecordHandlerException(
                                eventType,
                                gameEvent,
                                handler,
                                ex,
                                callerMember,
                                callerFile,
                                callerLine);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                            shouldLog = loggingEnabled && ShouldLogEvent(eventType);
                            if(shouldLog) {
                                var callerInfo = BuildCallerInfo(callerMember, callerFile, callerLine);

                                Debug.LogError($"[EventBus] Exception in {eventType.Name} handler:\n" +
                                               $"Event: {gameEvent}\n" +
                                               $"Handler: {handler.GetType().Name}\n" +
                                               $"Publisher: {callerInfo}\n" +
                                               $"CorrelationId: {GetCurrentCorrelationId()}\n" +
                                               $"Exception: {ex}");
                            }
#endif
                            if(shouldRethrow) {
                                throw;
                            }
                        } finally {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                            shouldLog = loggingEnabled && ShouldLogEvent(eventType);
                            if(shouldLog) {
                                var duration = Time.realtimeSinceStartup - startTime;
                                if(duration > 0.01f) { // Log slow handlers (>10ms)
                                    var handlerName = handler.GetType().Name;
                                    Debug.LogWarning($"[EventBus] Slow handler: {handlerName} took {duration * 1000:F2}ms");
                                }

                                // Track handler timings for editor window
                                var declaringType = handler.Method.DeclaringType;
                                var typeName = declaringType != null ? declaringType.Name : "Unknown";
                                var handlerKey = $"{typeName}.{handler.Method.Name}";
                                HandlerTimings.TryAdd(handlerKey, 0f);
                                HandlerTimings[handlerKey] =
                                    Mathf.Max(HandlerTimings[handlerKey], duration * 1000f); // Store max in ms
                            }
#endif
                        }
                    }
                }
            } finally {
                currentCorrelationContext = previousCorrelationContext;
            }
        }
        /// <summary>
        /// Clear all subscriptions. Useful for testing or scene transitions.
        /// </summary>
        public static void Clear() {
            Subscribers.Clear();
            currentCorrelationContext = default;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            EventHistory.Clear();
            HandlerTimings.Clear();
#endif
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>
        /// Log all current subscriptions. Useful for debugging.
        /// </summary>
        public static void LogSubscriptions() {
            Debug.Log("=== Event Bus Subscriptions ===");
            if(Subscribers.Count == 0) {
                Debug.Log("No active subscriptions.");
                return;
            }

            foreach(var kvp in Subscribers) {
                Debug.Log($"{kvp.Key.Name}: {kvp.Value.Count} subscriber(s)");
                foreach(var handler in kvp.Value) {
                    var method = handler.GetType().GetMethod("Invoke");
                    var methodName = "Unknown";
                    var typeName = "Unknown";
                    if(method != null) {
                        methodName = method.Name;
                        var declaringType = method.DeclaringType;
                        if(declaringType != null) typeName = declaringType.Name;
                    }
                    Debug.Log($"  - {typeName}.{methodName}");
                }
            }
        }

        /// <summary>
        /// Print the last 100 events. Useful for debugging.
        /// </summary>
        public static void PrintEventHistory() {
            Debug.Log("=== Event Bus History (Last 100) ===");
            if(EventHistory.Count == 0) {
                Debug.Log("No events in history.");
                return;
            }

            foreach(var entry in EventHistory) {
                Debug.Log(entry);
            }
        }

        /// <summary>
        /// Clear the event history.
        /// </summary>
        public static void ClearEventHistory() {
            EventHistory.Clear();
            Debug.Log("[EventBus] Event history cleared.");
        }

        /// <summary>
        /// Enable or disable event logging. Useful for performance or reducing log spam.
        /// </summary>
        public static void SetLoggingEnabled(bool enabled) {
            loggingEnabled = enabled;
            Debug.Log($"[EventBus] Logging {(enabled ? "enabled" : "disabled")}.");
        }
#endif

        /// <summary>
        /// Get the current logging state.
        /// </summary>
        public static bool IsLoggingEnabled() {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            return loggingEnabled;
#else
            return false;
#endif
        }

        /// <summary>
        /// Set the log settings ScriptableObject. Call this once at startup (e.g., from a manager).
        /// Available in all builds (settings are stored but only used in editor/dev builds).
        /// </summary>
        public static void SetLogSettings(EventBusLogSettings settings) {
            EventBusFailureDiagnostics.ApplyLogSettings(settings);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            logSettings = settings;
#else
            // In non-dev builds, settings are ignored but method exists to prevent compilation errors
#endif
        }

        /// <summary>
        /// Enables/disables file logging for EventBus failures at runtime.
        /// </summary>
        public static void SetFailureFileLogging(bool enabled) {
            EventBusFailureDiagnostics.SetFileLoggingEnabled(enabled);
        }

        /// <summary>
        /// Enables/disables EventBus failure capture at runtime.
        /// </summary>
        public static void SetFailureCaptureEnabled(bool enabled) {
            EventBusFailureDiagnostics.SetFailureCaptureEnabled(enabled);
        }

        /// <summary>
        /// Enables/disables fail-fast behavior for EventBus handler exceptions.
        /// </summary>
        public static void SetFailureFailFastEnabled(bool enabled) {
            EventBusFailureDiagnostics.SetFailFastEnabled(enabled);
        }

        /// <summary>
        /// Returns the current session failure log path, if active.
        /// </summary>
        public static string GetFailureLogPath() {
            return EventBusFailureDiagnostics.GetActiveLogPath();
        }

        /// <summary>
        /// Returns the correlation ID currently active on this thread, if any.
        /// </summary>
        private static string GetCurrentCorrelationId() {
            return currentCorrelationContext.CorrelationId ?? string.Empty;
        }

        /// <summary>
        /// Returns the parent correlation ID currently active on this thread, if any.
        /// </summary>
        public static string GetParentCorrelationId() {
            return currentCorrelationContext.ParentCorrelationId ?? string.Empty;
        }

        /// <summary>
        /// Returns the current correlation nesting depth for this thread.
        /// </summary>
        public static int GetCurrentCorrelationDepth() {
            return currentCorrelationContext.Depth;
        }

        /// <summary>
        /// Creates a new correlation ID using a low-allocation monotonic sequence.
        /// </summary>
        public static string GenerateCorrelationId() {
            return CreateCorrelationId();
        }

        /// <summary>
        /// Applies the current EventBus correlation chain to an event for forwarding scenarios (e.g., RPC boundaries).
        /// </summary>
        public static void AttachCurrentCorrelation(GameEvent gameEvent) {
            if(gameEvent == null) return;

            var parentCorrelationId = currentCorrelationContext.CorrelationId;
            var correlationId = string.IsNullOrWhiteSpace(parentCorrelationId)
                ? CreateCorrelationId()
                : parentCorrelationId;
            var depth = currentCorrelationContext.Depth > 0 ? currentCorrelationContext.Depth : 1;

            gameEvent.CorrelationId = correlationId;
            gameEvent.ParentCorrelationId = currentCorrelationContext.ParentCorrelationId ?? string.Empty;
            gameEvent.CorrelationDepth = depth;
        }

        /// <summary>
        /// Applies an externally supplied correlation context to an event.
        /// </summary>
        public static void AttachCorrelation(GameEvent gameEvent, string correlationId, string parentCorrelationId = "", int depth = 1) {
            if(gameEvent == null) return;

            gameEvent.CorrelationId = NormalizeCorrelationId(correlationId);
            gameEvent.ParentCorrelationId = NormalizeOptionalCorrelationId(parentCorrelationId);
            gameEvent.CorrelationDepth = Mathf.Max(1, depth);
        }

        /// <summary>
        /// Begins a scoped correlation context for inbound boundaries (e.g., RPC/message handlers).
        /// </summary>
        public static IDisposable BeginCorrelationScope(string correlationId, string parentCorrelationId = "", int depth = 0) {
            var previous = currentCorrelationContext;
            currentCorrelationContext = new CorrelationContext {
                CorrelationId = NormalizeCorrelationId(correlationId),
                ParentCorrelationId = NormalizeOptionalCorrelationId(parentCorrelationId),
                Depth = Mathf.Max(0, depth)
            };
            return new CorrelationScope(previous);
        }

        private static CorrelationContext BeginPublishContext(GameEvent gameEvent) {
            var previous = currentCorrelationContext;
            var hasParentScope = string.IsNullOrWhiteSpace(previous.CorrelationId) == false;

            var correlationId = gameEvent != null && string.IsNullOrWhiteSpace(gameEvent.CorrelationId) == false
                ? gameEvent.CorrelationId.Trim()
                : CreateCorrelationId();

            var parentCorrelationId = gameEvent != null && string.IsNullOrWhiteSpace(gameEvent.ParentCorrelationId) == false
                ? gameEvent.ParentCorrelationId.Trim()
                : hasParentScope
                    ? previous.CorrelationId
                    : string.Empty;

            var depth = hasParentScope
                ? previous.Depth + 1
                : gameEvent is { CorrelationDepth: > 0 }
                    ? gameEvent.CorrelationDepth
                    : 1;

            if(gameEvent != null) {
                gameEvent.CorrelationId = correlationId;
                gameEvent.ParentCorrelationId = parentCorrelationId;
                gameEvent.CorrelationDepth = depth;
            }

            currentCorrelationContext = new CorrelationContext {
                CorrelationId = correlationId,
                ParentCorrelationId = parentCorrelationId,
                Depth = depth
            };

            return previous;
        }

        private static string CreateCorrelationId() {
            var next = Interlocked.Increment(ref correlationSequence);
            return $"{Time.frameCount:x8}-{next:x8}";
        }

        private static string NormalizeCorrelationId(string correlationId) {
            return string.IsNullOrWhiteSpace(correlationId) ? CreateCorrelationId() : correlationId.Trim();
        }

        private static string NormalizeOptionalCorrelationId(string correlationId) {
            return string.IsNullOrWhiteSpace(correlationId) ? string.Empty : correlationId.Trim();
        }

        private readonly struct CorrelationScope : IDisposable {
            private readonly CorrelationContext _previous;

            public CorrelationScope(CorrelationContext previous) {
                _previous = previous;
            }

            public void Dispose() {
                currentCorrelationContext = _previous;
            }
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>
        /// Checks if a specific event type should be logged based on the log settings.
        /// </summary>
        private static bool ShouldLogEvent(Type eventType) {
            // If no settings assigned, default to logging everything (backward compatible)
            return logSettings == null || logSettings.IsLoggingEnabledFor(eventType);
        }
#endif

        private static string BuildCallerInfo(string callerMember, string callerFile, int callerLine) {
            if(string.IsNullOrEmpty(callerFile)) {
                return string.IsNullOrEmpty(callerMember) ? "Unknown" : callerMember;
            }

            var file = System.IO.Path.GetFileName(callerFile);
            if(string.IsNullOrEmpty(file)) file = callerFile;
            var member = string.IsNullOrEmpty(callerMember) ? "UnknownMember" : callerMember;
            return $"{file}:{callerLine} ({member})";
        }
    }
}

