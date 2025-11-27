using UnityEngine;

namespace Network.AntiCheat {
    [CreateAssetMenu(fileName = "AntiCheatConfig", menuName = "Config/Anti-Cheat Config")]
    public class AntiCheatConfig : ScriptableObject {
        private const string ResourcesPath = "Configs/AntiCheatConfig";
        private static AntiCheatConfig instance;

        public static AntiCheatConfig Instance {
            get {
                if(instance != null) return instance;
                instance = Resources.Load<AntiCheatConfig>(ResourcesPath);
                if(instance == null) {
                    Debug.LogWarning($"[AntiCheat] Could not load AntiCheatConfig at Resources/{ResourcesPath}. " +
                                     "Create one via Assets > Create > Config > Anti-Cheat Config.");
                }

                return instance;
            }
        }

        [Header("RPC Rate Limits")]
        [Tooltip("Time window (seconds) used when counting RPCs for rate limiting.")]
        public float rpcWindowSeconds = 1f;

        [Tooltip("Maximum damage RPCs allowed per window.")] public int damageRpcLimit = 15;

        [Tooltip("Maximum world SFX RPCs allowed per window.")] public int sfxRpcLimit = 30;

        [Tooltip("Maximum weapon switch requests allowed per window.")] public int weaponSwitchLimit = 6;

        [Header("Movement Validation")]
        [Tooltip("Maximum allowed speed in meters per second.")]
        public float maxSpeedMetersPerSecond = 200f;

        [Tooltip("Maximum allowed teleport distance in meters.")]
        public float maxTeleportDistance = 15f;

        [Tooltip("Time window (seconds) for tracking movement violations.")]
        public float movementViolationWindowSeconds = 2f;

        [Tooltip("Number of speed violations required within the time window before clamping player position.")]
        public int speedViolationThreshold = 5;

        [Tooltip("Number of teleport violations required within the time window before teleporting player back.")]
        public int teleportViolationThreshold = 2;

        [Header("Fire Rate Validation")]
        [Tooltip("Extra seconds allowed when comparing fire rate (to account for jitter).")]
        public float fireRateGraceSeconds = 0.02f;
    }
}