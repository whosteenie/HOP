using System;
using System.Collections.Generic;
using Game.Settings;
using Game.Social;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.UIElements;
using UnityUtils;

namespace Game.Menu.Options {
    public class OptionsAudioTabHandler : IOptionsTabHandler {
        private readonly AudioMixer _audioMixer;

        private Slider _masterVolumeSlider;
        private Slider _musicVolumeSlider;
        private Slider _sfxVolumeSlider;
        private Slider _voiceVolumeSlider;
        private Slider _voiceInputVolumeSlider;
        private TextField _masterVolumeValue;
        private TextField _musicVolumeValue;
        private TextField _sfxVolumeValue;
        private TextField _voiceVolumeValue;
        private TextField _voiceInputVolumeValue;
        private DropdownField _voiceDeviceDropdown;

        private float _originalMasterVolume;
        private float _originalMusicVolume;
        private float _originalSfxVolume;
        private float _originalVoiceVolume;
        private float _originalVoiceInputVolume;
        private string _originalVoiceDevice = "";

        public OptionsAudioTabHandler(AudioMixer audioMixer) {
            _audioMixer = audioMixer;
        }

        public void FindElements(IOptionsTabContext ctx) {
            _masterVolumeSlider = ctx.QOptional<Slider>("master-volume");
            _musicVolumeSlider = ctx.QOptional<Slider>("music-volume");
            _sfxVolumeSlider = ctx.QOptional<Slider>("sfx-volume");
            _masterVolumeValue = ctx.QOptional<TextField>("master-volume-value");
            _musicVolumeValue = ctx.QOptional<TextField>("music-volume-value");
            _sfxVolumeValue = ctx.QOptional<TextField>("sfx-volume-value");
            _voiceVolumeSlider = ctx.QOptional<Slider>("voice-volume");
            _voiceVolumeValue = ctx.QOptional<TextField>("voice-volume-value");
            _voiceInputVolumeSlider = ctx.QOptional<Slider>("voice-input-volume");
            _voiceInputVolumeValue = ctx.QOptional<TextField>("voice-input-volume-value");
            _voiceDeviceDropdown = ctx.QOptional<DropdownField>("voice-device");
        }

        public void SetupCallbacks(IOptionsTabContext ctx) {
            ctx.RegisterCleanup(OptionsSettingsHelpers.BindSliderToPercentTextField(_masterVolumeSlider, _masterVolumeValue));
            ctx.RegisterCleanup(OptionsSettingsHelpers.BindSliderToPercentTextField(_musicVolumeSlider, _musicVolumeValue));
            ctx.RegisterCleanup(OptionsSettingsHelpers.BindSliderToPercentTextField(_sfxVolumeSlider, _sfxVolumeValue));
            ctx.RegisterCleanup(OptionsSettingsHelpers.BindSliderToPercentTextField(_voiceVolumeSlider, _voiceVolumeValue));
            ctx.RegisterCleanup(OptionsSettingsHelpers.BindSliderToPercentTextField(_voiceInputVolumeSlider, _voiceInputVolumeValue));

            RefreshVoiceDeviceDropdownChoices();
            RefreshVoiceDeviceDropdownChoicesDeferred(ctx.Root);

            ctx.RegisterCleanup(OptionsSettingsHelpers.SetupVolumeInputField(_masterVolumeSlider, _masterVolumeValue, 0f, 1f, true));
            ctx.RegisterCleanup(OptionsSettingsHelpers.SetupVolumeInputField(_musicVolumeSlider, _musicVolumeValue, 0f, 1f, true));
            ctx.RegisterCleanup(OptionsSettingsHelpers.SetupVolumeInputField(_sfxVolumeSlider, _sfxVolumeValue, 0f, 1f, true));
            ctx.RegisterCleanup(OptionsSettingsHelpers.SetupVolumeInputField(_voiceVolumeSlider, _voiceVolumeValue, 0f, 1f, true));
            ctx.RegisterCleanup(OptionsSettingsHelpers.SetupVolumeInputField(_voiceInputVolumeSlider, _voiceInputVolumeValue, 0f, 1f, true));
        }

        public void Load(SettingsData data) {
            var masterDb = data.audio != null ? data.audio.masterVolumeDb : 0f;
            var musicDb = data.audio != null ? data.audio.musicVolumeDb : -20f;
            var sfxDb = data.audio != null ? data.audio.sfxVolumeDb : -8f;
            if(_masterVolumeSlider != null) _masterVolumeSlider.value = OptionsSettingsHelpers.DbToLinear(masterDb);
            if(_musicVolumeSlider != null) _musicVolumeSlider.value = OptionsSettingsHelpers.DbToLinear(musicDb);
            if(_sfxVolumeSlider != null) _sfxVolumeSlider.value = OptionsSettingsHelpers.DbToLinear(sfxDb);
            if(_voiceVolumeSlider != null) _voiceVolumeSlider.value = SocialSettings.VoiceVolume;
            if(_voiceVolumeValue != null) _voiceVolumeValue.value = Mathf.RoundToInt(SocialSettings.VoiceVolume * 100) + "%";
            if(_voiceInputVolumeSlider != null) _voiceInputVolumeSlider.value = SocialSettings.VoiceInputVolume;
            if(_voiceInputVolumeValue != null) _voiceInputVolumeValue.value = Mathf.RoundToInt(SocialSettings.VoiceInputVolume * 100) + "%";
            RefreshVoiceDeviceDropdownChoices();
        }

        public void Save(SettingsData data) {
            if(_masterVolumeSlider != null && data.audio != null)
                data.audio.masterVolumeDb = OptionsSettingsHelpers.LinearToDb(_masterVolumeSlider.value);
            if(_musicVolumeSlider != null && data.audio != null)
                data.audio.musicVolumeDb = OptionsSettingsHelpers.LinearToDb(_musicVolumeSlider.value);
            if(_sfxVolumeSlider != null && data.audio != null)
                data.audio.sfxVolumeDb = OptionsSettingsHelpers.LinearToDb(_sfxVolumeSlider.value);
            if(_voiceVolumeSlider != null) SocialSettings.VoiceVolume = _voiceVolumeSlider.value;
            if(_voiceInputVolumeSlider != null) SocialSettings.VoiceInputVolume = _voiceInputVolumeSlider.value;
            if(_voiceDeviceDropdown == null) return;
            SocialSettings.InputDevice = _voiceDeviceDropdown.value;
            VoiceManager.Instance.SetActiveMicAsync(_voiceDeviceDropdown.value).Forget();
        }

        public void StoreOriginal() {
            _originalMasterVolume = _masterVolumeSlider?.value ?? 0f;
            _originalMusicVolume = _musicVolumeSlider?.value ?? 0f;
            _originalSfxVolume = _sfxVolumeSlider?.value ?? 0f;
            _originalVoiceVolume = SocialSettings.VoiceVolume;
            _originalVoiceInputVolume = SocialSettings.VoiceInputVolume;
            _originalVoiceDevice = _voiceDeviceDropdown?.value ?? "";
        }

        public bool HasUnsavedChanges() {
            return FloatChanged(_masterVolumeSlider?.value, _originalMasterVolume) ||
                   FloatChanged(_musicVolumeSlider?.value, _originalMusicVolume) ||
                   FloatChanged(_sfxVolumeSlider?.value, _originalSfxVolume) ||
                   FloatChanged(_voiceVolumeSlider?.value, _originalVoiceVolume) ||
                   FloatChanged(_voiceInputVolumeSlider?.value, _originalVoiceInputVolume) ||
                   (_voiceDeviceDropdown != null && !string.Equals(_voiceDeviceDropdown.value, _originalVoiceDevice ?? "", StringComparison.Ordinal));
        }

        public void RefreshDisplay() {
            if(_masterVolumeValue != null && _masterVolumeSlider != null)
                _masterVolumeValue.value = Mathf.RoundToInt(_masterVolumeSlider.value * 100) + "%";
            if(_musicVolumeValue != null && _musicVolumeSlider != null)
                _musicVolumeValue.value = Mathf.RoundToInt(_musicVolumeSlider.value * 100) + "%";
            if(_sfxVolumeValue != null && _sfxVolumeSlider != null)
                _sfxVolumeValue.value = Mathf.RoundToInt(_sfxVolumeSlider.value * 100) + "%";
            if(_voiceVolumeValue != null && _voiceVolumeSlider != null)
                _voiceVolumeValue.value = Mathf.RoundToInt(_voiceVolumeSlider.value * 100) + "%";
            if(_voiceInputVolumeValue != null && _voiceInputVolumeSlider != null)
                _voiceInputVolumeValue.value = Mathf.RoundToInt(_voiceInputVolumeSlider.value * 100) + "%";
        }

        public void ApplyToRuntime() {
            if(_audioMixer == null) return;
            if(_masterVolumeSlider != null)
                _audioMixer.SetFloat("masterVolume", OptionsSettingsHelpers.LinearToDb(_masterVolumeSlider.value));
            if(_musicVolumeSlider != null)
                _audioMixer.SetFloat("musicVolume", OptionsSettingsHelpers.LinearToDb(_musicVolumeSlider.value));
            if(_sfxVolumeSlider != null)
                _audioMixer.SetFloat("soundFXVolume", OptionsSettingsHelpers.LinearToDb(_sfxVolumeSlider.value));
        }

        public void RefreshVoiceDeviceDropdownChoicesForPanel(VisualElement root) {
            RefreshVoiceDeviceDropdownChoices();
            RefreshVoiceDeviceDropdownChoicesDeferred(root);
        }

        private void RefreshVoiceDeviceDropdownChoices(string preferredDevice = null) {
            if(_voiceDeviceDropdown == null) return;
            var devices = VoiceManager.Instance != null
                ? VoiceManager.GetAvailableInputDevices()
                : new List<string>();
            if(devices == null || devices.Count == 0) devices = new List<string> { "Default" };
            _voiceDeviceDropdown.choices = devices;
            var targetDevice = string.IsNullOrWhiteSpace(preferredDevice) ? SocialSettings.InputDevice : preferredDevice;
            var selectedIndex = string.IsNullOrWhiteSpace(targetDevice) ? -1 : devices.IndexOf(targetDevice);
            if(selectedIndex < 0) selectedIndex = 0;
            _voiceDeviceDropdown.index = selectedIndex;
        }

        private void RefreshVoiceDeviceDropdownChoicesDeferred(VisualElement root) {
            if(root == null) return;
            root.schedule.Execute(() => RefreshVoiceDeviceDropdownChoices()).StartingIn(200);
            root.schedule.Execute(() => RefreshVoiceDeviceDropdownChoices()).StartingIn(700);
            root.schedule.Execute(() => RefreshVoiceDeviceDropdownChoices()).StartingIn(1500);
        }

        private static bool FloatChanged(float? a, float b) => a.HasValue && !Mathf.Approximately(a.Value, b);
    }
}
