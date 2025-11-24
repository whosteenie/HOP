using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    public class UpperBodyPitch : NetworkBehaviour {
        [Header("Kevin Iglesias Proxy Bone")]
        [Tooltip("Assign B-spineProxy here (the one with SpineProxy on it, sibling of B-hips).")]
        public Transform spineProxy;

        [Header("Axis & Limits")] public Vector3 localPitchAxis = Vector3.right; // usually X
        public bool invertAxis;
        [Range(-135f, 0f)] public float minPitch = -35f;
        [Range(0f, 135f)] public float maxPitch = 45f;
        public float smooth = 12f;

        [Header("Weighting")] [Tooltip("How strongly to bend around the proxy (0–1).")] [Range(0f, 1f)]
        public float spineWeight = 1f;

        [Header("References")]
        [SerializeField] private PlayerRagdoll playerRagdoll;

        // Networked pitch from owner
        // Note: WritePermission.Owner means only the owner can write this value
        // The editor may show warnings when inspecting non-owner instances - this is expected
        public NetworkVariable<float> netPitchDeg = new(
            0f,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner
        );
        
        // Throttling for pitch updates (at 90Hz: 3 ticks = ~33ms)
        private float _lastPitchUpdateTime;
        private const float PitchUpdateInterval = 0.033f; // ~3 ticks at 90Hz

        float _smoothedPitch;
        Vector3 _axis;

        void Awake() {
            _axis = localPitchAxis.normalized;
            if(invertAxis) _axis = -_axis;

            // Get PlayerRagdoll reference if not assigned
            if(playerRagdoll == null) {
                playerRagdoll = GetComponent<PlayerRagdoll>();
            }
        }

        /// <summary>
        /// Owner calls this every frame with the camera pitch (your CurrentPitch).
        /// </summary>
        public void SetLocalPitchFromCamera(float cameraPitchDeg) {
            if(!IsOwner) return;
            
            // Don't update pitch if player is in ragdoll
            if(playerRagdoll != null && playerRagdoll.IsRagdoll) return;
            
            var clampedPitch = Mathf.Clamp(cameraPitchDeg, minPitch, maxPitch);
            
            // Throttle network updates - only send if enough time has passed or value changed significantly
            if(Time.time - _lastPitchUpdateTime >= PitchUpdateInterval || Mathf.Abs(netPitchDeg.Value - clampedPitch) > 1f) {
                netPitchDeg.Value = clampedPitch;
                _lastPitchUpdateTime = Time.time;
            }
        }

        void LateUpdate() {
            if(!spineProxy) return;

            // Don't apply pitch rotation if player is in ragdoll
            if(playerRagdoll != null && playerRagdoll.IsRagdoll) return;

            float target = Mathf.Clamp(netPitchDeg.Value, minPitch, maxPitch);

            _smoothedPitch = Mathf.Lerp(
                _smoothedPitch,
                target,
                1f - Mathf.Exp(-smooth * Time.deltaTime)
            );

            // Apply pitch ADDITIVELY in LOCAL space on the proxy.
            if(spineWeight > 0f) {
                spineProxy.localRotation *= Quaternion.AngleAxis(
                    _smoothedPitch * spineWeight,
                    _axis
                );
            }
        }
    }
}