using System;
using System.Collections.Generic;
using Events;
using Game.Settings;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;

namespace Game.Menu.Options {
    public class OptionsVideoTabHandler : IOptionsTabHandler {
        private struct ResolutionData {
            internal readonly int Width;
            internal readonly int Height;
            internal readonly string AspectRatio;
            internal readonly string DisplayString;

            public ResolutionData(int w, int h) {
                Width = w;
                Height = h;
                AspectRatio = CalculateAspectRatio(w, h);
                DisplayString = $"{w} x {h}";
            }

            private static string CalculateAspectRatio(int width, int height) {
                var gcd = Gcd(width, height);
                var w = width / gcd;
                var h = height / gcd;
                var ratio = (float)width / height;
                if(Mathf.Approximately(ratio, 16f / 9f)) return "16:9";
                if(Mathf.Approximately(ratio, 16f / 10f)) return "16:10";
                if(Mathf.Approximately(ratio, 21f / 9f)) return "21:9";
                return Mathf.Approximately(ratio, 4f / 3f) ? "4:3" : $"{w}:{h}";
            }

            private static int Gcd(int a, int b) {
                while(b != 0) {
                    var temp = b;
                    b = a % b;
                    a = temp;
                }
                return a;
            }
        }

        private DropdownField _windowModeDropdown;
        private DropdownField _aspectRatioDropdown;
        private DropdownField _resolutionDropdown;
        private DropdownField _msaaDropdown;
        private Slider _shadowDistanceSlider;
        private TextField _shadowDistanceValue;
        private DropdownField _shadowResolutionDropdown;
        private Button _bloomButton;
        private Button _motionBlurButton;
        private Button _filmGrainButton;
        private Button _vignetteButton;
        private Button _vsyncButton;
        private DropdownField _fpsDropdown;

        private readonly List<ResolutionData> _allResolutions = new();
        private readonly List<ResolutionData> _filteredResolutions = new();
        private readonly HashSet<string> _availableAspectRatios = new();

        private int _originalWindowMode;
        private string _originalAspectRatio = "";
        private int _originalResolutionIndex;
        private int _originalMsaa;
        private float _originalShadowDistance;
        private int _originalShadowResolution;
        private bool _originalBloom;
        private bool _originalMotionBlur;
        private bool _originalFilmGrain;
        private bool _originalVignette;
        private bool _originalVsync;
        private int _originalTargetFPS;

        public void FindElements(IOptionsTabContext ctx) {
            _windowModeDropdown = ctx.QOptional<DropdownField>("window-mode");
            _aspectRatioDropdown = ctx.QOptional<DropdownField>("aspect-ratio");
            _resolutionDropdown = ctx.QOptional<DropdownField>("resolution");
            _msaaDropdown = ctx.QOptional<DropdownField>("msaa");
            _shadowDistanceSlider = ctx.QOptional<Slider>("shadow-distance");
            _shadowDistanceValue = ctx.QOptional<TextField>("shadow-distance-value");
            _shadowResolutionDropdown = ctx.QOptional<DropdownField>("shadow-resolution");
            _bloomButton = ctx.QOptional<Button>("bloom");
            _motionBlurButton = ctx.QOptional<Button>("motion-blur");
            _filmGrainButton = ctx.QOptional<Button>("film-grain");
            _vignetteButton = ctx.QOptional<Button>("vignette");
            _vsyncButton = ctx.QOptional<Button>("vsync");
            _fpsDropdown = ctx.QOptional<DropdownField>("target-fps");
        }

        public void SetupCallbacks(IOptionsTabContext ctx) {
            SetupWindowModeDropdown();
            SetupResolutionDropdowns(ctx);
            SetupMsaaDropdown();
            SetupShadowDistanceSlider(ctx);
            SetupShadowResolutionDropdown();
            if(_fpsDropdown != null) _fpsDropdown.choices = new List<string> { "30", "60", "120", "144", "Unlimited" };
            RegisterCheckboxButtons(ctx, _bloomButton, _motionBlurButton, _filmGrainButton, _vignetteButton, _vsyncButton);
        }

        private static void RegisterCheckboxButtons(IOptionsTabContext ctx, params Button[] buttons) {
            foreach(var button in buttons) {
                if(button == null) continue;
                var b = button;
                EventCallback<ClickEvent> handler = _ => OptionsSettingsHelpers.ToggleCheckbox(b);
                button.RegisterCallback(handler);
                ctx.RegisterCleanup(() => button.UnregisterCallback(handler));
            }
        }

        private void SetupWindowModeDropdown() {
            if(_windowModeDropdown == null) return;
            _windowModeDropdown.choices = new List<string> { "Windowed", "Borderless Windowed", "Fullscreen" };
        }

        private void SetupResolutionDropdowns(IOptionsTabContext ctx) {
            if(_aspectRatioDropdown == null || _resolutionDropdown == null) return;
            _allResolutions.Clear();
            var seenResolutions = new HashSet<string>();
            foreach(var res in Screen.resolutions) {
                var resData = new ResolutionData(res.width, res.height);
                var key = $"{resData.Width}x{resData.Height}";
                if(!seenResolutions.Add(key)) continue;
                _allResolutions.Add(resData);
                _availableAspectRatios.Add(resData.AspectRatio);
            }
            _allResolutions.Sort((a, b) => a.Width != b.Width ? b.Width.CompareTo(a.Width) : b.Height.CompareTo(a.Height));

            var supportedAspectRatios = new List<string>();
            if(_availableAspectRatios.Contains("16:9")) supportedAspectRatios.Add("16:9");
            if(_availableAspectRatios.Contains("16:10")) supportedAspectRatios.Add("16:10");
            if(_availableAspectRatios.Contains("21:9")) supportedAspectRatios.Add("21:9");
            if(_availableAspectRatios.Contains("4:3")) supportedAspectRatios.Add("4:3");
            if(supportedAspectRatios.Count == 0) supportedAspectRatios.Add("16:9");

            _aspectRatioDropdown.choices = supportedAspectRatios;
            var defaultAspectRatio = supportedAspectRatios.Contains("16:9") ? "16:9" : supportedAspectRatios[0];
            _aspectRatioDropdown.value = defaultAspectRatio;
            FilterResolutions(defaultAspectRatio);

            EventCallback<ChangeEvent<string>> aspectRatioHandler = _ => FilterResolutions(_aspectRatioDropdown.value);
            _aspectRatioDropdown.RegisterValueChangedCallback(aspectRatioHandler);
            ctx.RegisterCleanup(() => _aspectRatioDropdown.UnregisterCallback(aspectRatioHandler));
        }

        private void FilterResolutions(string aspectRatio) {
            if(_resolutionDropdown == null) return;
            _filteredResolutions.Clear();
            foreach(var res in _allResolutions) {
                if(res.AspectRatio == aspectRatio) _filteredResolutions.Add(res);
            }
            var resolutionChoices = new List<string>();
            foreach(var res in _filteredResolutions) resolutionChoices.Add(res.DisplayString);
            _resolutionDropdown.choices = resolutionChoices;
            var currentIndex = FindCurrentResolutionIndex();
            if(currentIndex >= 0) _resolutionDropdown.index = currentIndex;
            else if(_filteredResolutions.Count > 0) _resolutionDropdown.index = 0;
        }

        private int FindCurrentResolutionIndex() {
            var currentWidth = Screen.width;
            var currentHeight = Screen.height;
            for(var i = 0; i < _filteredResolutions.Count; i++) {
                if(_filteredResolutions[i].Width == currentWidth && _filteredResolutions[i].Height == currentHeight)
                    return i;
            }
            return -1;
        }

        private void SetupMsaaDropdown() {
            if(_msaaDropdown == null) return;
            _msaaDropdown.choices = new List<string> { "Off", "2x", "4x", "8x" };
        }

        private void SetupShadowDistanceSlider(IOptionsTabContext ctx) {
            if(_shadowDistanceSlider == null || _shadowDistanceValue == null) return;
            ctx.RegisterCleanup(OptionsSettingsHelpers.BindSliderToIntegerTextField(_shadowDistanceSlider, _shadowDistanceValue));
            ctx.RegisterCleanup(OptionsSettingsHelpers.SetupIntegerSliderInputField(_shadowDistanceSlider, _shadowDistanceValue, 0f, OptionsSettingsHelpers.ShadowDistanceMax));
        }

        private void SetupShadowResolutionDropdown() {
            if(_shadowResolutionDropdown == null) return;
            _shadowResolutionDropdown.choices = new List<string> { "Low", "Medium", "High", "Ultra" };
        }

        public void Load(SettingsData data) {
            if(_windowModeDropdown != null) {
                var savedWindowMode = data.video != null ? data.video.windowMode : GetCurrentWindowModeIndex();
                _windowModeDropdown.index = Mathf.Clamp(savedWindowMode, 0, _windowModeDropdown.choices.Count - 1);
            }
            var savedAspectRatio = data.video != null ? data.video.aspectRatio : "";
            if(_aspectRatioDropdown != null && _aspectRatioDropdown.choices.Count > 0) {
                if(string.IsNullOrEmpty(savedAspectRatio)) {
                    var currentRes = new ResolutionData(Screen.width, Screen.height);
                    savedAspectRatio = currentRes.AspectRatio;
                }
                var aspectRatioIndex = _aspectRatioDropdown.choices.IndexOf(savedAspectRatio);
                if(aspectRatioIndex >= 0) {
                    _aspectRatioDropdown.index = aspectRatioIndex;
                    FilterResolutions(savedAspectRatio);
                } else {
                    _aspectRatioDropdown.index = 0;
                    FilterResolutions(_aspectRatioDropdown.choices[0]);
                }
            }
            if(_resolutionDropdown != null && _filteredResolutions.Count > 0) {
                var savedWidth = data.video is { resolutionWidth: > 0 } ? data.video.resolutionWidth : Screen.width;
                var savedHeight = data.video is { resolutionHeight: > 0 } ? data.video.resolutionHeight : Screen.height;
                var resolutionIndex = -1;
                for(var i = 0; i < _filteredResolutions.Count; i++) {
                    if(_filteredResolutions[i].Width != savedWidth || _filteredResolutions[i].Height != savedHeight) continue;
                    resolutionIndex = i;
                    break;
                }
                if(resolutionIndex >= 0) _resolutionDropdown.index = resolutionIndex;
                else {
                    var currentIndex = FindCurrentResolutionIndex();
                    _resolutionDropdown.index = currentIndex >= 0 ? currentIndex : 0;
                }
            }
            LoadGraphicsSettings(data);
            if(_bloomButton != null) OptionsSettingsHelpers.SetCheckboxValue(_bloomButton, data.video == null || data.video.bloomEnabled);
            if(_motionBlurButton != null) OptionsSettingsHelpers.SetCheckboxValue(_motionBlurButton, data.video == null || data.video.motionBlurEnabled);
            if(_filmGrainButton != null) OptionsSettingsHelpers.SetCheckboxValue(_filmGrainButton, data.video == null || data.video.filmGrainEnabled);
            if(_vignetteButton != null) OptionsSettingsHelpers.SetCheckboxValue(_vignetteButton, data.video == null || data.video.vignetteEnabled);
            if(_vsyncButton != null) OptionsSettingsHelpers.SetCheckboxValue(_vsyncButton, data.video is { vsync: true });
            if(_fpsDropdown != null) _fpsDropdown.index = data.video != null ? data.video.targetFpsIndex : OptionsSettingsHelpers.TargetFpsDefaultIndex;
        }

        private void LoadGraphicsSettings(SettingsData data) {
            var urpAsset = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            var currentMsaa = 1;
            var currentShadowDistance = 300f;
            var currentShadowResolution = 2048;
            if(urpAsset != null) {
                currentMsaa = urpAsset.msaaSampleCount;
                currentShadowDistance = urpAsset.shadowDistance;
                currentShadowResolution = urpAsset.mainLightShadowmapResolution;
            }
            if(_msaaDropdown != null) {
                var savedMsaa = data.video is { msaa: > 0 } ? data.video.msaa : currentMsaa;
                _msaaDropdown.index = Mathf.Clamp(OptionsSettingsHelpers.MsaaValueToIndex(savedMsaa), 0, _msaaDropdown.choices.Count - 1);
            }
            if(_shadowDistanceSlider != null) {
                var savedShadowDistance = data.video is { shadowDistance: > 0f } ? data.video.shadowDistance : currentShadowDistance;
                _shadowDistanceSlider.value = Mathf.Clamp(savedShadowDistance, 0f, OptionsSettingsHelpers.ShadowDistanceMax);
                if(_shadowDistanceValue != null) _shadowDistanceValue.value = Mathf.RoundToInt(_shadowDistanceSlider.value).ToString();
            }

            if(_shadowResolutionDropdown == null) return;
            var savedShadowResolution = data.video is { shadowResolution: > 0 } ? data.video.shadowResolution : currentShadowResolution;
            _shadowResolutionDropdown.index = Mathf.Clamp(OptionsSettingsHelpers.ShadowResolutionValueToIndex(savedShadowResolution), 0, _shadowResolutionDropdown.choices.Count - 1);
        }

        public void Save(SettingsData data) {
            if(_windowModeDropdown != null && data.video != null) data.video.windowMode = _windowModeDropdown.index;
            if(_aspectRatioDropdown != null && data.video != null) data.video.aspectRatio = _aspectRatioDropdown.value;
            if(_resolutionDropdown is { index: >= 0 } && _resolutionDropdown.index < _filteredResolutions.Count && data.video != null) {
                var selectedRes = _filteredResolutions[_resolutionDropdown.index];
                data.video.resolutionWidth = selectedRes.Width;
                data.video.resolutionHeight = selectedRes.Height;
            }
            if(_msaaDropdown != null && data.video != null) data.video.msaa = OptionsSettingsHelpers.MsaaIndexToValue(_msaaDropdown.index);
            if(_shadowDistanceSlider != null && data.video != null) data.video.shadowDistance = _shadowDistanceSlider.value;
            if(_shadowResolutionDropdown != null && data.video != null) data.video.shadowResolution = OptionsSettingsHelpers.ShadowResolutionIndexToValue(_shadowResolutionDropdown.index);
            if(data.video != null && _bloomButton != null) data.video.bloomEnabled = OptionsSettingsHelpers.GetCheckboxValue(_bloomButton);
            if(data.video != null && _motionBlurButton != null) data.video.motionBlurEnabled = OptionsSettingsHelpers.GetCheckboxValue(_motionBlurButton);
            if(data.video != null && _filmGrainButton != null) data.video.filmGrainEnabled = OptionsSettingsHelpers.GetCheckboxValue(_filmGrainButton);
            if(data.video != null && _vignetteButton != null) data.video.vignetteEnabled = OptionsSettingsHelpers.GetCheckboxValue(_vignetteButton);
            if(data.video != null) data.video.vsync = OptionsSettingsHelpers.GetCheckboxValue(_vsyncButton);
            if(_fpsDropdown != null && data.video != null) data.video.targetFpsIndex = _fpsDropdown.index;
        }

        public void StoreOriginal() {
            _originalWindowMode = _windowModeDropdown?.index ?? 0;
            _originalAspectRatio = _aspectRatioDropdown?.value ?? "";
            _originalResolutionIndex = _resolutionDropdown?.index ?? 0;
            _originalMsaa = _msaaDropdown?.index ?? 0;
            _originalShadowDistance = _shadowDistanceSlider?.value ?? 50f;
            _originalShadowResolution = _shadowResolutionDropdown?.index ?? 2;
            _originalBloom = OptionsSettingsHelpers.GetCheckboxValue(_bloomButton);
            _originalMotionBlur = OptionsSettingsHelpers.GetCheckboxValue(_motionBlurButton);
            _originalFilmGrain = OptionsSettingsHelpers.GetCheckboxValue(_filmGrainButton);
            _originalVignette = OptionsSettingsHelpers.GetCheckboxValue(_vignetteButton);
            _originalVsync = OptionsSettingsHelpers.GetCheckboxValue(_vsyncButton);
            _originalTargetFPS = _fpsDropdown?.index ?? OptionsSettingsHelpers.TargetFpsDefaultIndex;
        }

        public bool HasUnsavedChanges() {
            return IndexChanged(_windowModeDropdown, _originalWindowMode) ||
                   ValueChanged(_aspectRatioDropdown, _originalAspectRatio) ||
                   IndexChanged(_resolutionDropdown, _originalResolutionIndex) ||
                   IndexChanged(_msaaDropdown, _originalMsaa) ||
                   FloatChanged(_shadowDistanceSlider?.value, _originalShadowDistance) ||
                   IndexChanged(_shadowResolutionDropdown, _originalShadowResolution) ||
                   OptionsSettingsHelpers.GetCheckboxValue(_bloomButton) != _originalBloom ||
                   OptionsSettingsHelpers.GetCheckboxValue(_motionBlurButton) != _originalMotionBlur ||
                   OptionsSettingsHelpers.GetCheckboxValue(_filmGrainButton) != _originalFilmGrain ||
                   OptionsSettingsHelpers.GetCheckboxValue(_vignetteButton) != _originalVignette ||
                   OptionsSettingsHelpers.GetCheckboxValue(_vsyncButton) != _originalVsync ||
                   IndexChanged(_fpsDropdown, _originalTargetFPS);
        }

        public void RefreshDisplay() {
            if(_shadowDistanceValue != null && _shadowDistanceSlider != null)
                _shadowDistanceValue.value = Mathf.RoundToInt(_shadowDistanceSlider.value).ToString();
        }

        public void ApplyToRuntime() {
            ApplyWindowModeAndResolution();
            ApplyUrpGraphicsSettings();
            ApplyVideoRuntimeSettings();
        }

        private void ApplyWindowModeAndResolution() {
            if(_windowModeDropdown == null || _resolutionDropdown == null) return;
            if(_resolutionDropdown.index < 0 || _resolutionDropdown.index >= _filteredResolutions.Count) return;
            var selectedRes = _filteredResolutions[_resolutionDropdown.index];
            var fullScreenMode = _windowModeDropdown.index switch {
                0 => FullScreenMode.Windowed,
                1 => FullScreenMode.FullScreenWindow,
                2 => FullScreenMode.ExclusiveFullScreen,
                _ => FullScreenMode.FullScreenWindow
            };
            Screen.SetResolution(selectedRes.Width, selectedRes.Height, fullScreenMode);
            EventBus.Publish(new ResolutionChangedEvent(selectedRes.Width, selectedRes.Height));
        }

        private void ApplyUrpGraphicsSettings() {
            var urpAsset = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            if(urpAsset == null) return;
            if(_msaaDropdown != null) urpAsset.msaaSampleCount = OptionsSettingsHelpers.MsaaIndexToValue(_msaaDropdown.index);
            if(_shadowDistanceSlider != null) urpAsset.shadowDistance = _shadowDistanceSlider.value;
            if(_shadowResolutionDropdown != null)
                urpAsset.mainLightShadowmapResolution = OptionsSettingsHelpers.ShadowResolutionIndexToValue(_shadowResolutionDropdown.index);
        }

        private void ApplyVideoRuntimeSettings() {
            var video = GameSettings.Data.video;
            var bloomEnabled = _bloomButton != null ? OptionsSettingsHelpers.GetCheckboxValue(_bloomButton) : video == null || video.bloomEnabled;
            var motionBlurEnabled = _motionBlurButton != null ? OptionsSettingsHelpers.GetCheckboxValue(_motionBlurButton) : video == null || video.motionBlurEnabled;
            var filmGrainEnabled = _filmGrainButton != null ? OptionsSettingsHelpers.GetCheckboxValue(_filmGrainButton) : video == null || video.filmGrainEnabled;
            var vignetteEnabled = _vignetteButton != null ? OptionsSettingsHelpers.GetCheckboxValue(_vignetteButton) : video == null || video.vignetteEnabled;
            VideoSettingsRuntimeApplier.ApplyBloomEnabled(bloomEnabled);
            VideoSettingsRuntimeApplier.ApplyMotionBlurEnabled(motionBlurEnabled);
            VideoSettingsRuntimeApplier.ApplyFilmGrainEnabled(filmGrainEnabled);
            VideoSettingsRuntimeApplier.ApplyVignetteEnabled(vignetteEnabled);
            QualitySettings.vSyncCount = OptionsSettingsHelpers.GetCheckboxValue(_vsyncButton) ? 1 : 0;
            if(_fpsDropdown != null) Application.targetFrameRate = OptionsSettingsHelpers.TargetFpsIndexToValue(_fpsDropdown.index);
        }

        private static int GetCurrentWindowModeIndex() =>
            Screen.fullScreenMode switch {
                FullScreenMode.Windowed => 0,
                FullScreenMode.FullScreenWindow => 1,
                FullScreenMode.ExclusiveFullScreen => 2,
                _ => 1
            };

        private static bool ValueChanged(DropdownField d, string orig) => d != null && !string.Equals(d.value, orig, StringComparison.Ordinal);
        private static bool IndexChanged(DropdownField d, int orig) => d != null && d.index != orig;
        private static bool FloatChanged(float? a, float b) => a.HasValue && !Mathf.Approximately(a.Value, b);
    }
}
