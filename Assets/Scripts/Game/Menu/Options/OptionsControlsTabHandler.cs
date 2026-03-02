using Game.Settings;
using Game.Social;
using Network.Singletons;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Menu.Options {
    public class OptionsControlsTabHandler : IOptionsTabHandler {
        private Slider _sensitivitySlider;
        private TextField _sensitivityValue;
        private Button _invertYButton;
        private Button _playerTrailsButton;
        private Button _streamerModeButton;
        private Button _holdMantleButton;
        private Button _profanityFilterButton;
        private Button _autoWallRunButton;

        private readonly System.Collections.Generic.Dictionary<string, Button[]> _keybindButtons = new();

        private float _originalSensitivity;
        private bool _originalInvertY;
        private bool _originalPlayerTrails;
        private bool _originalStreamerMode;
        private bool _originalHoldMantle;
        private bool _originalProfanityFilter;
        private bool _originalAutoWallRun;

        private static readonly string[] KeybindNames = {
            "forward", "back", "left", "right", "jump", "interact", "shoot", "ads", "reload", "grapple", "primary",
            "secondary", "nextweapon", "previousweapon", "ptt"
        };

        public void FindElements(IOptionsTabContext ctx) {
            _sensitivitySlider = ctx.QOptional<Slider>("sensitivity");
            _sensitivityValue = ctx.QOptional<TextField>("sensitivity-value");
            _invertYButton = ctx.QOptional<Button>("invert-y");
            _playerTrailsButton = ctx.QOptional<Button>("player-trails");
            _streamerModeButton = ctx.QOptional<Button>("streamer-mode");
            _holdMantleButton = ctx.QOptional<Button>("hold-mantle");
            _profanityFilterButton = ctx.QOptional<Button>("profanity-filter");
            _autoWallRunButton = ctx.QOptional<Button>("auto-wall-run");
            foreach(var name in KeybindNames) {
                var b0 = ctx.QOptional<Button>($"keybind-{name}-0");
                var b1 = ctx.QOptional<Button>($"keybind-{name}-1");
                if(b0 != null && b1 != null) _keybindButtons[name] = new[] { b0, b1 };
            }
        }

        public void SetupCallbacks(IOptionsTabContext ctx) {
            if(_sensitivitySlider != null) {
                EventCallback<ChangeEvent<float>> handler = evt => {
                    if(_sensitivityValue != null) _sensitivityValue.value = evt.newValue.ToString("F2");
                };
                _sensitivitySlider.RegisterValueChangedCallback(handler);
                ctx.RegisterCleanup(() => _sensitivitySlider.UnregisterCallback(handler));
            }
            _sensitivityValue?.AddToClassList("sensitivity-input");
            ctx.RegisterCleanup(OptionsSettingsHelpers.SetupVolumeInputField(_sensitivitySlider, _sensitivityValue, 0.01f, 0.5f, false));

            RegisterCheckboxButtons(ctx, _invertYButton, _playerTrailsButton, _streamerModeButton,
                _holdMantleButton, _profanityFilterButton, _autoWallRunButton);
        }

        public void SetupKeybinds(IOptionsTabContext ctx) {
            if(KeybindManager.Instance == null) {
                Debug.LogWarning("[OptionsControlsTabHandler] KeybindManager not found, keybinds will not work");
                return;
            }
            foreach(var keybindName in KeybindNames) {
                if(!_keybindButtons.TryGetValue(keybindName, out var buttons)) continue;
                for(var i = 0; i < 2; i++) {
                    var index = i;
                    var button = buttons[i];
                    EventCallback<ClickEvent> handler = _ => OnKeybindButtonClicked(keybindName, index);
                    button.RegisterCallback(handler);
                    ctx.RegisterCleanup(() => button.UnregisterCallback(handler));
                }
            }
            LoadKeybindDisplayStrings();
        }

        private void OnKeybindButtonClicked(string keybindName, int bindingIndex) {
            if(KeybindManager.Instance == null) return;
            if(!_keybindButtons.TryGetValue(keybindName, out var buttons)) return;
            var button = buttons[bindingIndex];
            button.text = "Press key...";
            button.SetEnabled(false);
            KeybindManager.Instance.StartRebinding(keybindName, bindingIndex, displayString => {
                button.SetEnabled(true);
                if(!string.IsNullOrEmpty(displayString)) button.text = displayString;
                else LoadKeybindDisplayString(keybindName, bindingIndex);
            });
        }

        public void LoadKeybindDisplayStrings() {
            if(KeybindManager.Instance == null) return;
            foreach(var (keybindName, buttons) in _keybindButtons) {
                for(var i = 0; i < buttons.Length; i++) {
                    if(buttons[i] != null) buttons[i].SetEnabled(true);
                    LoadKeybindDisplayString(keybindName, i);
                }
            }
        }

        private void LoadKeybindDisplayString(string keybindName, int bindingIndex) {
            if(KeybindManager.Instance == null || !_keybindButtons.TryGetValue(keybindName, out var keybindButton)) return;
            var button = keybindButton[bindingIndex];
            if(button == null) return;
            button.text = KeybindManager.GetBindingDisplayString(keybindName, bindingIndex);
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

        public void Load(SettingsData data) {
            var sensitivityValue = data.controls != null ? data.controls.sensitivity : 0.1f;
            if(_sensitivitySlider != null) _sensitivitySlider.value = sensitivityValue;
            if(_invertYButton != null) OptionsSettingsHelpers.SetCheckboxValue(_invertYButton, data.controls is { invertY: true });
            if(_playerTrailsButton != null)
                OptionsSettingsHelpers.SetCheckboxValue(_playerTrailsButton, data.controls == null || data.controls.playerTrails);
            if(_streamerModeButton != null)
                OptionsSettingsHelpers.SetCheckboxValue(_streamerModeButton, data.social is { streamerModeEnabled: true });
            if(_holdMantleButton != null)
                OptionsSettingsHelpers.SetCheckboxValue(_holdMantleButton, data.controls == null || data.controls.holdMantle);
            if(_profanityFilterButton != null)
                OptionsSettingsHelpers.SetCheckboxValue(_profanityFilterButton, SocialSettings.ProfanityFilterEnabled);
            if(_autoWallRunButton != null)
                OptionsSettingsHelpers.SetCheckboxValue(_autoWallRunButton, data.controls is { autoWallRun: true });
        }

        public void Save(SettingsData data) {
            if(_sensitivitySlider != null && data.controls != null) data.controls.sensitivity = _sensitivitySlider.value;
            if(data.controls != null) data.controls.invertY = OptionsSettingsHelpers.GetCheckboxValue(_invertYButton);
            if(data.controls != null) data.controls.playerTrails = OptionsSettingsHelpers.GetCheckboxValue(_playerTrailsButton);
            if(data.social != null) data.social.streamerModeEnabled = OptionsSettingsHelpers.GetCheckboxValue(_streamerModeButton);
            if(data.controls != null) data.controls.holdMantle = OptionsSettingsHelpers.GetCheckboxValue(_holdMantleButton);
            if(data.controls != null) data.controls.autoWallRun = OptionsSettingsHelpers.GetCheckboxValue(_autoWallRunButton);
            SocialSettings.ProfanityFilterEnabled = OptionsSettingsHelpers.GetCheckboxValue(_profanityFilterButton);
        }

        public void StoreOriginal() {
            _originalSensitivity = _sensitivitySlider?.value ?? 0.1f;
            _originalInvertY = OptionsSettingsHelpers.GetCheckboxValue(_invertYButton);
            _originalPlayerTrails = OptionsSettingsHelpers.GetCheckboxValue(_playerTrailsButton);
            _originalStreamerMode = OptionsSettingsHelpers.GetCheckboxValue(_streamerModeButton);
            _originalHoldMantle = OptionsSettingsHelpers.GetCheckboxValue(_holdMantleButton);
            _originalProfanityFilter = SocialSettings.ProfanityFilterEnabled;
            _originalAutoWallRun = OptionsSettingsHelpers.GetCheckboxValue(_autoWallRunButton);
        }

        public bool HasUnsavedChanges() {
            var hasKeybind = KeybindManager.Instance != null && KeybindManager.Instance.HasPendingBindings();
            return hasKeybind ||
                   FloatChanged(_sensitivitySlider?.value, _originalSensitivity) ||
                   OptionsSettingsHelpers.GetCheckboxValue(_invertYButton) != _originalInvertY ||
                   OptionsSettingsHelpers.GetCheckboxValue(_playerTrailsButton) != _originalPlayerTrails ||
                   OptionsSettingsHelpers.GetCheckboxValue(_streamerModeButton) != _originalStreamerMode ||
                   OptionsSettingsHelpers.GetCheckboxValue(_holdMantleButton) != _originalHoldMantle ||
                   OptionsSettingsHelpers.GetCheckboxValue(_profanityFilterButton) != _originalProfanityFilter ||
                   OptionsSettingsHelpers.GetCheckboxValue(_autoWallRunButton) != _originalAutoWallRun;
        }

        public void RefreshDisplay() {
            if(_sensitivityValue != null && _sensitivitySlider != null)
                _sensitivityValue.value = _sensitivitySlider.value.ToString("F2");
        }

        public void ApplyToRuntime() { }

        public static void CancelKeybindRebinding() {
            KeybindManager.Instance.CancelActiveRebinding();
        }

        public static void CancelKeybindBindings() {
            KeybindManager.Instance.CancelBindings();
        }

        private static bool FloatChanged(float? a, float b) => a.HasValue && !Mathf.Approximately(a.Value, b);
    }
}
