using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    public class UpperBodyPitch : NetworkBehaviour {
        [Header("Bones")] public Transform hips;
        public Transform spine;
        public Transform chest;

        [Header("Axis & Limits")] public Vector3 localPitchAxis = Vector3.right; // set to X for most humanoids
        public bool invertAxis = false; // flip if bending goes the wrong way
        [Range(-135f, 0f)] public float minPitch = -35f;
        [Range(0f, 135f)] public float maxPitch = 45f;
        public float smooth = 12f;

        [Tooltip("Distribution across hips / spine / chest (sum ~= 1).")]
        public Vector3 distribution = new Vector3(0.0f, 0.45f, 0.55f); // try spine+chest first

        // Networked pitch from owner
        public NetworkVariable<float> netPitchDeg = new(
            0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        // cached bind rotations
        Quaternion _bindHips, _bindSpine, _bindChest;
        float _smoothedPitch;
        Vector3 _axis;
        private bool _initialized;

        void Awake() {
            _axis = localPitchAxis.normalized;
            if(invertAxis) _axis = -_axis;
        }

        // Owner calls this every frame with CurrentPitch (or -CurrentPitch if needed)
        public void SetLocalPitchFromCamera(float cameraPitchDeg) {
            if(!IsOwner) return;
            netPitchDeg.Value = Mathf.Clamp(cameraPitchDeg, minPitch, maxPitch);
        }

        void LateUpdate() {
            var target = Mathf.Clamp(netPitchDeg.Value, minPitch, maxPitch);
            _smoothedPitch = Mathf.Lerp(_smoothedPitch, target, 1f - Mathf.Exp(-smooth * Time.deltaTime));

            // Apply pitch as LOCAL rotation AFTER animator (additive to current pose)
            if (spine && distribution.y != 0f)
                spine.localRotation *= Quaternion.AngleAxis(_smoothedPitch * distribution.y, _axis);

            if (chest && distribution.z != 0f)
                chest.localRotation *= Quaternion.AngleAxis(_smoothedPitch * distribution.z, _axis);
        }
    }
}