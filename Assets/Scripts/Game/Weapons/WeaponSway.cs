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
        private Vector3 _baseLocalPos;
        private Quaternion _baseLocalRot;
        private Vector2 _lastAngles; // (pitch, yaw)
        private Vector2 _smoothedDelta; // smoothed per-frame delta
        private Vector3 _curPos, _velPos;
        private Vector3 _curRotEuler, _velRot;

        private void Awake() {
            if(cam == null) {
                var c = GetComponentInParent<CinemachineCamera>();
                if(c) cam = c.transform;
            }

            var swayTransform = transform;
            _baseLocalPos = swayTransform.localPosition;
            _baseLocalRot = swayTransform.localRotation;

            var e = cam ? cam.eulerAngles : Vector3.zero;
            _lastAngles = new Vector2(e.x, e.y);
        }

        private void OnEnable() {
            var swayTransform = transform;
            swayTransform.localPosition = _baseLocalPos;
            swayTransform.localRotation = _baseLocalRot;

            _curPos = _velPos = Vector3.zero;
            _curRotEuler = _velRot = Vector3.zero;
            _smoothedDelta = Vector2.zero;

            var e = cam ? cam.eulerAngles : Vector3.zero;
            _lastAngles = new Vector2(e.x, e.y);
        }

        private void LateUpdate() {
            if(cam == null) {
                cam = GetComponentInParent<CinemachineCamera>().transform;
                return;
            }

            // 1) Compute per-frame angle delta (in degrees)
            var e = cam.eulerAngles;
            var dPitch = Mathf.DeltaAngle(_lastAngles.x, e.x); // + = look up
            var dYaw = Mathf.DeltaAngle(_lastAngles.y, e.y); // + = look right
            _lastAngles = new Vector2(e.x, e.y);

            // 2) Smooth the delta so it's not jittery
            var rawDelta = new Vector2(dPitch, dYaw);
            var t = 1f - deltaSmoothing;
            _smoothedDelta = Vector2.Lerp(_smoothedDelta, rawDelta, t);

            // 3) Build desired position offset (weapon lags opposite to look)
            var posX = Mathf.Clamp(-_smoothedDelta.y * posPerDegYaw, -posMaxX, posMaxX); // yaw → X
            var posY = Mathf.Clamp(-_smoothedDelta.x * posPerDegPitch, -posMaxY, posMaxY); // pitch → Y

            var combined = Mathf.Abs(_smoothedDelta.x) + Mathf.Abs(_smoothedDelta.y);
            var posZ = Mathf.Clamp(combined * posPerDegPush, -posMaxZ, posMaxZ);

            var targetPos = new Vector3(posX, posY, -posZ) * adsMultiplier;

            // 4) Build desired rotation offset (tiny tilt + bank)
            var rYaw = Mathf.Clamp(_smoothedDelta.y * rotPerDegYaw, -rotMaxYaw, rotMaxYaw);
            var rPitch = Mathf.Clamp(-_smoothedDelta.x * rotPerDegPitch, -rotMaxPitch, rotMaxPitch);
            var rRoll = Mathf.Clamp(_smoothedDelta.y * rotPerDegRoll, -rotMaxRoll, rotMaxRoll);

            var targetRot = new Vector3(rPitch, rYaw, rRoll) * adsMultiplier;

            // 5) Smoothly move toward offsets
            var dt = Time.deltaTime;
            var pSpeed = targetPos.sqrMagnitude > 0.0001f ? followPosSpeed : recenterSpeed;
            var rSpeed = targetRot.sqrMagnitude > 0.0001f ? followRotSpeed : recenterSpeed;

            var pTime = 1f / Mathf.Max(pSpeed, 0.01f);
            var rTime = 1f / Mathf.Max(rSpeed, 0.01f);

            _curPos = Vector3.SmoothDamp(_curPos, targetPos, ref _velPos, pTime, Mathf.Infinity, dt);
            _curRotEuler = SmoothDampEuler(_curRotEuler, targetRot, ref _velRot, rTime, dt);

            // 6) Apply
            transform.localPosition = _baseLocalPos + _curPos;
            transform.localRotation = _baseLocalRot * Quaternion.Euler(_curRotEuler);
        }

        private static Vector3 SmoothDampEuler(Vector3 current, Vector3 target, ref Vector3 vel, float smoothTime, float dt) {
            return new Vector3(
                Mathf.SmoothDamp(current.x, target.x, ref vel.x, smoothTime, Mathf.Infinity, dt),
                Mathf.SmoothDamp(current.y, target.y, ref vel.y, smoothTime, Mathf.Infinity, dt),
                Mathf.SmoothDamp(current.z, target.z, ref vel.z, smoothTime, Mathf.Infinity, dt)
            );
        }

        public void SetAdsMultiplier(float m) => adsMultiplier = Mathf.Clamp01(m);

        public void RecalibrateRestPose() {
            var swayTransform = transform;
            _baseLocalPos = swayTransform.localPosition;
            _baseLocalRot = swayTransform.localRotation;
            _curPos = _velPos = Vector3.zero;
            _curRotEuler = _velRot = Vector3.zero;
            _smoothedDelta = Vector2.zero;
        }
    }
}