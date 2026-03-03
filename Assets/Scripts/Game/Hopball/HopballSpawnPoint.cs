using UnityEngine;
using System.Collections.Generic;

namespace Game.Hopball {
    /// <summary>
    /// Marks a location where hopballs can spawn in Hopball gamemode.
    /// Place these in the scene and assign them to HopballSpawnManager.
    /// </summary>
    public class HopballSpawnPoint : MonoBehaviour {
        private static readonly HashSet<HopballSpawnPoint> InstancesSet = new();
        public static IReadOnlyCollection<HopballSpawnPoint> Instances => InstancesSet;

        [Header("Gizmo Settings")]
        [SerializeField] private Color gizmoColor = new(1f, 0f, 1f, 0.5f); // Magenta

        [SerializeField] private float gizmoRadius = 0.5f;

        private void OnEnable() {
            InstancesSet.Add(this);
        }

        private void OnDisable() {
            InstancesSet.Remove(this);
        }

        private void OnDrawGizmos() {
            Gizmos.color = gizmoColor;
            var position = transform.position;
            Gizmos.DrawSphere(position, gizmoRadius);
            Gizmos.DrawWireSphere(position, gizmoRadius);
        }

        private void OnDrawGizmosSelected() {
            Gizmos.color = gizmoColor;
            Gizmos.DrawSphere(transform.position, gizmoRadius * 1.5f);
        }
    }
}
