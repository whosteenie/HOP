using Unity.Netcode;
using UnityEngine;

namespace Game.Player.Contracts {
    public interface IPlayerMaterialCustomizationContext {
        NetworkVariable<int> PlayerMaterialPacketIndexState { get; }
        NetworkVariable<Vector4> PlayerBaseColorState { get; }
        NetworkVariable<float> PlayerSmoothnessState { get; }
        NetworkVariable<float> PlayerMetallicState { get; }
        NetworkVariable<Vector4> PlayerSpecularColorState { get; }
        NetworkVariable<float> PlayerHeightStrengthState { get; }
        NetworkVariable<bool> PlayerEmissionEnabledState { get; }
        NetworkVariable<Vector4> PlayerEmissionColorState { get; }
        float MinHeightStrengthValue { get; }
        float MaxHeightStrengthValue { get; }

        void ApplyPlayerMaterialCustomization(int packetIndex, Color baseColor, float smoothness, float metallic,
            Color specularColor, float heightStrength, bool emissionEnabled, Color emissionColor);
    }
}
