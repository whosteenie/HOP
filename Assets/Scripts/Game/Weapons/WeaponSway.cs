using Unity.Cinemachine;
using UnityEngine;

namespace Game.Weapons {
    [DisallowMultipleComponent]
    public class WeaponSway : MonoBehaviour {
        [Header("References")]
        [SerializeField] private Transform cam; // FP camera transform

        [Header("Position Sway (local offset)")]
        [SerializeField] private float posPerDegYaw = 0.01f; // X per degree of yaw

        [SerializeField] private float posPerDegPitch = 0.002f; // Y per degree of pitch
        [SerializeField] private float posPerDegPush = 0.003f; // Z per degree (tiny push/pull)
        [SerializeField] private float posMaxX = 0.06f;
        [SerializeField] private float posMaxY = 0.03f;
        [SerializeField] private float posMaxZ = 0.04f;

        [Header("Rotation Sway (local euler)")]
        [SerializeField] private float rotPerDegYaw = 0.5f;

        [SerializeField] private float rotPerDegPitch = 0.1f;
        [SerializeField] private float rotPerDegRoll = 0.45f; // bank on yaw
        [SerializeField] private float rotMaxYaw = 4f;
        [SerializeField] private float rotMaxPitch = 2.5f;
        [SerializeField] private float rotMaxRoll = 5f;

        [Header("Dynamics")]
        [SerializeField] private float followPosSpeed = 9f; // how fast it chases offset

        [SerializeField] private float followRotSpeed = 9f;
        [SerializeField] private float recenterSpeed = 6f; // how fast it returns to rest

        [Header("Filtering")]
        [Range(0f, 1f)] public float deltaSmoothing = 0.6f; // 0 = raw, 1 = very smooth

        [Range(0f, 1f)] public float adsMultiplier = 1f; // scale down when ADS

        // Internal state
        Vector3 baseLocalPos;
        Quaternion baseLocalRot;
        Vector2 lastAngles; // (pitch, yaw)
        Vector2 smoothedDelta; // smoothed per-frame delta
        Vector3 curPos, velPos;
        Vector3 curRotEuler, velRot;

        void Awake() {
            if(!cam) {
                var c = GetComponentInParent<CinemachineCamera>();
                if(c) cam = c.transform;
            }

            baseLocalPos = transform.localPosition;
            baseLocalRot = transform.localRotation;

            var e = cam ? cam.eulerAngles : Vector3.zero;
            lastAngles = new Vector2(e.x, e.y);
        }

        void OnEnable() {
            transform.localPosition = baseLocalPos;
            transform.localRotation = baseLocalRot;

            curPos = velPos = Vector3.zero;
            curRotEuler = velRot = Vector3.zero;
            smoothedDelta = Vector2.zero;

            var e = cam ? cam.eulerAngles : Vector3.zero;
            lastAngles = new Vector2(e.x, e.y);
        }

        void LateUpdate() {
            if(!cam) {
                cam = GetComponentInParent<CinemachineCamera>().transform;
                return;
            }

            // 1) Compute per-frame angle delta (in degrees)
            var e = cam.eulerAngles;
            float dPitch = Mathf.DeltaAngle(lastAngles.x, e.x); // + = look up
            float dYaw = Mathf.DeltaAngle(lastAngles.y, e.y); // + = look right
            lastAngles = new Vector2(e.x, e.y);

            // 2) Smooth the delta so it's not jittery
            var rawDelta = new Vector2(dPitch, dYaw);
            var t = 1f - deltaSmoothing;
            smoothedDelta = Vector2.Lerp(smoothedDelta, rawDelta, t);

            // 3) Build desired position offset (weapon lags opposite to look)
            float posX = Mathf.Clamp(-smoothedDelta.y * posPerDegYaw, -posMaxX, posMaxX); // yaw → X
            float posY = Mathf.Clamp(-smoothedDelta.x * posPerDegPitch, -posMaxY, posMaxY); // pitch → Y

            float combined = Mathf.Abs(smoothedDelta.x) + Mathf.Abs(smoothedDelta.y);
            float posZ = Mathf.Clamp(combined * posPerDegPush, -posMaxZ, posMaxZ);

            Vector3 targetPos = new Vector3(posX, posY, -posZ) * adsMultiplier;

            // 4) Build desired rotation offset (tiny tilt + bank)
            float rYaw = Mathf.Clamp(smoothedDelta.y * rotPerDegYaw, -rotMaxYaw, rotMaxYaw);
            float rPitch = Mathf.Clamp(-smoothedDelta.x * rotPerDegPitch, -rotMaxPitch, rotMaxPitch);
            float rRoll = Mathf.Clamp(smoothedDelta.y * rotPerDegRoll, -rotMaxRoll, rotMaxRoll);

            Vector3 targetRot = new Vector3(rPitch, rYaw, rRoll) * adsMultiplier;

            // 5) Smoothly move toward offsets
            float dt = Time.deltaTime;
            float pSpeed = (targetPos.sqrMagnitude > 0.0001f) ? followPosSpeed : recenterSpeed;
            float rSpeed = (targetRot.sqrMagnitude > 0.0001f) ? followRotSpeed : recenterSpeed;

            float pTime = 1f / Mathf.Max(pSpeed, 0.01f);
            float rTime = 1f / Mathf.Max(rSpeed, 0.01f);

            curPos = Vector3.SmoothDamp(curPos, targetPos, ref velPos, pTime, Mathf.Infinity, dt);
            curRotEuler = SmoothDampEuler(curRotEuler, targetRot, ref velRot, rTime, dt);

            // 6) Apply
            transform.localPosition = baseLocalPos + curPos;
            transform.localRotation = baseLocalRot * Quaternion.Euler(curRotEuler);
        }

        static Vector3 SmoothDampEuler(Vector3 current, Vector3 target, ref Vector3 vel, float smoothTime, float dt) {
            return new Vector3(
                Mathf.SmoothDamp(current.x, target.x, ref vel.x, smoothTime, Mathf.Infinity, dt),
                Mathf.SmoothDamp(current.y, target.y, ref vel.y, smoothTime, Mathf.Infinity, dt),
                Mathf.SmoothDamp(current.z, target.z, ref vel.z, smoothTime, Mathf.Infinity, dt)
            );
        }

        public void SetAdsMultiplier(float m) => adsMultiplier = Mathf.Clamp01(m);

        public void RecalibrateRestPose() {
            baseLocalPos = transform.localPosition;
            baseLocalRot = transform.localRotation;
            curPos = velPos = Vector3.zero;
            curRotEuler = velRot = Vector3.zero;
            smoothedDelta = Vector2.zero;
        }
    }
}