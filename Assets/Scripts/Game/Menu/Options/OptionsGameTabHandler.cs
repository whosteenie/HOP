using System;
using System.Collections.Generic;
using Game.Menu.Shared;
using Game.Settings;
using Game.Social;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Menu.Options {
    public class OptionsGameTabHandler : IOptionsTabHandler {
        private readonly Func<MainMenuBackgroundRandomizer> _resolveRandomizer;

        private DropdownField _mainMenuBackgroundDropdown;
        private DropdownField _grappleIndicatorDropdown;
        private DropdownField _crosshairStyleDropdown;
        private DropdownField _crosshairColorDropdown;
        private DropdownField _voiceModeDropdown;

        private string _originalMainMenuBackgroundSelection = MainMenuBackgroundRandomizer.RandomSelectionOption;
        private int _originalGrappleIndicator;
        private int _originalCrosshairStyle;
        private int _originalCrosshairColor;
        private int _originalVoiceMode;

        public OptionsGameTabHandler(Func<MainMenuBackgroundRandomizer> resolveRandomizer) {
            _resolveRandomizer = resolveRandomizer ?? (() => null);
        }

        public void FindElements(IOptionsTabContext ctx) {
            _mainMenuBackgroundDropdown = ctx.QOptional<DropdownField>("main-menu-background");
            _grappleIndicatorDropdown = ctx.QOptional<DropdownField>("grapple-indicator");
            _crosshairStyleDropdown = ctx.QOptional<DropdownField>("crosshair-style");
            _crosshairColorDropdown = ctx.QOptional<DropdownField>("crosshair-color");
            _voiceModeDropdown = ctx.QOptional<DropdownField>("voice-mode");
        }

        public void SetupCallbacks(IOptionsTabContext ctx) {
            SetupBackgroundDropdown(ctx);
            if(_grappleIndicatorDropdown != null)
                _grappleIndicatorDropdown.choices = new List<string> { "Crosshair", "Bottom", "None" };
            if(_crosshairStyleDropdown != null)
                _crosshairStyleDropdown.choices = new List<string> { "Cross", "Dot" };
            if(_crosshairColorDropdown != null)
                _crosshairColorDropdown.choices = new List<string> { "Red", "Blue", "Green", "Yellow" };
        }

        private void SetupBackgroundDropdown(IOptionsTabContext ctx) {
            if(_mainMenuBackgroundDropdown == null) return;
            RefreshBackgroundDropdownChoices(preserveCurrentSelection: false);
            EventCallback<ChangeEvent<string>> handler = evt => {
                var normalizedSelection = NormalizeBackgroundSelection(evt.newValue);
                if(!string.Equals(_mainMenuBackgroundDropdown.value, normalizedSelection, StringComparison.Ordinal))
                    _mainMenuBackgroundDropdown.SetValueWithoutNotify(normalizedSelection);
                ApplyBackgroundSelectionPreview(normalizedSelection);
            };
            _mainMenuBackgroundDropdown.RegisterValueChangedCallback(handler);
            ctx.RegisterCleanup(() => _mainMenuBackgroundDropdown.UnregisterCallback(handler));
        }

        private void RefreshBackgroundDropdownChoices(bool preserveCurrentSelection) {
            if(_mainMenuBackgroundDropdown == null) return;
            var previousSelection = preserveCurrentSelection ? _mainMenuBackgroundDropdown.value : null;
            var choices = new List<string> { MainMenuBackgroundRandomizer.RandomSelectionOption };
            var randomizer = _resolveRandomizer();
            if(randomizer != null) {
                foreach(var backgroundName in randomizer.GetAvailableSelectionNames()) {
                    if(string.IsNullOrWhiteSpace(backgroundName) || choices.Contains(backgroundName)) continue;
                    choices.Add(backgroundName);
                }
            } else {
                var persistedSelection = NormalizeBackgroundSelection(GameSettings.Data.video?.mainMenuBackgroundSelection);
                if(!MainMenuBackgroundRandomizer.IsRandomSelection(persistedSelection) && !choices.Contains(persistedSelection))
                    choices.Add(persistedSelection);
                if(!string.IsNullOrWhiteSpace(previousSelection) &&
                   !MainMenuBackgroundRandomizer.IsRandomSelection(previousSelection) &&
                   !choices.Contains(previousSelection))
                    choices.Add(previousSelection);
            }
            _mainMenuBackgroundDropdown.choices = choices;
            var selectionToSet = previousSelection ?? GameSettings.Data.video?.mainMenuBackgroundSelection;
            selectionToSet = NormalizeBackgroundSelection(selectionToSet);
            if(!choices.Contains(selectionToSet)) selectionToSet = MainMenuBackgroundRandomizer.RandomSelectionOption;
            _mainMenuBackgroundDropdown.SetValueWithoutNotify(selectionToSet);
            _mainMenuBackgroundDropdown.SetEnabled(randomizer != null && choices.Count > 1);
        }

        private static string NormalizeBackgroundSelection(string selection) =>
            MainMenuBackgroundRandomizer.IsRandomSelection(selection) ? MainMenuBackgroundRandomizer.RandomSelectionOption : selection;

        private void ApplyBackgroundSelectionPreview(string selection) {
            var randomizer = _resolveRandomizer();
            if(randomizer != null) randomizer.ApplySelection(selection);
        }

        public void Load(SettingsData data) {
            RefreshBackgroundDropdownChoices(preserveCurrentSelection: false);
            if(_mainMenuBackgroundDropdown != null) {
                var savedBackgroundSelection = data.video?.mainMenuBackgroundSelection;
                var normalizedSelection = NormalizeBackgroundSelection(savedBackgroundSelection);
                if(!_mainMenuBackgroundDropdown.choices.Contains(normalizedSelection))
                    normalizedSelection = MainMenuBackgroundRandomizer.RandomSelectionOption;
                _mainMenuBackgroundDropdown.SetValueWithoutNotify(normalizedSelection);
            }
            if(_grappleIndicatorDropdown != null) {
                var savedGrappleIndicator = data.controls != null ? data.controls.grappleIndicator : 0;
                _grappleIndicatorDropdown.index = Mathf.Clamp(savedGrappleIndicator, 0, _grappleIndicatorDropdown.choices.Count - 1);
            }
            if(_crosshairStyleDropdown != null) {
                var savedCrosshairStyle = data.controls != null ? data.controls.crosshairStyle : 0;
                _crosshairStyleDropdown.index = Mathf.Clamp(savedCrosshairStyle, 0, _crosshairStyleDropdown.choices.Count - 1);
            }
            if(_crosshairColorDropdown != null) {
                var savedCrosshairColor = data.controls != null ? data.controls.crosshairColor : 0;
                _crosshairColorDropdown.index = Mathf.Clamp(savedCrosshairColor, 0, _crosshairColorDropdown.choices.Count - 1);
            }

            if(_voiceModeDropdown == null) return;
            _voiceModeDropdown.choices = new List<string>(Enum.GetNames(typeof(VoiceInputMode)));
            _voiceModeDropdown.index = (int)SocialSettings.InputMode;
        }

        public void Save(SettingsData data) {
            if(data.video != null && _mainMenuBackgroundDropdown != null)
                data.video.mainMenuBackgroundSelection = NormalizeBackgroundSelection(_mainMenuBackgroundDropdown.value);
            if(_grappleIndicatorDropdown != null && data.controls != null) data.controls.grappleIndicator = _grappleIndicatorDropdown.index;
            if(_crosshairStyleDropdown != null && data.controls != null) data.controls.crosshairStyle = _crosshairStyleDropdown.index;
            if(_crosshairColorDropdown != null && data.controls != null) data.controls.crosshairColor = _crosshairColorDropdown.index;
            if(_voiceModeDropdown != null) SocialSettings.InputMode = (VoiceInputMode)_voiceModeDropdown.index;
        }

        public void StoreOriginal() {
            _originalMainMenuBackgroundSelection = _mainMenuBackgroundDropdown != null
                ? NormalizeBackgroundSelection(_mainMenuBackgroundDropdown.value)
                : MainMenuBackgroundRandomizer.RandomSelectionOption;
            _originalGrappleIndicator = _grappleIndicatorDropdown?.index ?? 0;
            _originalCrosshairStyle = _crosshairStyleDropdown?.index ?? 0;
            _originalCrosshairColor = _crosshairColorDropdown?.index ?? 0;
            _originalVoiceMode = (int)SocialSettings.InputMode;
        }

        public bool HasUnsavedChanges() {
            var mainBgChanged = _mainMenuBackgroundDropdown != null &&
                !string.Equals(_mainMenuBackgroundDropdown.value, _originalMainMenuBackgroundSelection, StringComparison.Ordinal);
            return mainBgChanged ||
                   IndexChanged(_grappleIndicatorDropdown, _originalGrappleIndicator) ||
                   IndexChanged(_crosshairStyleDropdown, _originalCrosshairStyle) ||
                   IndexChanged(_crosshairColorDropdown, _originalCrosshairColor) ||
                   IndexChanged(_voiceModeDropdown, _originalVoiceMode);
        }

        public void RefreshDisplay() { }

        public void ApplyToRuntime() { }

        public void RefreshBackgroundChoices(bool preserveCurrentSelection = true) {
            RefreshBackgroundDropdownChoices(preserveCurrentSelection);
        }

        /// <summary>
        /// Applies the preview for the current dropdown selection.
        /// When called after Apply with a non-Random selection, only applies if not Random (matches original behavior).
        /// When called after Load (e.g. discard), always applies to restore display.
        /// </summary>
        public void ApplyCurrentBackgroundPreview(bool onlyIfNotRandom = false) {
            if(_mainMenuBackgroundDropdown == null) return;
            var selection = NormalizeBackgroundSelection(_mainMenuBackgroundDropdown.value);
            if(onlyIfNotRandom && MainMenuBackgroundRandomizer.IsRandomSelection(selection)) return;
            ApplyBackgroundSelectionPreview(selection);
        }

        /// <summary>
        /// Returns true if the main menu background selection has changed from the stored original.
        /// </summary>
        public bool HasBackgroundChange() {
            return _mainMenuBackgroundDropdown != null &&
                   !string.Equals(NormalizeBackgroundSelection(_mainMenuBackgroundDropdown.value), _originalMainMenuBackgroundSelection, StringComparison.Ordinal);
        }

        private static bool IndexChanged(DropdownField d, int orig) => d != null && d.index != orig;
    }
}
