using Unity.Netcode;
using UnityEngine;

namespace Game.Player.Contracts {
    public struct PlayerMaterialCustomizationRequest {
        public int PacketIndex { get; set; }
        public Color BaseColor { get; set; }
        public float Smoothness { get; set; }
        public float Metallic { get; set; }
        public Color SpecularColor { get; set; }
        public float HeightStrength { get; set; }
        public bool EmissionEnabled { get; set; }
        public Color EmissionColor { get; set; }
    }

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

        void ApplyPlayerMaterialCustomization(in PlayerMaterialCustomizationRequest request);
    }
}
