// Designed by KINEMATION, 2025.

using System.Collections.Generic;
using KINEMATION.FPSAnimationPack.Scripts.Camera;
using KINEMATION.ProceduralRecoilAnimationSystem.Runtime;
using UnityEngine;

namespace KINEMATION.FPSAnimationPack.Scripts.Weapon
{
    [CreateAssetMenu(fileName = "NewWeaponSettings", menuName = "KINEMATION/FPS Animation Pack/Weapon Settings")]
    public class FPSWeaponSettings : ScriptableObject
    {
        [Header("General")]
        public RuntimeAnimatorController characterController;
        public RecoilAnimData recoilAnimData;
        public FPSCameraShake cameraShake;
        
        [Header("IK")]
        public Quaternion rotationOffset = Quaternion.Euler(90f, 0f, 0f);
        public Vector3 ikOffset;
        public Vector3 leftClavicleOffset;
        public Vector3 rightClavicleOffset;
        public Vector3 aimPointOffset;
        public Quaternion rightHandSprintOffset = Quaternion.identity;
        [Range(0f, 1f)] public float adsBlend = 0f;
        
        [Header("Gameplay")]
        [Min(0f)] public float fireRate = 600f;
        [Min(1)] public int ammo = 1;
        [Min(0f)] public float aimFov = 70f;
        [Min(0f)] public float ammoResetTimeScale = 1f;
        public bool fullAuto;
        public bool useFireClip;
        public bool hasEquipOverride;
        public bool hasFireOut;
        public bool useSprintTriggerDiscipline = true;
        
        [Header("SFX")]
        public List<AudioClip> fireSounds;
        public Vector2 firePitchRange = Vector2.one;
        public Vector2 fireVolumeRange = Vector2.one;
        public List<AudioClip> weaponEventSounds;
    }
}