using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    public class UpperBodyPitch : NetworkBehaviour {
        [Header("References")]
        [SerializeField] private PlayerController playerController;
        private PlayerRagdoll _playerRagdoll;
        
        [Header("Kevin Iglesias Proxy Bone")]
        [Tooltip("Assign B-spineProxy here (the one with SpineProxy on it, sibling of B-hips).")]
        [SerializeField] private Transform spineProxy;

        [Header("Axis & Limits")]
        [SerializeField] private Vector3 localPitchAxis = Vector3.right; // usually X
        [SerializeField] private bool invertAxis;
        [Range(-135f, 0f)]
        [SerializeField] private float minPitch = -35f;
        [Range(0f, 135f)]
        [SerializeField] private float maxPitch = 45f;
        [SerializeField] private float smooth = 12f;

        [Header("Weighting")] [Tooltip("How strongly to bend around the proxy (0–1).")] [Range(0f, 1f)]
        [SerializeField] private float spineWeight = 1f;

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

        private float _smoothedPitch;
        private Vector3 _axis;

        private void Awake() {
            playerController ??= GetComponent<PlayerController>();
            _playerRagdoll ??= playerController.PlayerRagdoll;
            
            _axis = localPitchAxis.normalized;
            if(invertAxis) _axis = -_axis;
        }

        /// <summary>
        /// Owner calls this every frame with the camera pitch (your CurrentPitch).
        /// </summary>
        public void SetLocalPitchFromCamera(float cameraPitchDeg) {
            if(!IsOwner) return;
            
            // Don't update pitch if player is in ragdoll
            if(_playerRagdoll != null && _playerRagdoll.IsRagdoll) return;
            
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
            if(_playerRagdoll != null && _playerRagdoll.IsRagdoll) return;

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