using Diagnostics;
using UnityEngine;

namespace Game.Weapon.Kinemation {
    [DisallowMultipleComponent]
    public sealed class KinWeaponPartReferences : MonoBehaviour {
        [Header("Muzzle")]
        [Tooltip("Preferred FP muzzle transform used for local muzzle sampling/spawn.")]
        [SerializeField] private Transform fpMuzzleTransform;

        [Header("Drake-12")]
        [Tooltip("Top shell transform used for Drake shell suppression/show handling.")]
        [SerializeField] private Transform drakeTopShell;
        [Tooltip("Bottom shell transform used for Drake shell suppression/show handling.")]
        [SerializeField] private Transform drakeBottomShell;

        [Header("Kar98K")]
        [Tooltip("Loop bullet transform used for Kar reload loop hide/show handling.")]
        [SerializeField] private Transform karLoopBullet;

        public Transform FpMuzzleTransform => fpMuzzleTransform;
        public Transform DrakeTopShell => drakeTopShell;
        public Transform DrakeBottomShell => drakeBottomShell;
        public Transform KarLoopBullet => karLoopBullet;

#if UNITY_EDITOR
        private void OnValidate() {
            ValidateReference(fpMuzzleTransform, nameof(fpMuzzleTransform));
            ValidateReference(drakeTopShell, nameof(drakeTopShell));
            ValidateReference(drakeBottomShell, nameof(drakeBottomShell));
            ValidateReference(karLoopBullet, nameof(karLoopBullet));
        }

        private void ValidateReference(Transform reference, string fieldName) {
            if(reference == null) return;
            if(reference == transform || reference.IsChildOf(transform)) return;

            DevLog.LogWarning(
                $"[KINWeaponPartReferences] '{fieldName}' on '{name}' should point to this prefab hierarchy.",
                this);
        }

#endif
    }
}
