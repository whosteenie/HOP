using System;
using Game.Weapon.Core;
using UnityEngine;

namespace Game.Weapon.Kinemation {
    [Serializable]
    internal class KinemationWeaponBinding {
        public WeaponData weaponData;
        public GameObject kinemationWeaponPrefab;
        public bool useCustomViewmodelPose;
        public Vector3 viewmodelLocalPosition;
        public Vector3 viewmodelLocalEulerAngles;
        [Tooltip("Optional grapple clip override for this weapon.")]
        public AnimationClip grappleClip;
    }
}
