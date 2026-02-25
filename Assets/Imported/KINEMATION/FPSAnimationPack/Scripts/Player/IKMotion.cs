// Designed by KINEMATION, 2025.

using KINEMATION.KAnimationCore.Runtime.Core;
using UnityEngine;

namespace KINEMATION.FPSAnimationPack.Scripts.Player
{
    [CreateAssetMenu(fileName = "NewIkMotionLayer", menuName = "KINEMATION/FPS Animation Pack/IK Motion")]
    public class IKMotion : ScriptableObject
    {
        public VectorCurve rotationCurves = new VectorCurve(new Keyframe[]
        {
            new Keyframe(0f, 0f),
            new Keyframe(1f, 0f)
        });
        
        public VectorCurve translationCurves = new VectorCurve(new Keyframe[]
        {
            new Keyframe(0f, 0f),
            new Keyframe(1f, 0f)
        });
        
        public Vector3 rotationScale = Vector3.one;
        public Vector3 translationScale = Vector3.one;

        [Min(0f)] public float blendTime = 0f;
        [Min(0f)] public float playRate = 1f;

        public float GetLength()
        {
            return Mathf.Max(rotationCurves.GetCurveLength(), translationCurves.GetCurveLength());
        }
    }
}
