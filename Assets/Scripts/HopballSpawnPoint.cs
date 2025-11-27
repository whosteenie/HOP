using UnityEngine;

/// <summary>
/// Marks a location where hopballs can spawn in Hopball gamemode.
/// Place these in the scene and assign them to HopballSpawnManager.
/// </summary>
public class HopballSpawnPoint : MonoBehaviour {
    [Header("Gizmo Settings")]
    [SerializeField] private Color gizmoColor = new(1f, 0f, 1f, 0.5f); // Magenta

    [SerializeField] private float gizmoRadius = 0.5f;

    private void OnDrawGizmos() {
        Gizmos.color = gizmoColor;
        Gizmos.DrawSphere(transform.position, gizmoRadius);
        Gizmos.DrawWireSphere(transform.position, gizmoRadius);
    }

    private void OnDrawGizmosSelected() {
        Gizmos.color = gizmoColor;
        Gizmos.DrawSphere(transform.position, gizmoRadius * 1.5f);
    }
}