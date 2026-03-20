using Game.Player.Contracts;
using Game.Settings;
using UnityEngine;

namespace Game.Player.Visual {
    internal sealed class PlayerMaterialCustomization {
        private readonly IPlayerMaterialCustomizationContext _player;

        public PlayerMaterialCustomization(IPlayerMaterialCustomizationContext player) {
            _player = player;
        }

        public void Subscribe() {
            _player.PlayerMaterialPacketIndexState.OnValueChanged -= OnMaterialPacketChanged;
            _player.PlayerMaterialPacketIndexState.OnValueChanged += OnMaterialPacketChanged;
            _player.PlayerBaseColorState.OnValueChanged -= OnMaterialCustomizationChanged;
            _player.PlayerBaseColorState.OnValueChanged += OnMaterialCustomizationChanged;
            _player.PlayerSmoothnessState.OnValueChanged -= OnMaterialCustomizationChanged;
            _player.PlayerSmoothnessState.OnValueChanged += OnMaterialCustomizationChanged;
            _player.PlayerMetallicState.OnValueChanged -= OnMaterialCustomizationChanged;
            _player.PlayerMetallicState.OnValueChanged += OnMaterialCustomizationChanged;
            _player.PlayerSpecularColorState.OnValueChanged -= OnMaterialCustomizationChanged;
            _player.PlayerSpecularColorState.OnValueChanged += OnMaterialCustomizationChanged;
            _player.PlayerHeightStrengthState.OnValueChanged -= OnMaterialCustomizationChanged;
            _player.PlayerHeightStrengthState.OnValueChanged += OnMaterialCustomizationChanged;
            _player.PlayerEmissionEnabledState.OnValueChanged -= OnMaterialCustomizationChanged;
            _player.PlayerEmissionEnabledState.OnValueChanged += OnMaterialCustomizationChanged;
            _player.PlayerEmissionColorState.OnValueChanged -= OnMaterialCustomizationChanged;
            _player.PlayerEmissionColorState.OnValueChanged += OnMaterialCustomizationChanged;
        }

        public void Unsubscribe() {
            _player.PlayerMaterialPacketIndexState.OnValueChanged -= OnMaterialPacketChanged;
            _player.PlayerBaseColorState.OnValueChanged -= OnMaterialCustomizationChanged;
            _player.PlayerSmoothnessState.OnValueChanged -= OnMaterialCustomizationChanged;
            _player.PlayerMetallicState.OnValueChanged -= OnMaterialCustomizationChanged;
            _player.PlayerSpecularColorState.OnValueChanged -= OnMaterialCustomizationChanged;
            _player.PlayerHeightStrengthState.OnValueChanged -= OnMaterialCustomizationChanged;
            _player.PlayerEmissionEnabledState.OnValueChanged -= OnMaterialCustomizationChanged;
            _player.PlayerEmissionColorState.OnValueChanged -= OnMaterialCustomizationChanged;
        }

        private void OnMaterialPacketChanged() {
            UpdateMaterialFromNetwork();
        }

        private void OnMaterialCustomizationChanged() {
            UpdateMaterialFromNetwork();
        }

        public void UpdateMaterialFromNetwork() {
            var baseColor = new Color(
                _player.PlayerBaseColorState.Value.x,
                _player.PlayerBaseColorState.Value.y,
                _player.PlayerBaseColorState.Value.z,
                _player.PlayerBaseColorState.Value.w);

            var specularColor = new Color(
                _player.PlayerSpecularColorState.Value.x,
                _player.PlayerSpecularColorState.Value.y,
                _player.PlayerSpecularColorState.Value.z,
                _player.PlayerSpecularColorState.Value.w);

            var emissionColor = new Color(
                _player.PlayerEmissionColorState.Value.x,
                _player.PlayerEmissionColorState.Value.y,
                _player.PlayerEmissionColorState.Value.z,
                _player.PlayerEmissionColorState.Value.w);

            _player.ApplyPlayerMaterialCustomization(new PlayerMaterialCustomizationRequest {
                PacketIndex = _player.PlayerMaterialPacketIndexState.Value,
                BaseColor = baseColor,
                Smoothness = _player.PlayerSmoothnessState.Value,
                Metallic = _player.PlayerMetallicState.Value,
                SpecularColor = specularColor,
                HeightStrength = Mathf.Clamp(_player.PlayerHeightStrengthState.Value,
                    _player.MinHeightStrengthValue, _player.MaxHeightStrengthValue),
                EmissionEnabled = _player.PlayerEmissionEnabledState.Value,
                EmissionColor = emissionColor
            });
        }

        public void LoadMaterialCustomizationFromPrefs() {
            var customization = GameSettings.Data.player.customization;
            _player.PlayerMaterialPacketIndexState.Value = customization.materialPacketIndex;
            _player.PlayerBaseColorState.Value = customization.baseColor;
            _player.PlayerSmoothnessState.Value = customization.smoothness;
            _player.PlayerMetallicState.Value = customization.metallic;
            _player.PlayerSpecularColorState.Value = customization.specularColor;
            _player.PlayerHeightStrengthState.Value = Mathf.Clamp(customization.heightStrength,
                _player.MinHeightStrengthValue, _player.MaxHeightStrengthValue);
            _player.PlayerEmissionEnabledState.Value = customization.emissionEnabled;
            _player.PlayerEmissionColorState.Value = customization.emissionColor;
            UpdateMaterialFromNetwork();
        }

        public void SaveMaterialCustomizationToPrefs() {
            var customization = GameSettings.Data.player.customization;
            customization.materialPacketIndex = _player.PlayerMaterialPacketIndexState.Value;
            customization.baseColor = _player.PlayerBaseColorState.Value;
            customization.smoothness = _player.PlayerSmoothnessState.Value;
            customization.metallic = _player.PlayerMetallicState.Value;
            customization.specularColor = _player.PlayerSpecularColorState.Value;
            customization.heightStrength = Mathf.Clamp(_player.PlayerHeightStrengthState.Value,
                _player.MinHeightStrengthValue, _player.MaxHeightStrengthValue);
            customization.emissionEnabled = _player.PlayerEmissionEnabledState.Value;
            customization.emissionColor = _player.PlayerEmissionColorState.Value;
            GameSettings.Save();
        }

        private void OnMaterialPacketChanged(int _, int __) {
            OnMaterialPacketChanged();
        }

        private void OnMaterialCustomizationChanged<T>(T _, T __) {
            OnMaterialCustomizationChanged();
        }
    }
}
