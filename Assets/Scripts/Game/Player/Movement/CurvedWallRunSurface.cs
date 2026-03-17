using UnityEngine;

namespace Game.Player.Movement {
    /// <summary>
    /// Attach to colliders that form a cylindrical (or curved) wall-run surface.
    /// Enables continuation/grace logic and provides cylinder geometry so the next segment normal
    /// and probe distance can be computed for reliable detection at high speed.
    /// </summary>
    public class CurvedWallRunSurface : MonoBehaviour {
        [Tooltip("Number of segments around the cylinder (e.g. 64 for ProBuilder default).")]
        [SerializeField] private int cylinderSides = 64;

        [Tooltip("Radius in local space. World radius uses transform scale (uniform X/Z recommended).")]
        [SerializeField] private float radius = 2f;

        [Tooltip("Local axis of the cylinder (e.g. Y for vertical).")]
        [SerializeField] private Vector3 axis = Vector3.up;

        private int CylinderSides => Mathf.Max(3, cylinderSides);
        private float Radius => Mathf.Max(0.01f, radius);

        /// <summary>World-space cylinder axis (normalized).</summary>
        private Vector3 WorldAxis {
            get {
                var a = transform.TransformDirection(axis);
                return a.sqrMagnitude > 0.0001f ? a.normalized : Vector3.up;
            }
        }

        /// <summary>World radius from transform scale (uses max of X/Z for horizontal cross-section).</summary>
        public float WorldRadius {
            get {
                var s = transform.lossyScale;
                var horizontalScale = Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));
                return Radius * horizontalScale;
            }
        }

        /// <summary>World-space point on the cylinder axis (e.g. transform center).</summary>
        private Vector3 WorldAxisPoint => transform.position;

        /// <summary>Distance from worldPoint to the cylinder surface (0 = on surface).</summary>
        public float GetDistanceToSurface(Vector3 worldPoint) {
            var worldAxis = WorldAxis;
            var toPoint = worldPoint - WorldAxisPoint;
            var alongAxis = Vector3.Dot(toPoint, worldAxis);
            var nearestOnAxis = WorldAxisPoint + worldAxis * alongAxis;
            var distToAxis = (worldPoint - nearestOnAxis).magnitude;
            return Mathf.Abs(distToAxis - WorldRadius);
        }

        /// <summary>Outward wall normal at the nearest point on the cylinder to worldPoint. Returns false if degenerate.</summary>
        public bool TryGetNormalAt(Vector3 worldPoint, out Vector3 normal) {
            normal = Vector3.zero;
            var worldAxis = WorldAxis;
            var toPoint = worldPoint - WorldAxisPoint;
            var alongAxis = Vector3.Dot(toPoint, worldAxis);
            var nearestOnAxis = WorldAxisPoint + worldAxis * alongAxis;
            var outward = worldPoint - nearestOnAxis;
            if(outward.sqrMagnitude < 0.0001f) return false;
            normal = outward.normalized;
            return true;
        }
    }
}
