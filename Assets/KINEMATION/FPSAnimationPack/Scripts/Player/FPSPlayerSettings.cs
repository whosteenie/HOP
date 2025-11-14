// Designed by KINEMATION, 2025.

using System.Collections.Generic;
using UnityEngine;

namespace KINEMATION.FPSAnimationPack.Scripts.Player
{
    [CreateAssetMenu(fileName = "NewPlayerSettings", menuName = "KINEMATION/FPS Animation Pack/FPS Player Settings")]
    public class FPSPlayerSettings : ScriptableObject
    {
        [Header("Controls")]
        [Min(0f)] public float sensitivity = 1f;
        
        public List<GameObject> weaponPrefabs;
        public float grenadeDelay = 0f;
        public float gaitSmoothing = 0f;
        
        [Range(0f, 1f)] public float ikWeight = 1f;
        public float aimSpeed = 0f;

        public IKMotion aimingMotion;
        public IKMotion fireModeMotion;

        public List<AudioClip> generalSounds;
    }
}