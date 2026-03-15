using UnityEngine;
using UnityEngine.Rendering;

namespace Game.UI.Misc {
    /// <summary>
    /// Drives a dedicated global volume weight for menu/options blur.
    /// Assign the target Volume in the inspector.
    /// </summary>
    [DisallowMultipleComponent]
    public class MenuBlurVolumeController : MonoBehaviour {
        [SerializeField] private Volume blurVolume;
        [SerializeField] private float openSpeed = 7f;
        [SerializeField] private float closeSpeed = 16f;
        [SerializeField] private float maxWeight = 1f;
        [SerializeField] private bool instantClose;

        private float _targetWeight;
        private float _currentWeight;

        private void Awake() {
            _currentWeight = blurVolume != null ? Mathf.Clamp01(blurVolume.weight) : 0f;
            _targetWeight = 0f;
            ApplyWeight(0f);
        }

        private void Update() {
            if(blurVolume == null) {
                return;
            }

            if(Mathf.Approximately(_currentWeight, _targetWeight)) {
                return;
            }

            var speed = _targetWeight > _currentWeight ? openSpeed : closeSpeed;
            _currentWeight = Mathf.MoveTowards(_currentWeight, _targetWeight, speed * Time.unscaledDeltaTime);
            ApplyWeight(_currentWeight);
        }

        public void SetBlurActive(bool isActive) {
            if(!isActive && instantClose) {
                _targetWeight = 0f;
                _currentWeight = 0f;
                ApplyWeight(0f);
                return;
            }

            _targetWeight = isActive ? Mathf.Clamp01(maxWeight) : 0f;
        }

        public void SetBlurVolume(Volume volume) {
            blurVolume = volume;
            _currentWeight = 0f;
            _targetWeight = 0f;
            ApplyWeight(0f);
        }

        private void OnDisable() {
            ApplyWeight(0f);
            _currentWeight = 0f;
            _targetWeight = 0f;
        }

        private void ApplyWeight(float value) {
            if(blurVolume == null) {
                return;
            }

            blurVolume.weight = Mathf.Clamp01(value);
        }
    }
}
