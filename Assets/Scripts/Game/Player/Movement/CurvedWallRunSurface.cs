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

        /// <summary>
        /// Predicts the next segment's outward normal given the current wall normal and run direction.
        /// Returns true if the prediction is valid.
        /// </summary>
        public bool TryGetPredictedNextNormal(Vector3 currentWallNormal, Vector3 runDirection, out Vector3 predictedNextNormal) {
            predictedNextNormal = currentWallNormal;
            var n = currentWallNormal;
            n.y = 0f;
            if(n.sqrMagnitude < 0.0001f) return false;

            n.Normalize();
            var worldAxis = WorldAxis;
            var tangent = Vector3.Cross(worldAxis, n);
            if(tangent.sqrMagnitude < 0.0001f) return false;

            tangent.Normalize();
            var run = runDirection;
            run.y = 0f;
            if(run.sqrMagnitude < 0.0001f) return false;

            run.Normalize();
            var sign = Mathf.Sign(Vector3.Dot(run, tangent));
            var angleDeg = 360f / CylinderSides;
            var rotation = Quaternion.AngleAxis(sign * angleDeg, worldAxis);
            var current = currentWallNormal.sqrMagnitude > 0.0001f ? currentWallNormal.normalized : n;
            predictedNextNormal = rotation * current;
            if(!(predictedNextNormal.sqrMagnitude > 0.0001f)) return false;
            predictedNextNormal.Normalize();
            return true;

        }

        /// <summary>Chord length for one segment at world radius; use as minimum probe distance for the next face.</summary>
        public float GetSegmentAwareProbeDistance() {
            var r = WorldRadius;
            var sides = CylinderSides;
            var angleRad = Mathf.PI * 2f / sides;
            var chord = 2f * r * Mathf.Sin(angleRad * 0.5f);
            return Mathf.Max(chord * 1.2f, 0.5f);
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

        /// <summary>True if worldPoint is within maxDistance of the cylinder surface. Use for "am I still on this wall?".</summary>
        public bool IsPointOnSurface(Vector3 worldPoint, float maxDistance) {
            return GetDistanceToSurface(worldPoint) <= maxDistance;
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
