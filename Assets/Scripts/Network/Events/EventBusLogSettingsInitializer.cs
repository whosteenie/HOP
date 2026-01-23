using UnityEngine;

namespace Network.Events {
    /// <summary>
    /// Simple MonoBehaviour to initialize EventBus log settings at startup.
    /// Attach this to a GameObject in your initial scene (e.g., a GameManager or NetworkManager).
    /// Assign the EventBusLogSettings ScriptableObject in the inspector.
    /// </summary>
    public class EventBusLogSettingsInitializer : MonoBehaviour {
        [Header("Event Bus Log Settings")]
        [Tooltip("Assign the EventBusLogSettings ScriptableObject here. " +
                 "Create one via: Right-click in Project → Create → Network → Event Bus Log Settings")]
        [SerializeField] private EventBusLogSettings logSettings;
        
        private void Awake() {
            if(logSettings != null) {
                EventBus.SetLogSettings(logSettings);
                Debug.Log($"[EventBusLogSettingsInitializer] EventBus log settings initialized from '{logSettings.name}'");
            } else {
                Debug.LogWarning("[EventBusLogSettingsInitializer] No log settings assigned. EventBus will log all events by default.");
            }
        }
    }
}

