using System;
using Game.Weapon.Core;
using UnityEngine;

namespace Game.Weapon.Kinemation {
    [Serializable]
    internal class KinWeaponBinding {
        internal WeaponData WeaponData;
        internal GameObject KinWeaponPrefab;
        internal bool UseCustomViewmodelPose;
        internal Vector3 ViewmodelLocalPosition;
        internal Vector3 ViewmodelLocalEulerAngles;
        [Tooltip("Optional grapple clip override for this weapon.")]
        public AnimationClip grappleClip;
    }
}