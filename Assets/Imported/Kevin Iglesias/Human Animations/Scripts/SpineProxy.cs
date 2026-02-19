// -- SPINE PROXY 1.0 | Kevin Iglesias --
// This script ensures correct animation display when mixing upper and lower body animations using Unity Avatar Masks.
// Attach this script to the 'B-spineProxy' transform, which is a sibling of the 'B-hips' bone.
// In the 'originalSpine' field, assign the 'B-spine' bone (child of 'B-hips' and parent of 'B-chest').
// By default it will automatically find the 'B-spine' and assign it to the 'originalSpine' field (OnValidate).
// When using a different character rig, manually assign the corresponding spine bone to the 'originalSpine' field and recreate
// 'Rig > B-root > B-spine' structure in your character hierarchy with empty GameObjects.

// More information: https://www.keviniglesias.com/spine-proxy.html
// Contact Support: support@keviniglesias.com

using UnityEngine;

namespace KevinIglesias
{
    [ExecuteAlways]
    [DefaultExecutionOrder(6000)]
    public class SpineProxy : MonoBehaviour
    {
        //Assign 'B-spine' (or equivalent) here:
        [SerializeField] private Transform originalSpine;

        private Quaternion rotationOffset = Quaternion.identity;

        //Match correct orientation on different character rigs
        void Awake()
        {
            RecalculateOffsetFromCurrentPose();
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            if(originalSpine == null)
            {
                Transform parent = transform.parent;
                if(parent != null)
                {
                    Transform hips = parent.Find("B-hips");
                    if(hips != null)
                    {
                        Transform spine = hips.Find("B-spine");
                        if(spine != null)
                        {
                            originalSpine = spine;
                        }
                    }
                }
            }
        }
#endif

        //Copy rotations from spine proxy bone to the original spine bone.
        void LateUpdate()
        {
            ApplyProxyToOriginalSpine();
        }

        public void ApplyProxyToOriginalSpine()
        {
            if(originalSpine == null)
            {
                return;
            }
            originalSpine.rotation = transform.rotation * rotationOffset;
        }

        public void RecalculateOffsetFromCurrentPose()
        {
            if(originalSpine == null)
            {
                return;
            }
            // Sample the current relation between proxy and real spine so runtime/editor start from the same baseline.
            rotationOffset = Quaternion.Inverse(transform.rotation) * originalSpine.rotation;
        }

        public void SetRotationOffset(Quaternion offset)
        {
            rotationOffset = offset;
        }

        public Quaternion GetRotationOffset()
        {
            return rotationOffset;
        }

        public bool TryGetDebugState(
            out Transform spine,
            out Quaternion proxyWorldRotation,
            out Quaternion expectedSpineWorldRotation,
            out Quaternion actualSpineWorldRotation,
            out Quaternion offsetRotation)
        {
            spine = originalSpine;
            proxyWorldRotation = transform.rotation;
            offsetRotation = rotationOffset;
            expectedSpineWorldRotation = transform.rotation * rotationOffset;
            actualSpineWorldRotation = originalSpine != null ? originalSpine.rotation : Quaternion.identity;
            return originalSpine != null;
        }
    }
}
