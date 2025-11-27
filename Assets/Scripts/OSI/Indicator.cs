using UnityEngine;
using UnityEngine.UI;

namespace OSI {
    /// <summary>
    /// Assign this script to the indicator prefabs.
    /// </summary>
    public class Indicator : MonoBehaviour {
        [SerializeField] private IndicatorType indicatorType;
        private Image _indicatorImage;
        private Text _distanceText;

        /// <summary>
        /// Gets if the game object is active in hierarchy.
        /// </summary>
        public bool Active => transform.gameObject.activeInHierarchy;

        /// <summary>
        /// Gets the indicator type
        /// </summary>
        public IndicatorType Type => indicatorType;

        private void Awake() {
            _indicatorImage = transform.GetComponent<Image>();
            _distanceText = transform.GetComponentInChildren<Text>();
            if(_distanceText == null) return;
            
            _distanceText.color = Color.white;
            var shadow = _distanceText.GetComponent<Shadow>();
            if(shadow == null) {
                shadow = _distanceText.gameObject.AddComponent<Shadow>();
            }

            shadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
            shadow.effectDistance = new Vector2(1f, -1f);
        }

        /// <summary>
        /// Sets the image color for the indicator.
        /// </summary>
        /// <param name="color"></param>
        public void SetImageColor(Color color) {
            _indicatorImage.color = color;
        }

        /// <summary>
        /// Sets the distance text for the indicator.
        /// </summary>
        /// <param name="value"></param>
        public void SetDistanceText(float value) {
            _distanceText.text = value >= 0 ? Mathf.Floor(value) + " m" : "";
        }

        /// <summary>
        /// Sets the distance text rotation of the indicator.
        /// </summary>
        /// <param name="rotation"></param>
        public void SetTextRotation(Quaternion rotation) {
            _distanceText.rectTransform.rotation = rotation;
        }

        /// <summary>
        /// Sets the indicator as active or inactive.
        /// </summary>
        /// <param name="value"></param>
        public void Activate(bool value) {
            transform.gameObject.SetActive(value);
        }
    }

    public enum IndicatorType {
        Box,
        Arrow
    }
}