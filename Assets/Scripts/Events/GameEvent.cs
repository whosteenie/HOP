using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Netcode;
using UnityEngine;

namespace Events {
    /// <summary>
    /// Base class for all game events.
    /// All events must inherit from this class to be published through the EventBus.
    /// </summary>
    public abstract class GameEvent {
        [NonSerialized] private EventBusContextValues _contextValues;

        /// <summary>
        /// Correlation identifier for this event publish chain.
        /// Automatically assigned by EventBus.Publish when missing.
        /// </summary>
        public string CorrelationId { get; internal set; } = string.Empty;

        /// <summary>
        /// Parent correlation identifier when this event was published from another event handler.
        /// </summary>
        public string ParentCorrelationId { get; internal set; } = string.Empty;

        /// <summary>
        /// Publish depth in the current correlation chain (1 = root publish).
        /// </summary>
        public int CorrelationDepth { get; internal set; }

        /// <summary>
        /// Optional key/value context captured at publish sites.
        /// Lazily allocated and only materialized when used.
        /// </summary>
        public EventBusContextValues ContextValues => _contextValues ??= new EventBusContextValues();

        public void SetContext(string key, string value) {
            ContextValues.Add(key, value);
        }

        public void SetContext(string key, int value) {
            ContextValues.Add(key, value);
        }

        public void SetContext(string key, long value) {
            ContextValues.Add(key, value);
        }

        public void SetContext(string key, bool value) {
            ContextValues.Add(key, value);
        }

        public void SetContext(string key, float value) {
            ContextValues.Add(key, value);
        }

        internal string BuildContextSummary() {
            return _contextValues == null ? string.Empty : _contextValues.ToCompactString();
        }
    }

    /// <summary>
    /// Shared key/value context container used for EventBus diagnostics enrichment.
    /// </summary>
    [Serializable]
    public sealed class EventBusContextValues {
        private const int MaxEntries = 24;
        private const int MaxValueLength = 160;

        [Serializable]
        private struct ContextEntry {
            public string key;
            public string value;
        }

        [SerializeField] private List<ContextEntry> entries;

        public bool HasEntries => entries is { Count: > 0 };

        public void Add(string key, string value) {
            if(string.IsNullOrWhiteSpace(key)) return;

            var normalizedKey = key.Trim();
            var normalizedValue = value == null ? string.Empty : value.Trim();
            if(normalizedValue.Length > MaxValueLength) {
                normalizedValue = normalizedValue[..MaxValueLength];
            }

            entries ??= new List<ContextEntry>(4);

            for(var i = 0; i < entries.Count; i++) {
                if(!string.Equals(entries[i].key, normalizedKey, StringComparison.Ordinal)) continue;
                entries[i] = new ContextEntry {
                    key = normalizedKey,
                    value = normalizedValue
                };
                return;
            }

            if(entries.Count >= MaxEntries) return;

            entries.Add(new ContextEntry {
                key = normalizedKey,
                value = normalizedValue
            });
        }

        public void Add(string key, int value) {
            Add(key, value.ToString(CultureInfo.InvariantCulture));
        }

        public void Add(string key, long value) {
            Add(key, value.ToString(CultureInfo.InvariantCulture));
        }

        public void Add(string key, bool value) {
            Add(key, value ? "true" : "false");
        }

        public void Add(string key, float value) {
            Add(key, value.ToString("0.###", CultureInfo.InvariantCulture));
        }

        public void AddUnityObject(string prefix, UnityEngine.Object source) {
            if(source == null) return;

            var baseKey = string.IsNullOrWhiteSpace(prefix) ? "source" : prefix.Trim();
            Add($"{baseKey}.type", source.GetType().Name);
            Add($"{baseKey}.name", source.name);
            Add($"{baseKey}.instanceId", source.GetInstanceID());

            var component = source as Component;
            if(component != null) {
                Add($"{baseKey}.path", BuildHierarchyPath(component.transform));
            }

            var networkBehaviour = source as NetworkBehaviour;
            if(networkBehaviour == null) return;
            if(networkBehaviour.NetworkObject != null) {
                Add($"{baseKey}.networkObjectId", networkBehaviour.NetworkObjectId.ToString(CultureInfo.InvariantCulture));
            }
            Add($"{baseKey}.ownerClientId", networkBehaviour.OwnerClientId.ToString(CultureInfo.InvariantCulture));
        }

        internal string ToCompactString(int maxPairs = MaxEntries) {
            if(entries == null || entries.Count == 0 || maxPairs <= 0) return string.Empty;

            var sb = new StringBuilder(128);
            var count = Mathf.Min(maxPairs, entries.Count);
            for(var i = 0; i < count; i++) {
                if(i > 0) sb.Append(';');
                sb.Append(entries[i].key);
                sb.Append('=');
                sb.Append(entries[i].value);
            }
            return sb.ToString();
        }

        internal static string BuildHierarchyPath(Transform transform) {
            if(transform == null) return string.Empty;
            var path = transform.name;
            var current = transform.parent;
            while(current != null) {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return path;
        }
    }

    /// <summary>
    /// Optional subscriber-side context provider invoked only on EventBus exception paths.
    /// </summary>
    public interface IEventBusContextProvider {
        void PopulateEventBusContext(EventBusContextValues contextValues);
    }
}

