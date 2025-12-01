using UnityEngine;

namespace Game.Player {
    /// <summary>
    /// ScriptableObject that defines a material packet containing texture maps and settings.
    /// Players can select a packet and then customize base color, smoothness, and metallic values.
    /// </summary>
    [CreateAssetMenu(fileName = "NewMaterialPacket", menuName = "Player/Material Packet")]
    public class PlayerMaterialPacket : ScriptableObject {
        [Header("Display")]
        [Tooltip("Display name for this material packet")]
        public string packetName = "New Material";

        [Header("Textures")]
        [Tooltip("Base color/albedo texture (Base Map in URP/Lit)")]
        public Texture2D albedoTexture;

        [Tooltip("Normal map for surface detail")]
        public Texture2D normalMap;

        [Tooltip("Normal map strength (1 = default). Inspector-only; not exposed to players.")]
        [Range(0f, 5f)]
        public float normalMapStrength = 1f;

        [Tooltip("Height map for parallax/displacement")]
        public Texture2D heightMap;

        [Tooltip("Ambient occlusion map")]
        public Texture2D occlusionMap;

        [Tooltip("Metallic map (if using metallic workflow)")]
        public Texture2D metallicMap;

        [Tooltip("Emission map for glowing areas. Enables emission controls when assigned.")]
        public Texture2D emissionMap;

        [Header("Tiling & Offset")]
        [Tooltip("Texture tiling to ensure proper scaling on player model")]
        public Vector2 tiling = Vector2.one;

        [Tooltip("Texture offset")]
        public Vector2 offset = Vector2.zero;

        [Header("Default Values")]
        [Tooltip("Use metallic workflow (true) or specular workflow (false).\n" +
                 "Metallic: Has Metallic + Smoothness sliders (for metals, plastics).\n" +
                 "Specular: Has Smoothness + Specular Color (for non-metals like skin, fabric).")]
        public bool useMetallicWorkflow = true;

        [Tooltip("Suggested smoothness value (0-1). Controls how reflective/shiny the surface is.")]
        [Range(0f, 1f)]
        public float defaultSmoothness = 0.5f;

        [Tooltip("Suggested metallic value (0-1). Only used if useMetallicWorkflow is true.\n" +
                 "0 = non-metal (dielectric), 1 = metal (conductor).")]
        [Range(0f, 1f)]
        public float defaultMetallic;

        [Tooltip("Suggested specular color. Only used if useMetallicWorkflow is false.\n" +
                 "Controls the color of specular highlights for non-metallic surfaces.")]
        public Color defaultSpecularColor = new Color(0.2f, 0.2f, 0.2f, 1f);

        [Header("Height Map Settings")]
        [Tooltip("Height map strength/scale (0-1). Controls parallax displacement intensity.\n" +
                 "0 = no effect, 1 = maximum displacement. Only used if heightMap is assigned.")]
        [Range(0f, 1f)]
        public float heightMapStrength = 0.02f;

        [Header("Emission Settings")]
        [Tooltip("Whether emission should be enabled by default when this packet is selected.")]
        public bool defaultEmissionEnabled;

        [Tooltip("Default emission color used when emission is enabled.")]
        public Color defaultEmissionColor = new Color(0f, 0f, 0f, 1f);

        /// <summary>
        /// Returns true if this is a valid packet (has at least an albedo texture, or is the "None" packet).
        /// </summary>
        public bool IsValid => albedoTexture != null || IsNonePacket;

        /// <summary>
        /// Returns true if this is the special "None" packet (no textures, just base color customization).
        /// </summary>
        private bool IsNonePacket => string.IsNullOrEmpty(packetName) || packetName == "None";
    }
}

