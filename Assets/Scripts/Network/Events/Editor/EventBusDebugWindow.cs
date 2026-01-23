#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Network.Events;
using UnityEditor;
using UnityEngine;

namespace Network.Events.Editor {
    /// <summary>
    /// Editor window for debugging the Event Bus system.
    /// Shows real-time event stream, subscriptions, performance metrics, and event history.
    /// </summary>
    public class EventBusDebugWindow : EditorWindow {
        private Vector2 _eventStreamScroll;
        private Vector2 _subscriptionsScroll;
        private Vector2 _performanceScroll;
        private Vector2 _historyScroll;
        
        private string _eventFilter = "";
        private bool _autoScroll = true;
        private bool _showSubscriptions = true;
        private bool _showPerformance = true;
        private bool _showHistory = true;
        
        private int _selectedTab = 0;
        private readonly string[] _tabNames = { "Event Stream", "Subscriptions", "Performance", "History" };
        
        private readonly Dictionary<string, int> _eventCounts = new();
        private readonly List<EventLogEntry> _eventLog = new();
        private const int MaxLogEntries = 500;
        
        private struct EventLogEntry {
            public string EventName;
            public string Caller;
            public int SubscriberCount;
            public int Frame;
            public float Time;
        }
        
        [MenuItem("Tools/Event Bus Debugger")]
        public static void ShowWindow() {
            var window = GetWindow<EventBusDebugWindow>("Event Bus Debugger");
            window.minSize = new Vector2(600, 400);
            window.Show();
        }
        
        private void OnEnable() {
            EditorApplication.update += OnEditorUpdate;
        }
        
        private void OnDisable() {
            EditorApplication.update -= OnEditorUpdate;
        }
        
        private void OnEditorUpdate() {
            if(Application.isPlaying) {
                // Capture events from EventBus history
                var history = EventBus.GetEventHistory();
                if(history != null && history.Count > 0) {
                    // Parse new entries (simple parsing - format: "[Frame X] EventName from Caller → N subscriber(s)")
                    var lastKnownCount = _eventLog.Count;
                    for(var i = lastKnownCount; i < history.Count && i < MaxLogEntries; i++) {
                        var entry = ParseHistoryEntry(history[i]);
                        if(entry.HasValue) {
                            _eventLog.Add(entry.Value);
                            
                            // Update event counts
                            if(!_eventCounts.ContainsKey(entry.Value.EventName)) {
                                _eventCounts[entry.Value.EventName] = 0;
                            }
                            _eventCounts[entry.Value.EventName]++;
                        }
                    }
                    
                    // Trim log if too large
                    if(_eventLog.Count > MaxLogEntries) {
                        _eventLog.RemoveRange(0, _eventLog.Count - MaxLogEntries);
                    }
                }
                
                Repaint();
            }
        }
        
        private EventLogEntry? ParseHistoryEntry(string entry) {
            try {
                // Format: "[Frame X] EventName from Caller → N subscriber(s)"
                if(string.IsNullOrEmpty(entry)) return null;
                
                // Find the frame number
                var frameStart = entry.IndexOf("[Frame ", StringComparison.Ordinal);
                if(frameStart < 0) return null;
                frameStart += 7; // Length of "[Frame "
                
                var frameEnd = entry.IndexOf("]", frameStart, StringComparison.Ordinal);
                if(frameEnd < 0) return null;
                
                if(!int.TryParse(entry.Substring(frameStart, frameEnd - frameStart), out var frame)) {
                    return null;
                }
                
                // Find event name (between "] " and " from ")
                var eventNameStart = entry.IndexOf("] ", frameEnd, StringComparison.Ordinal);
                if(eventNameStart < 0) return null;
                eventNameStart += 2; // Length of "] "
                
                var eventNameEnd = entry.IndexOf(" from ", eventNameStart, StringComparison.Ordinal);
                if(eventNameEnd < 0) return null;
                
                var eventName = entry.Substring(eventNameStart, eventNameEnd - eventNameStart).Trim();
                
                // Find caller (between " from " and " → ")
                var callerStart = eventNameEnd + 6; // Length of " from "
                var callerEnd = entry.IndexOf(" → ", callerStart, StringComparison.Ordinal);
                if(callerEnd < 0) return null;
                
                var caller = entry.Substring(callerStart, callerEnd - callerStart).Trim();
                
                // Find subscriber count (after " → ")
                var subscriberStart = callerEnd + 3; // Length of " → "
                var subscriberEnd = entry.IndexOf(" subscriber(s)", subscriberStart, StringComparison.Ordinal);
                if(subscriberEnd < 0) return null;
                
                if(!int.TryParse(entry.Substring(subscriberStart, subscriberEnd - subscriberStart).Trim(), out var subscriberCount)) {
                    return null;
                }
                
                return new EventLogEntry {
                    EventName = eventName,
                    Caller = caller,
                    SubscriberCount = subscriberCount,
                    Frame = frame,
                    Time = Time.realtimeSinceStartup
                };
            } catch {
                return null;
            }
        }
        
        private void OnGUI() {
            DrawToolbar();
            
            EditorGUILayout.Space();
            
            _selectedTab = GUILayout.Toolbar(_selectedTab, _tabNames);
            
            EditorGUILayout.Space();
            
            switch(_selectedTab) {
                case 0:
                    DrawEventStream();
                    break;
                case 1:
                    DrawSubscriptions();
                    break;
                case 2:
                    DrawPerformance();
                    break;
                case 3:
                    DrawHistory();
                    break;
            }
        }
        
        private void DrawToolbar() {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            
            if(GUILayout.Button("Clear Log", EditorStyles.toolbarButton, GUILayout.Width(80))) {
                _eventLog.Clear();
                _eventCounts.Clear();
            }
            
            if(GUILayout.Button("Clear History", EditorStyles.toolbarButton, GUILayout.Width(100))) {
                EventBus.ClearEventHistory();
                _eventLog.Clear();
                _eventCounts.Clear();
            }
            
            if(GUILayout.Button("Log Subscriptions", EditorStyles.toolbarButton, GUILayout.Width(120))) {
                EventBus.LogSubscriptions();
            }
            
            GUILayout.FlexibleSpace();
            
            _autoScroll = GUILayout.Toggle(_autoScroll, "Auto Scroll", EditorStyles.toolbarButton);
            
            EditorGUILayout.EndHorizontal();
            
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("Filter:", GUILayout.Width(40));
            _eventFilter = EditorGUILayout.TextField(_eventFilter, EditorStyles.toolbarSearchField);
            EditorGUILayout.EndHorizontal();
        }
        
        private void DrawEventStream() {
            EditorGUILayout.LabelField("Real-Time Event Stream", EditorStyles.boldLabel);
            EditorGUILayout.Space();
            
            if(!Application.isPlaying) {
                EditorGUILayout.HelpBox("Enter Play Mode to see events in real-time.", MessageType.Info);
                return;
            }
            
            // Event counts summary
            EditorGUILayout.LabelField("Event Counts:", EditorStyles.miniLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            var sortedCounts = _eventCounts.OrderByDescending(kvp => kvp.Value).Take(10);
            foreach(var kvp in sortedCounts) {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(kvp.Key, GUILayout.Width(200));
                EditorGUILayout.LabelField(kvp.Value.ToString(), GUILayout.Width(50));
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();
            
            EditorGUILayout.Space();
            
            // Event log
            EditorGUILayout.LabelField($"Recent Events ({_eventLog.Count}):", EditorStyles.miniLabel);
            _eventStreamScroll = EditorGUILayout.BeginScrollView(_eventStreamScroll);
            
            var filteredLog = string.IsNullOrEmpty(_eventFilter)
                ? _eventLog
                : _eventLog.Where(e => e.EventName.Contains(_eventFilter, StringComparison.OrdinalIgnoreCase) ||
                                       e.Caller.Contains(_eventFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            
            // Draw from newest to oldest
            for(var i = filteredLog.Count - 1; i >= 0; i--) {
                var entry = filteredLog[i];
                DrawEventEntry(entry);
            }
            
            EditorGUILayout.EndScrollView();
            
            if(_autoScroll && Event.current.type == EventType.Repaint) {
                _eventStreamScroll.y = Mathf.Max(0, _eventStreamScroll.y);
            }
        }
        
        private void DrawEventEntry(EventLogEntry entry) {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            
            // Color code by subscriber count
            var color = entry.SubscriberCount == 0 ? Color.red : (entry.SubscriberCount > 5 ? Color.green : Color.yellow);
            var originalColor = GUI.color;
            GUI.color = color;
            
            EditorGUILayout.LabelField(entry.EventName, EditorStyles.boldLabel, GUILayout.Width(200));
            GUI.color = originalColor;
            
            EditorGUILayout.LabelField($"Frame {entry.Frame}", GUILayout.Width(80));
            EditorGUILayout.LabelField(entry.Caller, GUILayout.Width(200));
            EditorGUILayout.LabelField($"{entry.SubscriberCount} sub(s)", GUILayout.Width(80));
            
            EditorGUILayout.EndHorizontal();
        }
        
        private void DrawSubscriptions() {
            EditorGUILayout.LabelField("Current Subscriptions", EditorStyles.boldLabel);
            EditorGUILayout.Space();
            
            if(!Application.isPlaying) {
                EditorGUILayout.HelpBox("Enter Play Mode to see subscriptions.", MessageType.Info);
                return;
            }
            
            var subscribers = EventBus.GetSubscribers();
            if(subscribers == null || subscribers.Count == 0) {
                EditorGUILayout.HelpBox("No active subscriptions.", MessageType.Info);
                return;
            }
            
            _subscriptionsScroll = EditorGUILayout.BeginScrollView(_subscriptionsScroll);
            
            foreach(var kvp in subscribers.OrderBy(k => k.Key.Name)) {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField($"{kvp.Key.Name} ({kvp.Value.Count} subscriber(s))", EditorStyles.boldLabel);
                
                foreach(var handler in kvp.Value) {
                    var method = handler.GetType().GetMethod("Invoke");
                    var declaringType = method?.DeclaringType?.Name ?? "Unknown";
                    var methodName = method?.Name ?? "Unknown";
                    
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(20);
                    EditorGUILayout.LabelField($"• {declaringType}.{methodName}", EditorStyles.miniLabel);
                    EditorGUILayout.EndHorizontal();
                }
                
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(5);
            }
            
            EditorGUILayout.EndScrollView();
        }
        
        private void DrawPerformance() {
            EditorGUILayout.LabelField("Handler Performance Metrics", EditorStyles.boldLabel);
            EditorGUILayout.Space();
            
            if(!Application.isPlaying) {
                EditorGUILayout.HelpBox("Enter Play Mode to see performance metrics.", MessageType.Info);
                return;
            }
            
            var timings = EventBus.GetHandlerTimings();
            if(timings == null || timings.Count == 0) {
                EditorGUILayout.HelpBox("No performance data available. Handlers must take >10ms to be tracked.", MessageType.Info);
                return;
            }
            
            _performanceScroll = EditorGUILayout.BeginScrollView(_performanceScroll);
            
            var sortedTimings = timings.OrderByDescending(kvp => kvp.Value).ToList();
            
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Slow Handlers (>10ms):", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);
            
            foreach(var kvp in sortedTimings) {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(kvp.Key, GUILayout.Width(300));
                
                // Color code by duration
                var color = kvp.Value > 50f ? Color.red : (kvp.Value > 20f ? Color.yellow : Color.green);
                var originalColor = GUI.color;
                GUI.color = color;
                
                EditorGUILayout.LabelField($"{kvp.Value:F2}ms", EditorStyles.boldLabel, GUILayout.Width(80));
                GUI.color = originalColor;
                
                EditorGUILayout.EndHorizontal();
            }
            
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndScrollView();
        }
        
        private void DrawHistory() {
            EditorGUILayout.LabelField("Event History (Last 100)", EditorStyles.boldLabel);
            EditorGUILayout.Space();
            
            if(!Application.isPlaying) {
                EditorGUILayout.HelpBox("Enter Play Mode to see event history.", MessageType.Info);
                return;
            }
            
            var history = EventBus.GetEventHistory();
            if(history == null || history.Count == 0) {
                EditorGUILayout.HelpBox("No events in history.", MessageType.Info);
                return;
            }
            
            _historyScroll = EditorGUILayout.BeginScrollView(_historyScroll);
            
            // Show from newest to oldest
            for(var i = history.Count - 1; i >= 0; i--) {
                var entry = history[i];
                
                // Apply filter
                if(!string.IsNullOrEmpty(_eventFilter) &&
                   !entry.Contains(_eventFilter, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }
                
                EditorGUILayout.LabelField(entry, EditorStyles.wordWrappedLabel);
            }
            
            EditorGUILayout.EndScrollView();
        }
    }
}
#endif

