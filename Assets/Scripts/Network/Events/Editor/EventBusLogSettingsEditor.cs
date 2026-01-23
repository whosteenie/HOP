using System;
using System.Linq;
using Network.Events;
using UnityEditor;
using UnityEngine;

namespace Network.Events.Editor {
    [CustomEditor(typeof(EventBusLogSettings))]
    public class EventBusLogSettingsEditor : UnityEditor.Editor {
        public override void OnInspectorGUI() {
            DrawDefaultInspector();
            
            var settings = (EventBusLogSettings)target;
            
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Helper Tools", EditorStyles.boldLabel);
            
            if(GUILayout.Button("Auto-Populate All Event Types")) {
                AutoPopulateEventTypes(settings);
            }
            
            if(GUILayout.Button("Clear All Entries")) {
                settings.eventLogSettings.Clear();
                EditorUtility.SetDirty(settings);
            }
            
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "To use these settings:\n" +
                "1. Assign this ScriptableObject to EventBus via EventBus.SetLogSettings() in your initialization code\n" +
                "2. Add event types to the list and toggle their enabled state\n" +
                "3. Use 'Auto-Populate All Event Types' to automatically add all GameEvent types",
                MessageType.Info
            );
        }
        
        private void AutoPopulateEventTypes(EventBusLogSettings settings) {
            // Find all types that inherit from GameEvent
            var eventTypes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(assembly => {
                    try {
                        return assembly.GetTypes();
                    } catch {
                        return Enumerable.Empty<Type>();
                    }
                })
                .Where(type => typeof(GameEvent).IsAssignableFrom(type) && 
                               !type.IsAbstract && 
                               !type.IsInterface)
                .OrderBy(type => type.Name)
                .ToList();
            
            // Clear existing entries
            settings.eventLogSettings.Clear();
            
            // Add all event types (default to enabled)
            foreach(var eventType in eventTypes) {
                var fullName = eventType.FullName;
                // Check if already exists
                if(settings.eventLogSettings.Any(e => e.eventTypeName == fullName)) {
                    continue;
                }
                
                settings.eventLogSettings.Add(new EventLogEntry {
                    eventTypeName = fullName,
                    enabled = true // Default to enabled
                });
            }
            
            EditorUtility.SetDirty(settings);
            Debug.Log($"[EventBusLogSettings] Auto-populated {eventTypes.Count} event types.");
        }
    }
}

