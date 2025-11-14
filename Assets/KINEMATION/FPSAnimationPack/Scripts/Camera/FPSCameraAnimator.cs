// Designed by KINEMATION, 2025.

using KINEMATION.FPSAnimationPack.Scripts.Player;
using KINEMATION.KAnimationCore.Runtime.Core;
using UnityEngine;

namespace KINEMATION.FPSAnimationPack.Scripts.Camera
{
    [AddComponentMenu("KINEMATION/FPS Animation Pack/FPS Camera Animator")]
    public class FPSCameraAnimator : MonoBehaviour
    {
        [SerializeField] private Transform cameraBone;
        
        private FPSCameraShake _activeShake;
        private Vector3 _cameraShake;
        private Vector3 _cameraShakeTarget;
        private float _cameraShakePlayback;

        private UnityEngine.Camera _camera;
        private FPSPlayer _player;
        private float _baseFov;
        
        public virtual void PlayCameraShake(FPSCameraShake newShake)
        {
            if (newShake == null) return;

            _activeShake = newShake;
            _cameraShakePlayback = 0f;

            _cameraShakeTarget.x = FPSCameraShake.GetTarget(_activeShake.pitch);
            _cameraShakeTarget.y = FPSCameraShake.GetTarget(_activeShake.yaw);
            _cameraShakeTarget.z = FPSCameraShake.GetTarget(_activeShake.roll);
        }
        
        protected virtual void UpdateCameraShake()
        {
            if (_activeShake == null) return;

            float length = _activeShake.shakeCurve.GetCurveLength();
            _cameraShakePlayback += Time.deltaTime * _activeShake.playRate;
            _cameraShakePlayback = Mathf.Clamp(_cameraShakePlayback, 0f, length);

            float alpha = KMath.ExpDecayAlpha(_activeShake.smoothSpeed, Time.deltaTime);
            if (!KAnimationMath.IsWeightRelevant(_activeShake.smoothSpeed))
            {
                alpha = 1f;
            }

            Vector3 target = _activeShake.shakeCurve.GetValue(_cameraShakePlayback);
            target.x *= _cameraShakeTarget.x;
            target.y *= _cameraShakeTarget.y;
            target.z *= _cameraShakeTarget.z;
            
            _cameraShake = Vector3.Lerp(_cameraShake, target, alpha);
            transform.rotation *= Quaternion.Euler(_cameraShake);
        }

        protected virtual void UpdateFOV()
        {
            if (_camera == null || _player == null) return;
            
            _camera.fieldOfView = Mathf.Lerp(_baseFov,
                _player.GetActiveWeapon().weaponSettings.aimFov, _player.AdsWeight);
        }

        private void Awake()
        {
            _player = transform.root.GetComponentInChildren<FPSPlayer>();
            _camera = GetComponent<UnityEngine.Camera>();
            _baseFov = _camera.fieldOfView;
        }

        private void LateUpdate()
        {
            transform.localRotation = _player.transform.localRotation * cameraBone.localRotation;
            UpdateCameraShake();
            UpdateFOV();
        }
    }
}
