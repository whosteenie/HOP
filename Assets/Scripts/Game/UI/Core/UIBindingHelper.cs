using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI.Core {
    /// <summary>
    /// Helper for validating required UI elements and managing bind/unbind lifecycle
    /// to prevent missing-element crashes and duplicate event handler registration.
    /// </summary>
    public static class UIBindingHelper {
        /// <summary>
        /// Queries a required element from the root. Logs a clear error if missing.
        /// Use this instead of direct Q() calls for elements that must exist.
        /// </summary>
        public static T QRequired<T>(VisualElement root, string name, string context = null) where T : VisualElement {
            var element = root.Q<T>(name);
            if(element != null) return element;
            var ctx = string.IsNullOrEmpty(context) ? "UI" : context;
            Debug.LogError($"[{ctx}] Required UI element '{name}' (type: {typeof(T).Name}) not found in root '{root.name}'");
            return null;
        }

        /// <summary>
        /// Validates multiple required elements and returns a list of missing element names.
        /// Useful for batch validation during view initialization.
        /// </summary>
        public static List<string> ValidateRequiredElements(VisualElement root, Dictionary<string, Type> requiredElements, string context = null) {
            var missing = new List<string>();
            foreach(var kvp in requiredElements) {
                var element = root.Q(kvp.Key);
                if(element == null || !kvp.Value.IsInstanceOfType(element)) {
                    missing.Add($"{kvp.Key} ({kvp.Value.Name})");
                }
            }

            if(missing.Count <= 0) return missing;
            var ctx = string.IsNullOrEmpty(context) ? "UI" : context;
            Debug.LogError($"[{ctx}] Missing required UI elements in '{root.name}': {string.Join(", ", missing)}");

            return missing;
        }

        /// <summary>
        /// Safely unregisters a callback, preventing errors if already unregistered or element is null.
        /// </summary>
        public static void SafeUnregister<TEvent>(VisualElement element, EventCallback<TEvent> callback) where TEvent : EventBase<TEvent>, new() {
            if(element == null || callback == null) return;
            try {
                element.UnregisterCallback(callback);
            } catch {
                // Already unregistered or element destroyed - ignore
            }
        }

        /// <summary>
        /// Safely unregisters a clicked callback from a button.
        /// </summary>
        public static void SafeUnregisterClick(Button button, Action callback) {
            if(button == null || callback == null) return;
            try {
                button.clicked -= callback;
            } catch {
                // Already unregistered or button destroyed - ignore
            }
        }
    }
}
