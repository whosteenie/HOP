using System;
using System.Collections.Generic;
using UnityEngine;

namespace Network.Events {
    /// <summary>
    /// ScriptableObject for configuring EventBus logging per event type.
    /// Create an instance in Project window, then assign it to EventBus via SetLogSettings().
    /// </summary>
    [CreateAssetMenu(fileName = "EventBusLogSettings", menuName = "Network/Event Bus Log Settings")]
    public class EventBusLogSettings : ScriptableObject {
        [Header("Global Settings")]
        [Tooltip("Master toggle for all EventBus logging. When false, no logs are generated.")]
        public bool loggingEnabled = true;
        
        [Header("Per-Event Logging")]
        [Tooltip("Events that should be logged. Events not in this list will not be logged.")]
        public List<EventLogEntry> eventLogSettings = new();

        [Header("Failure Diagnostics")]
        [Tooltip("Capture EventBus handler exceptions and route them through the centralized diagnostics path.")]
        public bool failureCaptureEnabled = true;

        [Tooltip("Write EventBus failure records to a local per-session NDJSON log file.")]
        public bool failureFileLoggingEnabled = true;

        [Tooltip("Echo EventBus failure records to Unity console output.")]
        public bool failureEchoToUnityConsole = true;

        [Tooltip("Include publisher stack trace in failure records (higher overhead on failure path).")]
        public bool failureIncludePublisherStackTrace;

        [Tooltip("Include event payload (gameEvent.ToString()) in failure records.")]
        public bool failureIncludeEventPayload;

        [Tooltip("Throw after logging handler exceptions (fail-fast mode).")]
        public bool failureFailFastOnHandlerException;

        [Tooltip("Flush interval for the per-session failure log file.")]
        public float failureFlushIntervalSeconds = 2f;

        [Tooltip("Maximum failure log file size (MB) per session.")]
        public int failureMaxFileSizeMb = 8;

        [Tooltip("Maximum number of failure records to write per session.")]
        public int failureMaxRecordsPerSession = 20000;

        [Tooltip("Flush immediately on handler exception.")]
        public bool failureImmediateFlushOnError = true;

        [Tooltip("Redact identity fields in failure records (future privacy hardening).")]
        public bool failureRedactIdentifiers;
        
        // Dictionary for fast lookup (built from eventLogSettings)
        private Dictionary<Type, bool> _eventLogLookup;
        private bool _lookupBuilt;
        
        /// <summary>
        /// Checks if logging is enabled for a specific event type.
        /// </summary>
        public bool IsLoggingEnabledFor(Type eventType) {
            if(!loggingEnabled) return false;
            
            BuildLookupIfNeeded();
            
            // If event is in the list, use its setting. Otherwise, default to false (no log)
            return _eventLogLookup.TryGetValue(eventType, out var enabled) && enabled;
        }
        
        /// <summary>
        /// Builds the fast lookup dictionary from the serialized list.
        /// </summary>
        private void BuildLookupIfNeeded() {
            if(_lookupBuilt && _eventLogLookup != null) return;
            
            _eventLogLookup = new Dictionary<Type, bool>();
            
            foreach(var entry in eventLogSettings) {
                if(entry.eventTypeName == null || string.IsNullOrEmpty(entry.eventTypeName)) continue;
                var type = Type.GetType(entry.eventTypeName);
                if(type != null) {
                    _eventLogLookup[type] = entry.enabled;
                }
            }
            
            _lookupBuilt = true;
        }
        
        /// <summary>
        /// Rebuilds the lookup when settings are modified in the inspector.
        /// </summary>
        private void OnValidate() {
            _lookupBuilt = false;
            BuildLookupIfNeeded();
        }
    }
    
    /// <summary>
    /// Serializable entry for per-event logging configuration.
    /// </summary>
    [Serializable]
    public class EventLogEntry {
        [Tooltip("Full type name of the event (e.g., 'Network.Events.PlayUISoundEvent')")]
        public string eventTypeName;
        
        [Tooltip("Whether to log this event type")]
        public bool enabled = true;
    }
}

