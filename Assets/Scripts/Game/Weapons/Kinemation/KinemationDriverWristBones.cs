using UnityEngine;

namespace Game.Weapons.Kinemation {
    /// <summary>Caches wrist/debug bones and applies fixed offsets for the KIN viewmodel left arm.</summary>
    internal sealed class KinemationDriverWristBones {
        private static readonly Vector3 FixedUpperarmLeftPositionOffset = new(0f, 0.027f, 0f);
        private static readonly Vector3 FixedTwistLeftEulerOffset = new(0f, -7.5f, 0f);

        private readonly IKinemationDriverResolverContext _context;
        private bool _hasCached;
        private Transform _clavicleLeft;
        private Transform _wristDebugUpperarmLeft;
        private Transform _wristDebugLowerarmLeft;
        private Transform _wristDebugTwistLeft;
        private Transform _wristDebugHandLeft;
        private Transform _ikHandLeft;
        private Transform _grappleOrigin;

        public KinemationDriverWristBones(IKinemationDriverResolverContext context) {
            _context = context;
        }

        public Transform ClavicleLeft => _clavicleLeft;
        public Transform GrappleOrigin => _grappleOrigin;
        public Transform IkHandLeft => _ikHandLeft;
        public Transform WristDebugHandLeft => _wristDebugHandLeft;

        public void CacheIfNeeded() {
            if(_hasCached || _context.PlayerInstance == null) return;
            var root = _context.PlayerInstance.transform;
            TryFindChildByName(root, "clavicle_l", out _clavicleLeft);
            TryFindChildByName(root, "upperarm_l", out _wristDebugUpperarmLeft);
            TryFindChildByName(root, "lowerarm_l", out _wristDebugLowerarmLeft);
            TryFindChildByName(root, "lowerarm_twist_01_l", out _wristDebugTwistLeft);
            TryFindChildByName(root, "hand_l", out _wristDebugHandLeft);
            TryFindChildByName(root, "ik_hand_l", out _ikHandLeft);
            TryFindChildByName(root, "GrappleOrigin", out _grappleOrigin);
            _hasCached = true;
        }

        public Transform GetGrappleOriginFpTransform() {
            CacheIfNeeded();
            if(_grappleOrigin != null) return _grappleOrigin;
            return _ikHandLeft != null ? _ikHandLeft : _wristDebugHandLeft;
        }

        public void ApplyFixedWristOffsets() {
            if(_context.PlayerInstance == null) return;
            CacheIfNeeded();
            if(_wristDebugUpperarmLeft == null && _wristDebugTwistLeft == null) return;

            var preserveHand = _wristDebugHandLeft != null;
            Vector3 handPos = default;
            Quaternion handRot = default;
            if(preserveHand) {
                handPos = _wristDebugHandLeft.position;
                handRot = _wristDebugHandLeft.rotation;
            }

            if(_wristDebugUpperarmLeft != null && FixedUpperarmLeftPositionOffset.sqrMagnitude > 1e-8f)
                _wristDebugUpperarmLeft.localPosition += FixedUpperarmLeftPositionOffset;
            if(_wristDebugTwistLeft != null && FixedTwistLeftEulerOffset.sqrMagnitude > 1e-6f) {
                _wristDebugTwistLeft.localRotation *= Quaternion.Euler(FixedTwistLeftEulerOffset);
            }

            if(preserveHand) _wristDebugHandLeft.SetPositionAndRotation(handPos, handRot);
        }

        public bool TryGetClavicleLeft(out Transform clavicle) {
            CacheIfNeeded();
            clavicle = _clavicleLeft;
            return _clavicleLeft != null;
        }

        public bool EnsureClavicleLeft(GameObject playerInstance) {
            if(_clavicleLeft != null) return true;
            return playerInstance != null && TryFindChildByName(playerInstance.transform, "clavicle_l", out _clavicleLeft);
        }

        private static bool TryFindChildByName(Transform root, string targetName, out Transform found) {
            if(root.name == targetName) {
                found = root;
                return true;
            }
            for(var i = 0; i < root.childCount; i++) {
                if(TryFindChildByName(root.GetChild(i), targetName, out found)) return true;
            }
            found = null;
            return false;
        }
    }
}
