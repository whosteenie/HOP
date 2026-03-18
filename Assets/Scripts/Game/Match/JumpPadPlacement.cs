using Diagnostics;
using UnityEngine;

namespace Game.Match {
    /// <summary>
    /// Editor placement helper for jump pads.
    /// Snap places the pad against ground and can bury part of the mesh.
    /// </summary>
    public class JumpPadPlacement : MonoBehaviour {
        [Header("Ground Snap")]
        [SerializeField] private LayerMask groundMask = Physics.DefaultRaycastLayers;
        [SerializeField] private float rayStartHeight = 50f;
        [SerializeField] private float rayDistance = 200f;
        [SerializeField] [Range(0f, 1f)] private float buriedFraction = 0.5f;
        [SerializeField] private float surfaceOffset;

        [ContextMenu("Snap to Ground (Half Buried)")]
        private void SnapToGroundHalfBuried() {
            var tr = transform;
            var axis = tr.up.normalized;
            var rayOrigin = tr.position + Vector3.up * Mathf.Max(0.1f, rayStartHeight);

            if(!Physics.Raycast(rayOrigin, Vector3.down, out var hit, Mathf.Max(1f, rayDistance), groundMask,
                   QueryTriggerInteraction.Ignore)) {
                DevLog.LogWarning($"[JumpPadPlacement] Could not find ground below {name}.");
                return;
            }

            if(!TryGetProjectedBounds(axis, out var min, out var max)) {
                DevLog.LogWarning($"[JumpPadPlacement] No Renderer/Collider bounds found on {name}.");
                return;
            }

            var thickness = Mathf.Max(0.001f, max - min);
            var halfThickness = thickness * 0.5f;
            var meshCenterFromPivot = (min + max) * 0.5f;
            var targetCenterAboveSurface = halfThickness * (1f - 2f * Mathf.Clamp01(buriedFraction));
            var pivotAboveSurface = targetCenterAboveSurface - meshCenterFromPivot + surfaceOffset;
            var targetPosition = hit.point + axis * pivotAboveSurface;

#if UNITY_EDITOR
            UnityEditor.Undo.RecordObject(transform, "Snap JumpPad To Ground");
#endif
            transform.position = targetPosition;

#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(transform);
#endif

            DevLog.Log(
                $"[JumpPadPlacement] Snapped '{name}' to {hit.collider.name}. Buried={buriedFraction:0.00}, thickness={thickness:0.###}");
        }

        private bool TryGetProjectedBounds(Vector3 axis, out float min, out float max) {
            min = float.PositiveInfinity;
            max = float.NegativeInfinity;
            var hasAny = false;

            var renderers = GetComponentsInChildren<Renderer>(true);
            foreach(var r in renderers) {
                if(!r.enabled) continue;
                ExpandProjectedBounds(r.bounds, axis, ref min, ref max);
                hasAny = true;
            }

            if(hasAny) {
                return true;
            }

            var colliders = GetComponentsInChildren<Collider>(true);
            foreach(var col in colliders) {
                ExpandProjectedBounds(col.bounds, axis, ref min, ref max);
                hasAny = true;
            }

            return hasAny;
        }

        private void ExpandProjectedBounds(Bounds bounds, Vector3 axis, ref float min, ref float max) {
            var localCenter = bounds.center - transform.position;
            var centerProjection = Vector3.Dot(localCenter, axis);
            var extents = bounds.extents;
            var projectedHalfExtent =
                Mathf.Abs(axis.x) * extents.x +
                Mathf.Abs(axis.y) * extents.y +
                Mathf.Abs(axis.z) * extents.z;

            var boundMin = centerProjection - projectedHalfExtent;
            var boundMax = centerProjection + projectedHalfExtent;

            if(boundMin < min) min = boundMin;
            if(boundMax > max) max = boundMax;
        }
    }
}
