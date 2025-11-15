using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    public class UpperBodyPitch : NetworkBehaviour {
        [Header("Kevin Iglesias Proxy Bone")]
        [Tooltip("Assign B-spineProxy here (the one with SpineProxy on it, sibling of B-hips).")]
        public Transform spineProxy;

        [Header("Axis & Limits")]
        public Vector3 localPitchAxis = Vector3.right; // usually X
        public bool invertAxis = false;
        [Range(-135f, 0f)] public float minPitch = -35f;
        [Range(0f, 135f)] public float maxPitch = 45f;
        public float smooth = 12f;

        [Header("Weighting")]
        [Tooltip("How strongly to bend around the proxy (0–1).")]
        [Range(0f, 1f)] public float spineWeight = 1f;

        // Networked pitch from owner
        public NetworkVariable<float> netPitchDeg = new(
            0f,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner
        );

        float _smoothedPitch;
        Vector3 _axis;

        void Awake() {
            _axis = localPitchAxis.normalized;
            if (invertAxis) _axis = -_axis;
        }

        /// <summary>
        /// Owner calls this every frame with the camera pitch (your CurrentPitch).
        /// </summary>
        public void SetLocalPitchFromCamera(float cameraPitchDeg) {
            if (!IsOwner) return;
            netPitchDeg.Value = Mathf.Clamp(cameraPitchDeg, minPitch, maxPitch);
        }

        void LateUpdate() {
            if (!spineProxy) return;

            float target = Mathf.Clamp(netPitchDeg.Value, minPitch, maxPitch);

            _smoothedPitch = Mathf.Lerp(
                _smoothedPitch,
                target,
                1f - Mathf.Exp(-smooth * Time.deltaTime)
            );

            // Apply pitch ADDITIVELY in LOCAL space on the proxy.
            if (spineWeight > 0f) {
                spineProxy.localRotation *= Quaternion.AngleAxis(
                    _smoothedPitch * spineWeight,
                    _axis
                );
            }
        }
    }
}
