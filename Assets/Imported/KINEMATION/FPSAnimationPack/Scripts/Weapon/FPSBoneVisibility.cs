// Designed by KINEMATION, 2025.

using System.Collections.Generic;
using UnityEngine;

namespace KINEMATION.FPSAnimationPack.Scripts.Weapon
{
    [AddComponentMenu("KINEMATION/FPS Animation Pack/Weapon/FPS Bone Visibility")]
    public class FPSBoneVisibility : MonoBehaviour
    {
        [SerializeField] private List<Transform> defaultBonesToHide;
        [SerializeField] private List<Transform> bonesToHideByEvent;

        private bool _isVisible = true;

        private void Start()
        {
            foreach (var bone in defaultBonesToHide) bone.localScale = Vector3.zero;
        }

        public void SetBoneVisibility(int value)
        {
            _isVisible = value == 1;
        }

        private void LateUpdate()
        {
            foreach (var bone in defaultBonesToHide) bone.localScale = Vector3.zero;
            foreach (var bone in bonesToHideByEvent)
                bone.localScale = _isVisible ? Vector3.one : Vector3.zero;
        }
    }
}