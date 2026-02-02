using UnityEngine;

namespace Game.Match {
    /// <summary>
    /// Marker component for King of the Hill spawn points.
    /// Helps finding them in the scene and visualizing in Editor.
    /// </summary>
    public class HillSpawnPoint : MonoBehaviour {
        private void OnDrawGizmos() {
            Gizmos.color = new Color(0.5f, 0f, 1f, 0.7f); // Purple
            Gizmos.DrawSphere(transform.position, 0.5f);
            Gizmos.DrawWireSphere(transform.position, 1.0f);
        }

        [ContextMenu("Snap to Ground")]
        private void SnapToGround() {
            // Check for ground 50 units up and down
            var startPos = transform.position + Vector3.up * 50f;
            if (Physics.Raycast(startPos, Vector3.down, out RaycastHit hit, 100f)) {
                // Snap to hit point + slight offset to avoid clipping
                transform.position = hit.point;
                Debug.Log($"[HillSpawnPoint] Snapped to {hit.collider.name} at Y={hit.point.y}");
            } else {
                Debug.LogWarning($"[HillSpawnPoint] Could not find ground below (or above) {name}");
            }
        }
    }
}
