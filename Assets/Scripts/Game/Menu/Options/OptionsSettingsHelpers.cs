using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Menu.Options {
    /// <summary>
    /// Static utility class for options menu shared logic.
    /// </summary>
    public static class OptionsSettingsHelpers {
        public struct VolumeInputFieldSetupRequest {
            public Slider Slider { get; set; }
            public TextField TextField { get; set; }
            public float MinValue { get; set; }
            public float MaxValue { get; set; }
            public bool IsPercentage { get; set; }
        }

        public const float ShadowDistanceMax = 500f;

        // Target FPS indices (matches dropdown order: 30, 60, 120, 144, Unlimited)
        private const int TargetFpsIndex30 = 0;
        private const int TargetFpsIndex60 = 1;
        private const int TargetFpsIndex120 = 2;
        private const int TargetFpsIndex144 = 3;
        private const int TargetFpsIndexUnlimited = 4;
        public const int TargetFpsDefaultIndex = TargetFpsIndex60;

        public static float LinearToDb(float linear) => linear <= 0f ? -80f : 20f * Mathf.Log10(linear);
        public static float DbToLinear(float db) => db <= -80f ? 0f : Mathf.Pow(10f, db / 20f);

        public static int MsaaValueToIndex(int value) =>
            value switch { 1 => 0, 2 => 1, 4 => 2, 8 => 3, _ => 0 };

        public static int MsaaIndexToValue(int index) =>
            index switch { 0 => 1, 1 => 2, 2 => 4, 3 => 8, _ => 1 };

        public static int ShadowResolutionValueToIndex(int value) =>
            value switch {
                512 => 0, 1024 => 1, 2048 => 2, 4096 => 3,
                <= 512 => 0, <= 1024 => 1, <= 2048 => 2, _ => 3
            };

        public static int ShadowResolutionIndexToValue(int index) =>
            index switch { 0 => 512, 1 => 1024, 2 => 2048, 3 => 4096, _ => 2048 };

        public static int TargetFpsIndexToValue(int index) =>
            index switch {
                TargetFpsIndex30 => 30,
                TargetFpsIndex60 => 60,
                TargetFpsIndex120 => 120,
                TargetFpsIndex144 => 144,
                TargetFpsIndexUnlimited => -1,
                _ => Application.targetFrameRate
            };

        public static bool GetCheckboxValue(Button button) {
            return button != null && button.ClassListContains("checked");
        }

        public static void SetCheckboxValue(Button button, bool value) {
            if(button == null) return;
            if(value) {
                button.AddToClassList("checked");
            } else {
                button.RemoveFromClassList("checked");
            }
        }

        public static void ToggleCheckbox(Button button) {
            if(button == null) return;
            var currentValue = GetCheckboxValue(button);
            SetCheckboxValue(button, !currentValue);
        }

        /// <summary>
        /// Binds a slider value to a text field displaying percentage (0-100). Returns cleanup action.
        /// </summary>
        public static Action BindSliderToPercentTextField(Slider slider, TextField textField) {
            if(slider == null || textField == null) return () => { };
            EventCallback<ChangeEvent<float>> handler = evt => textField.value = Mathf.RoundToInt(evt.newValue * 100) + "%";
            slider.RegisterValueChangedCallback(handler);
            return () => slider.UnregisterCallback(handler);
        }

        /// <summary>
        /// Binds a slider value to a text field displaying integer. Returns cleanup action.
        /// </summary>
        public static Action BindSliderToIntegerTextField(Slider slider, TextField textField) {
            if(slider == null || textField == null) return () => { };
            EventCallback<ChangeEvent<float>> handler = evt => textField.value = Mathf.RoundToInt(evt.newValue).ToString();
            slider.RegisterValueChangedCallback(handler);
            return () => slider.UnregisterCallback(handler);
        }

        /// <summary>
        /// Applies parsed text field value to slider (percentage or raw float).
        /// </summary>
        private static void ApplyTextFieldValue(Slider slider, TextField textField, float minValue, float maxValue, bool isPercentage) {
            if(slider == null || textField == null) return;
            var input = textField.value.Trim();
            if(isPercentage && input.EndsWith("%")) input = input.Replace("%", "").Trim();
            if(string.IsNullOrEmpty(input)) {
                if(isPercentage) textField.value = Mathf.RoundToInt(slider.value * 100) + "%";
                else textField.value = slider.value.ToString("F2");
                return;
            }
            if(float.TryParse(input, out var parsedValue)) {
                if(isPercentage) parsedValue /= 100f;
                var clamped = Mathf.Clamp(parsedValue, minValue, maxValue);
                slider.value = clamped;
                textField.value = isPercentage ? Mathf.RoundToInt(clamped * 100) + "%" : clamped.ToString("F2");
            } else {
                textField.value = isPercentage ? Mathf.RoundToInt(slider.value * 100) + "%" : slider.value.ToString("F2");
            }
        }

        /// <summary>
        /// Applies parsed integer text field value to slider.
        /// </summary>
        private static void ApplyIntegerTextFieldValue(Slider slider, TextField textField, float minValue, float maxValue) {
            if(slider == null || textField == null) return;
            var input = textField.value.Trim();
            if(string.IsNullOrEmpty(input)) {
                textField.value = Mathf.RoundToInt(slider.value).ToString();
                return;
            }
            if(int.TryParse(input, out var parsedValue)) {
                var clamped = Mathf.Clamp(parsedValue, minValue, maxValue);
                slider.value = clamped;
                textField.value = Mathf.RoundToInt(clamped).ToString();
            } else {
                textField.value = Mathf.RoundToInt(slider.value).ToString();
            }
        }

        /// <summary>
        /// Sets up volume or sensitivity input field with filtering and apply on Enter/blur. Returns cleanup action.
        /// </summary>
        public static Action SetupVolumeInputField(in VolumeInputFieldSetupRequest request) {
            var slider = request.Slider;
            var textField = request.TextField;
            var minValue = request.MinValue;
            var maxValue = request.MaxValue;
            var isPercentage = request.IsPercentage;
            if(slider == null || textField == null) return () => { };
            textField.maxLength = isPercentage ? 4 : 5;
            textField.isDelayed = false;

            EventCallback<ChangeEvent<string>> valueChangedHandler = evt => {
                var newValue = evt.newValue;
                if(isPercentage && newValue.EndsWith("%")) newValue = newValue.Replace("%", "");
                var filtered = "";
                foreach(var c in newValue) {
                    if(char.IsDigit(c)) filtered += c;
                    else if(c == '.' && !isPercentage && !filtered.Contains(".")) filtered += c;
                }
                filtered = isPercentage ? filtered.Length > 3 ? filtered[..3] : filtered : filtered.Length > 5 ? filtered[..5] : filtered;
                if(isPercentage && !string.IsNullOrEmpty(filtered)) filtered += "%";
                if(filtered != evt.newValue) textField.value = filtered;
            };
            textField.RegisterValueChangedCallback(valueChangedHandler);

            EventCallback<KeyDownEvent> keyDownHandler = evt => {
                if(evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter) return;
                ApplyTextFieldValue(slider, textField, minValue, maxValue, isPercentage);
                textField.Blur();
            };
            textField.RegisterCallback(keyDownHandler);

            EventCallback<BlurEvent> blurHandler = _ => ApplyTextFieldValue(slider, textField, minValue, maxValue, isPercentage);
            textField.RegisterCallback(blurHandler);

            return () => {
                textField.UnregisterCallback(valueChangedHandler);
                textField.UnregisterCallback(keyDownHandler);
                textField.UnregisterCallback(blurHandler);
            };
        }

        /// <summary>
        /// Sets up integer slider input field. Returns cleanup action.
        /// </summary>
        public static Action SetupIntegerSliderInputField(Slider slider, TextField textField, float minValue, float maxValue) {
            if(slider == null || textField == null) return () => { };
            textField.maxLength = 6;
            textField.isDelayed = false;

            EventCallback<ChangeEvent<string>> valueChangedHandler = evt => {
                var filtered = new string(evt.newValue.Where(char.IsDigit).ToArray());
                if(filtered.Length > 6) filtered = filtered[..6];
                if(filtered != evt.newValue) textField.value = filtered;
            };
            textField.RegisterValueChangedCallback(valueChangedHandler);

            EventCallback<KeyDownEvent> keyDownHandler = evt => {
                if(evt.keyCode is not (KeyCode.Return or KeyCode.KeypadEnter)) return;
                ApplyIntegerTextFieldValue(slider, textField, minValue, maxValue);
                textField.Blur();
            };
            textField.RegisterCallback(keyDownHandler);

            EventCallback<BlurEvent> blurHandler = _ => ApplyIntegerTextFieldValue(slider, textField, minValue, maxValue);
            textField.RegisterCallback(blurHandler);

            return () => {
                textField.UnregisterCallback(valueChangedHandler);
                textField.UnregisterCallback(keyDownHandler);
                textField.UnregisterCallback(blurHandler);
            };
        }
    }
}
