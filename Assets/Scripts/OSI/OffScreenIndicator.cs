using System;
using System.Collections.Generic;
using UnityEngine;

namespace OSI {
    /// <summary>
    /// Attach the script to the off-screen indicator panel.
    /// </summary>
    [DefaultExecutionOrder(-1)]
    public class OffScreenIndicator : MonoBehaviour {
        [Range(0.5f, 0.9f)]
        [Tooltip("Distance offset of the indicators from the centre of the screen")]
        [SerializeField]
        private float screenBoundOffset = 0.9f;

        [SerializeField] private bool updateScreenBoundsOnScreenChange;

        private Camera _mainCamera;
        private Vector3 _screenCentre;
        private Vector3 _screenBounds;
        private ScreenOrientation _currentOrientation;
        private float _currentAspectRatio;

        private readonly List<Target> _targets = new();

        public static Action<Target, bool> TargetStateChanged;

        public void Awake() {
            _mainCamera = Camera.main;
            _screenCentre = new Vector3(Screen.width, Screen.height, 0) / 2;
            _screenBounds = _screenCentre * screenBoundOffset;
            _currentOrientation = Screen.orientation;
            _currentAspectRatio = (float)Screen.width / Screen.height;
            TargetStateChanged += HandleTargetStateChanged;
        }

        private void LateUpdate() {
            CheckScreenOrientation();
            DrawIndicators();
        }

        /// <summary>
        /// Draw the indicators on the screen and set their position and rotation and other properties.
        /// </summary>
        private void DrawIndicators() {
            foreach(var target in _targets) {
                var targetPosition = target.GetWorldPosition();
                var screenPosition =
                    OffScreenIndicatorCore.GetScreenPosition(_mainCamera, targetPosition);
                var isTargetVisible = OffScreenIndicatorCore.IsTargetVisible(screenPosition);
                var distanceFromCamera = target.NeedDistanceText
                    ? target.GetDistanceFromCamera(_mainCamera.transform.position)
                    : float.MinValue; // Gets the target distance from the camera.
                Indicator indicator = null;

                if(target.NeedBoxIndicator && isTargetVisible) {
                    screenPosition.z = 0;
                    indicator = GetIndicator(ref target.indicator,
                        IndicatorType.BOX); // Gets the box indicator from the pool.
                } else {
                    var angle = float.MinValue;
                    if(target.NeedArrowIndicator && !isTargetVisible) {
                        OffScreenIndicatorCore.GetArrowIndicatorPositionAndAngle(ref screenPosition, ref angle,
                            _screenCentre, _screenBounds);
                        indicator = GetIndicator(ref target.indicator,
                            IndicatorType.ARROW); // Gets the arrow indicator from the pool.
                        indicator.transform.rotation =
                            Quaternion.Euler(0, 0, angle * Mathf.Rad2Deg); // Sets the rotation for the arrow indicator.
                    } else {
                        target.indicator?.Activate(false);
                        target.indicator = null;
                    }
                }

                if(!indicator) continue;
                indicator.SetImageColor(target.TargetColor); // Sets the image color of the indicator.
                indicator.SetDistanceText(distanceFromCamera); //Set the distance text for the indicator.
                indicator.transform.position = screenPosition; //Sets the position of the indicator on the screen.
                indicator.SetTextRotation(Quaternion
                    .identity); // Sets the rotation of the distance text of the indicator.
            }
        }

        /// <summary>
        /// Check if the screen orientation has changed and update the screen bounds accordingly.
        /// </summary>
        private void CheckScreenOrientation() {
            if(!updateScreenBoundsOnScreenChange ||
               (_currentOrientation == Screen.orientation && !HasAspectRatioChanged())) return;
            _screenCentre = new Vector3(Screen.width, Screen.height, 0) / 2;
            _screenBounds = _screenCentre * screenBoundOffset;
            _currentAspectRatio = (float)Screen.width / Screen.height;
            _currentOrientation = Screen.orientation;
        }

        /// <summary>
        /// Check if the aspect ratio has changed.
        /// Using Mathf.Approximately to compare the aspect ratio as floating point values can have precision issues.
        /// </summary>
        /// <returns></returns>
        private bool HasAspectRatioChanged() {
            var cAp = (float)Screen.width / Screen.height;
            return !Mathf.Approximately(cAp, _currentAspectRatio);
        }

        /// <summary>
        /// 1. Add the target to targets list if <paramref name="active"/> is true.
        /// 2. If <paramref name="active"/> is false deactivate the targets indicator, 
        ///     set its reference null and remove it from the targets list.
        /// </summary>
        /// <param name="target"></param>
        /// <param name="active"></param>
        private void HandleTargetStateChanged(Target target, bool active) {
            if(active) {
                _targets.Add(target);
            } else {
                target.indicator?.Activate(false);
                target.indicator = null;
                _targets.Remove(target);
            }
        }

        /// <summary>
        /// Get the indicator for the target.
        /// 1. If it's not null and of the same required <paramref name="type"/> 
        ///     then return the same indicator;
        /// 2. If it's not null but is of different type from <paramref name="type"/> 
        ///     then deactivate the old reference so that it returns to the pool 
        ///     and request one of another type from pool.
        /// 3. If its null then request one from the pool of <paramref name="type"/>.
        /// </summary>
        /// <param name="indicator"></param>
        /// <param name="type"></param>
        /// <returns></returns>
        private Indicator GetIndicator(ref Indicator indicator, IndicatorType type) {
            if(indicator != null) {
                if(indicator.Type == type) return indicator;
                indicator.Activate(false);
                // Sets the indicator as active.
            }

            // Sets the indicator as active.
            indicator = type == IndicatorType.BOX
                ? BoxObjectPool.Current.GetPooledObject()
                : ArrowObjectPool.Current.GetPooledObject();
            indicator.Activate(true); // Sets the indicator as active.

            return indicator;
        }

        private void OnDestroy() {
            TargetStateChanged -= HandleTargetStateChanged;
        }
    }
}