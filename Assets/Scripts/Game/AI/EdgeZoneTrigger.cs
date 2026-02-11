using UnityEngine;

namespace Game.AI {
    /// <summary>
    /// Trigger collider script for detecting when bots enter/exit edge zones.
    /// Place this on trigger colliders around map edges.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class EdgeZoneTrigger : MonoBehaviour {
        [Header("Settings")]
        [Tooltip("If true, only triggers for objects with BotAgent component. If false, triggers for all objects.")]
        [SerializeField] private bool botOnly = true;
        
        private void OnTriggerEnter(Collider other) {
            if(botOnly) {
                var botAgent = other.GetComponent<BotAgent>();
                if(botAgent != null) {
                    botAgent.SetEdgeZoneState(true);
                }
            } else {
                // If not bot-only, check for BotAgent but don't require it
                var botAgent = other.GetComponent<BotAgent>();
                if(botAgent != null) {
                    botAgent.SetEdgeZoneState(true);
                }
            }
        }
        
        private void OnTriggerExit(Collider other) {
            if(botOnly) {
                var botAgent = other.GetComponent<BotAgent>();
                if(botAgent != null) {
                    botAgent.SetEdgeZoneState(false);
                }
            } else {
                var botAgent = other.GetComponent<BotAgent>();
                if(botAgent != null) {
                    botAgent.SetEdgeZoneState(false);
                }
            }
        }
        
        private void OnValidate() {
            // Ensure collider is set to trigger
            var collider = GetComponent<Collider>();
            if(collider != null && !collider.isTrigger) {
                collider.isTrigger = true;
                Debug.LogWarning($"[EdgeZoneTrigger] Collider on {gameObject.name} was not set to trigger. Auto-fixed.");
            }
        }
    }
}



