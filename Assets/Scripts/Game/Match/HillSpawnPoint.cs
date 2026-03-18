using UnityEngine;
using System.Collections.Generic;
using Diagnostics;

namespace Game.Match {
    /// <summary>
    /// Marker component for King of the Hill spawn points.
    /// Helps to find them in the scene and visualizing in Editor.
    /// </summary>
    public class HillSpawnPoint : MonoBehaviour {
        private static readonly HashSet<HillSpawnPoint> InstancesSet = new();
        public static IReadOnlyCollection<HillSpawnPoint> Instances => InstancesSet;

        private void OnEnable() {
            InstancesSet.Add(this);
        }

        private void OnDisable() {
            InstancesSet.Remove(this);
        }

        private void OnDrawGizmos() {
            Gizmos.color = new Color(0.3f, 0.4f, 1f, 0.7f); // Purple
            var position = transform.position;
            Gizmos.DrawSphere(position, 0.5f);
            Gizmos.DrawWireSphere(position, 1.0f);
        }

        // Context menu to snap the spawn point to the ground below it
        [ContextMenu("Snap to Ground")]
        private void SnapToGround() {
            // Check for ground 50 units up and down
            var startPos = transform.position + Vector3.up * 50f;
            if (Physics.Raycast(startPos, Vector3.down, out var hit, 100f)) {
                // Snap to hit point + slight offset to avoid clipping
                transform.position = hit.point;
                DevLog.Log($"[HillSpawnPoint] Snapped to {hit.collider.name} at Y={hit.point.y}");
            } else {
                DevLog.LogWarning($"[HillSpawnPoint] Could not find ground below (or above) {name}");
            }
        }
    }
}
