using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Network.Events.Editor {
    [CustomEditor(typeof(EventBusLogSettings))]
    public class EventBusLogSettingsEditor : UnityEditor.Editor {
        private const string CategoryCritical = "Critical / Failures";
        private const string CategoryMatchSession = "Match / Session";
        private const string CategoryPlayerCombat = "Player / Combat";
        private const string CategoryUiHud = "UI / HUD";
        private const string CategoryMovement = "Movement / Ability";
        private const string CategoryHopball = "Hopball";
        private const string CategoryAudio = "Audio";
        private const string CategoryOther = "Other";

        private static readonly string[] CategoryOrder = {
            CategoryCritical,
            CategoryMatchSession,
            CategoryPlayerCombat,
            CategoryUiHud,
            CategoryMovement,
            CategoryHopball,
            CategoryAudio,
            CategoryOther
        };

        private readonly Dictionary<string, bool> _categoryFoldouts = new();

        private SerializedProperty _loggingEnabled;
        private SerializedProperty _eventLogSettings;
        private SerializedProperty _failureCaptureEnabled;
        private SerializedProperty _failureFileLoggingEnabled;
        private SerializedProperty _failureEchoToUnityConsole;
        private SerializedProperty _failurePublisherStackTrace;
        private SerializedProperty _failureIncludeEventPayload;
        private SerializedProperty _failureFailFast;
        private SerializedProperty _failureFlushIntervalSeconds;
        private SerializedProperty _failureMaxFileSizeMb;
        private SerializedProperty _failureMaxRecordsPerSession;
        private SerializedProperty _failureImmediateFlushOnError;
        private SerializedProperty _failureRedactIdentifiers;

        private void OnEnable() {
            _loggingEnabled = serializedObject.FindProperty("loggingEnabled");
            _eventLogSettings = serializedObject.FindProperty("eventLogSettings");
            _failureCaptureEnabled = serializedObject.FindProperty("failureCaptureEnabled");
            _failureFileLoggingEnabled = serializedObject.FindProperty("failureFileLoggingEnabled");
            _failureEchoToUnityConsole = serializedObject.FindProperty("failureEchoToUnityConsole");
            _failurePublisherStackTrace = serializedObject.FindProperty("failureIncludePublisherStackTrace");
            _failureIncludeEventPayload = serializedObject.FindProperty("failureIncludeEventPayload");
            _failureFailFast = serializedObject.FindProperty("failureFailFastOnHandlerException");
            _failureFlushIntervalSeconds = serializedObject.FindProperty("failureFlushIntervalSeconds");
            _failureMaxFileSizeMb = serializedObject.FindProperty("failureMaxFileSizeMb");
            _failureMaxRecordsPerSession = serializedObject.FindProperty("failureMaxRecordsPerSession");
            _failureImmediateFlushOnError = serializedObject.FindProperty("failureImmediateFlushOnError");
            _failureRedactIdentifiers = serializedObject.FindProperty("failureRedactIdentifiers");

            foreach(var category in CategoryOrder) {
                _categoryFoldouts.TryAdd(category, category == CategoryCritical);
            }
        }

        public override void OnInspectorGUI() {
            serializedObject.Update();

            DrawGlobalSection();
            EditorGUILayout.Space();
            DrawPerEventSection();
            EditorGUILayout.Space();
            DrawFailureDiagnostics();
            EditorGUILayout.Space();
            DrawHelpSection();

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawGlobalSection() {
            EditorGUILayout.LabelField("Global Settings", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_loggingEnabled);
        }

        private void DrawPerEventSection() {
            EditorGUILayout.LabelField("Per-Event Logging", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Per-event toggles control EventBus debug logging in console and the Event Bus Debug Window stream/history/perf data.",
                MessageType.Info);

            EditorGUILayout.BeginHorizontal();
            if(GUILayout.Button("Auto-Populate All Event Types")) {
                AutoPopulateEventTypes((EventBusLogSettings)target);
                serializedObject.Update();
            }

            if(GUILayout.Button("Clear All Entries")) {
                _eventLogSettings.ClearArray();
            }
            EditorGUILayout.EndHorizontal();

            if(_eventLogSettings.arraySize == 0) {
                EditorGUILayout.HelpBox("No event entries configured. Auto-populate to create per-event toggles.", MessageType.Warning);
                return;
            }

            DrawCategoryQuickActions();
            DrawCategoryGroups();
        }

        private void DrawCategoryQuickActions() {
            EditorGUILayout.BeginHorizontal();
            if(GUILayout.Button("Enable Critical / Failures")) {
                SetCategoryEnabled(CategoryCritical, true);
            }

            if(GUILayout.Button("Disable Critical / Failures")) {
                SetCategoryEnabled(CategoryCritical, false);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if(GUILayout.Button("Enable All")) {
                SetAllEnabled(true);
            }

            if(GUILayout.Button("Disable All")) {
                SetAllEnabled(false);
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawCategoryGroups() {
            var groupedIndices = BuildGroupedIndices();
            foreach(var category in CategoryOrder) {
                if(!groupedIndices.TryGetValue(category, out var indices) || indices.Count == 0) continue;

                var enabledCount = CountEnabled(indices);
                _categoryFoldouts.TryAdd(category, false);
                _categoryFoldouts[category] = EditorGUILayout.Foldout(
                    _categoryFoldouts[category],
                    $"{category} ({enabledCount}/{indices.Count})",
                    true);

                if(_categoryFoldouts[category] == false) continue;

                EditorGUI.indentLevel++;
                DrawCategoryActions(category);
                foreach(var index in indices) {
                    DrawEventRow(index);
                }
                EditorGUI.indentLevel--;
                EditorGUILayout.Space(4f);
            }
        }

        private void DrawCategoryActions(string category) {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(EditorGUI.indentLevel * 15f);
            if(GUILayout.Button("All On", GUILayout.Width(80f))) {
                SetCategoryEnabled(category, true);
            }

            if(GUILayout.Button("All Off", GUILayout.Width(80f))) {
                SetCategoryEnabled(category, false);
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawEventRow(int index) {
            var entry = _eventLogSettings.GetArrayElementAtIndex(index);
            var typeNameProperty = entry.FindPropertyRelative("eventTypeName");
            var enabledProperty = entry.FindPropertyRelative("enabled");

            var label = ToShortTypeName(typeNameProperty.stringValue);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(EditorGUI.indentLevel * 15f);
            enabledProperty.boolValue = EditorGUILayout.ToggleLeft(label, enabledProperty.boolValue);
            EditorGUILayout.EndHorizontal();
        }

        private Dictionary<string, List<int>> BuildGroupedIndices() {
            var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach(var category in CategoryOrder) {
                groups[category] = new List<int>();
            }

            for(var i = 0; i < _eventLogSettings.arraySize; i++) {
                var entry = _eventLogSettings.GetArrayElementAtIndex(i);
                var typeNameProperty = entry.FindPropertyRelative("eventTypeName");
                var category = Categorize(typeNameProperty.stringValue);
                groups[category].Add(i);
            }

            return groups;
        }

        private int CountEnabled(List<int> indices) {
            var enabledCount = 0;
            foreach(var index in indices) {
                var entry = _eventLogSettings.GetArrayElementAtIndex(index);
                var enabledProperty = entry.FindPropertyRelative("enabled");
                if(enabledProperty.boolValue) {
                    enabledCount++;
                }
            }
            return enabledCount;
        }

        private void SetAllEnabled(bool enabled) {
            for(var i = 0; i < _eventLogSettings.arraySize; i++) {
                var entry = _eventLogSettings.GetArrayElementAtIndex(i);
                var enabledProperty = entry.FindPropertyRelative("enabled");
                enabledProperty.boolValue = enabled;
            }
        }

        private void SetCategoryEnabled(string category, bool enabled) {
            for(var i = 0; i < _eventLogSettings.arraySize; i++) {
                var entry = _eventLogSettings.GetArrayElementAtIndex(i);
                var typeNameProperty = entry.FindPropertyRelative("eventTypeName");
                if(Categorize(typeNameProperty.stringValue) != category) continue;

                var enabledProperty = entry.FindPropertyRelative("enabled");
                enabledProperty.boolValue = enabled;
            }
        }

        private static string Categorize(string fullTypeName) {
            var typeName = ToShortTypeName(fullTypeName);

            if(typeName.Contains("Critical", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("NotFound", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Failed", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Disconnected", StringComparison.OrdinalIgnoreCase)) {
                return CategoryCritical;
            }

            if(typeName.Contains("Match", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Session", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Lobby", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Relay", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("PlayersChanged", StringComparison.OrdinalIgnoreCase)) {
                return CategoryMatchSession;
            }

            if(typeName.Contains("HUD", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("KillFeed", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Scoreboard", StringComparison.OrdinalIgnoreCase)) {
                return CategoryUiHud;
            }

            if(typeName.Contains("Grapple", StringComparison.OrdinalIgnoreCase)) {
                return CategoryMovement;
            }

            if(typeName.Contains("Hopball", StringComparison.OrdinalIgnoreCase)) {
                return CategoryHopball;
            }

            if(typeName.Contains("Sound", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Audio", StringComparison.OrdinalIgnoreCase)) {
                return CategoryAudio;
            }

            if(typeName.Contains("Player", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Weapon", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Ammo", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Health", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Multiplier", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Tag", StringComparison.OrdinalIgnoreCase)) {
                return CategoryPlayerCombat;
            }

            return CategoryOther;
        }

        private static string ToShortTypeName(string fullTypeName) {
            if(string.IsNullOrWhiteSpace(fullTypeName)) {
                return "UnknownEvent";
            }

            var separator = fullTypeName.LastIndexOf('.');
            if(separator < 0 || separator == fullTypeName.Length - 1) {
                return fullTypeName;
            }
            return fullTypeName[(separator + 1)..];
        }

        private void DrawFailureDiagnostics() {
            EditorGUILayout.LabelField("Failure Diagnostics", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_failureCaptureEnabled);
            EditorGUILayout.PropertyField(_failureFileLoggingEnabled);
            EditorGUILayout.PropertyField(_failureEchoToUnityConsole);
            EditorGUILayout.PropertyField(_failurePublisherStackTrace);
            EditorGUILayout.PropertyField(_failureIncludeEventPayload);
            EditorGUILayout.PropertyField(_failureFailFast);
            EditorGUILayout.PropertyField(_failureFlushIntervalSeconds);
            EditorGUILayout.PropertyField(_failureMaxFileSizeMb);
            EditorGUILayout.PropertyField(_failureMaxRecordsPerSession);
            EditorGUILayout.PropertyField(_failureImmediateFlushOnError);
            EditorGUILayout.PropertyField(_failureRedactIdentifiers);
        }

        private static void DrawHelpSection() {
            EditorGUILayout.LabelField("Usage", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "1. Assign this ScriptableObject through EventBusLogSettingsInitializer.\n" +
                "2. Use category toggles to quickly scope logging.\n" +
                "3. Keep Critical / Failures enabled for low-noise operational visibility.",
                MessageType.Info);
        }

        private static void AutoPopulateEventTypes(EventBusLogSettings settings) {
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

            settings.eventLogSettings.Clear();

            foreach(var eventType in eventTypes) {
                var fullName = eventType.FullName;
                if(settings.eventLogSettings.Any(e => e.eventTypeName == fullName)) {
                    continue;
                }

                settings.eventLogSettings.Add(new EventLogEntry {
                    eventTypeName = fullName,
                    enabled = true
                });
            }

            EditorUtility.SetDirty(settings);
            Debug.Log($"[EventBusLogSettings] Auto-populated {eventTypes.Count} event types.");
        }
    }
}
