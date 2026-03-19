using System;
using Game.Weapon.Core;
using UnityEngine;

namespace Game.Weapon.Kinemation {
    [Serializable]
    internal class KinWeaponBinding {
        [SerializeField] private WeaponData weaponData;
        [SerializeField] private GameObject kinemationWeaponPrefab;
        [SerializeField] private bool useCustomViewmodelPose;
        [SerializeField] private Vector3 viewmodelLocalPosition;
        [SerializeField] private Vector3 viewmodelLocalEulerAngles;

        internal WeaponData WeaponData => weaponData;
        internal GameObject KinWeaponPrefab => kinemationWeaponPrefab;
        internal bool UseCustomViewmodelPose => useCustomViewmodelPose;
        internal Vector3 ViewmodelLocalPosition => viewmodelLocalPosition;
        internal Vector3 ViewmodelLocalEulerAngles => viewmodelLocalEulerAngles;

        [Tooltip("Optional grapple clip override for this weapon.")]
        public AnimationClip grappleClip;
    }
}