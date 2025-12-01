using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Player {
    /// <summary>
    /// Static utility for generating URP/Lit materials from material packets and customization values.
    /// Handles material caching to avoid creating duplicate materials.
    /// </summary>
    public static class PlayerMaterialGenerator {
        private static readonly System.Collections.Generic.Dictionary<string, Material> MaterialCache = new();

        // URP/Lit shader property IDs
        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int NormalMapId = Shader.PropertyToID("_BumpMap");
        private static readonly int NormalScaleId = Shader.PropertyToID("_BumpScale");
        private static readonly int HeightMapId = Shader.PropertyToID("_ParallaxMap");
        private static readonly int ParallaxId = Shader.PropertyToID("_Parallax");
        private static readonly int OcclusionMapId = Shader.PropertyToID("_OcclusionMap");
        private static readonly int MetallicMapId = Shader.PropertyToID("_MetallicGlossMap");
        private static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
        private static readonly int MetallicId = Shader.PropertyToID("_Metallic");
        private static readonly int SpecularColorId = Shader.PropertyToID("_SpecularColor");
        private static readonly int WorkflowModeId = Shader.PropertyToID("_WorkflowMode");
        private static readonly int EmissionMapId = Shader.PropertyToID("_EmissionMap");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private static readonly int EmissionIntensityId = Shader.PropertyToID("_EmissionIntensity");

        /// <summary>
        /// Generates a URP/Lit material from a packet and customization values.
        /// Materials are cached by their parameters to avoid duplicates.
        /// </summary>
        /// <param name="packet">The material packet containing textures and settings</param>
        /// <param name="baseColor">Base color tint (applied to base map or used directly if no map)</param>
        /// <param name="smoothness">Smoothness value (0-1), controls reflectivity</param>
        /// <param name="metallic">Metallic value (0-1), only used if packet uses metallic workflow</param>
        /// <param name="specularColor">Specular color, only used if packet uses specular workflow</param>
        /// <param name="heightStrength">Height map strength override (0-1), uses packet default if null</param>
        /// <param name="emissionEnabled"></param>
        /// <param name="emissionColor"></param>
        public static Material GenerateMaterial(PlayerMaterialPacket packet, Color baseColor, float smoothness, 
            float metallic = 0f, Color? specularColor = null, float? heightStrength = null,
            bool emissionEnabled = false, Color? emissionColor = null) {
            if(packet == null) {
                Debug.LogWarning("[PlayerMaterialGenerator] Packet is null, using default material.");
                return CreateDefaultMaterial(baseColor, smoothness, metallic, emissionEnabled, emissionColor);
            }

            // Use packet defaults if not provided
            var finalSpecularColor = specularColor ?? packet.defaultSpecularColor;
            var finalHeightStrength = heightStrength ?? packet.heightMapStrength;
            var finalEmissionColor = emissionColor ?? packet.defaultEmissionColor;
            var supportsEmission = packet.emissionMap != null;
            var finalEmissionEnabled = emissionEnabled && (supportsEmission || finalEmissionColor.maxColorComponent > 0.001f);

            // Create cache key
            var cacheKey = GetCacheKey(packet, baseColor, smoothness, metallic, finalSpecularColor, finalHeightStrength, finalEmissionEnabled, finalEmissionColor);

            // Check cache
            if(MaterialCache.TryGetValue(cacheKey, out var cachedMaterial) && cachedMaterial != null) {
                return cachedMaterial;
            }

            // Create new material
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if(shader == null) {
                Debug.LogError("[PlayerMaterialGenerator] URP/Lit shader not found! Falling back to default material.");
                return CreateDefaultMaterial(baseColor, smoothness, metallic, finalEmissionEnabled, finalEmissionColor);
            }

            var material = new Material(shader) {
                name = $"PlayerMaterial_{packet.packetName}_{baseColor}_{smoothness}_{metallic}"
            };

            // Set workflow mode
            material.SetFloat(WorkflowModeId, packet.useMetallicWorkflow ? 0f : 1f); // 0 = Metallic, 1 = Specular

            // Base Map and Color
            if(packet.albedoTexture != null) {
                material.SetTexture(BaseMapId, packet.albedoTexture);
                // Apply tiling and offset
                material.SetTextureScale(BaseMapId, packet.tiling);
                material.SetTextureOffset(BaseMapId, packet.offset);
            }

            // Base Color (tints the base map, or is the color if no map)
            material.SetColor(BaseColorId, baseColor);

            // Normal Map
            if(packet.normalMap != null) {
                material.SetTexture(NormalMapId, packet.normalMap);
                material.SetFloat(NormalScaleId, 1f); // Default normal strength
                material.SetTextureScale(NormalMapId, packet.tiling);
                material.SetTextureOffset(NormalMapId, packet.offset);
            }

            // Height Map
            if(packet.heightMap != null) {
                material.SetTexture(HeightMapId, packet.heightMap);
                material.SetFloat(ParallaxId, finalHeightStrength); // Height map strength/scale
                material.SetTextureScale(HeightMapId, packet.tiling);
                material.SetTextureOffset(HeightMapId, packet.offset);
                material.EnableKeyword("_PARALLAXMAP");
            } else {
                material.DisableKeyword("_PARALLAXMAP");
            }

            // Occlusion Map
            if(packet.occlusionMap != null) {
                material.SetTexture(OcclusionMapId, packet.occlusionMap);
                material.SetTextureScale(OcclusionMapId, packet.tiling);
                material.SetTextureOffset(OcclusionMapId, packet.offset);
            }

            // Metallic Map (only if using metallic workflow)
            if(packet.useMetallicWorkflow && packet.metallicMap != null) {
                material.SetTexture(MetallicMapId, packet.metallicMap);
                material.SetTextureScale(MetallicMapId, packet.tiling);
                material.SetTextureOffset(MetallicMapId, packet.offset);
            }

            // Smoothness (used in both workflows)
            material.SetFloat(SmoothnessId, smoothness);

            // Workflow-specific properties
            if(packet.useMetallicWorkflow) {
                // Metallic workflow: Set metallic value
                material.SetFloat(MetallicId, metallic);
            } else {
                // Specular workflow: Set specular color (no metallic slider)
                material.SetColor(SpecularColorId, finalSpecularColor);
            }

            // Emission
            if(supportsEmission && packet.emissionMap != null) {
                material.SetTexture(EmissionMapId, packet.emissionMap);
                material.SetTextureScale(EmissionMapId, packet.tiling);
                material.SetTextureOffset(EmissionMapId, packet.offset);
            }

            if(finalEmissionEnabled) {
                // CRITICAL: Set emission color BEFORE enabling keyword for build compatibility
                // In builds, Unity's shader variant stripping can cause issues if order is wrong
                material.SetColor(EmissionColorId, finalEmissionColor);
                
                // Enable emission keyword AFTER setting color
                material.EnableKeyword("_EMISSION");
                
                // Set global illumination flags for proper emission rendering
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                
                // BRUTE FORCE: Also try setting emission intensity explicitly (some shader variants need this)
                // This ensures emission works even if variant stripping is aggressive
                if(material.HasProperty(EmissionIntensityId)) {
                    // Calculate intensity from color (max component)
                    var intensity = finalEmissionColor.maxColorComponent;
                    material.SetFloat(EmissionIntensityId, intensity > 0.001f ? intensity : 1f);
                }
            } else {
                material.DisableKeyword("_EMISSION");
                material.SetColor(EmissionColorId, Color.black);
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            }

            // Set render queue and other defaults
            material.renderQueue = (int)RenderQueue.Geometry;

            // Cache the material
            MaterialCache[cacheKey] = material;

            return material;
        }

        /// <summary>
        /// Creates a default URP/Lit material with just base color, smoothness, and metallic (no textures).
        /// </summary>
        private static Material CreateDefaultMaterial(Color baseColor, float smoothness, float metallic, bool emissionEnabled = false, Color? emissionColor = null) {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if(shader == null) {
                Debug.LogError("[PlayerMaterialGenerator] URP/Lit shader not found! Creating fallback material.");
                return new Material(Shader.Find("Standard"));
            }

            var material = new Material(shader) {
                name = $"PlayerMaterial_Default_{baseColor}_{smoothness}_{metallic}"
            };

            material.SetColor(BaseColorId, baseColor);
            material.SetFloat(SmoothnessId, smoothness);
            material.SetFloat(MetallicId, metallic);
            material.SetFloat(WorkflowModeId, 0f); // Metallic workflow

            if(emissionEnabled && emissionColor.HasValue) {
                // CRITICAL: Set emission color BEFORE enabling keyword for build compatibility
                material.SetColor(EmissionColorId, emissionColor.Value);
                
                // Enable emission keyword AFTER setting color
                material.EnableKeyword("_EMISSION");
                
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                
                // BRUTE FORCE: Also try setting emission intensity explicitly
                if(material.HasProperty(EmissionIntensityId)) {
                    var intensity = emissionColor.Value.maxColorComponent;
                    material.SetFloat(EmissionIntensityId, intensity > 0.001f ? intensity : 1f);
                }
            } else {
                material.DisableKeyword("_EMISSION");
                material.SetColor(EmissionColorId, Color.black);
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            }

            return material;
        }

        /// <summary>
        /// Generates a cache key from material parameters.
        /// </summary>
        private static string GetCacheKey(PlayerMaterialPacket packet, Color baseColor, float smoothness, 
            float metallic, Color specularColor, float heightStrength, bool emissionEnabled, Color emissionColor) {
            var packetId = packet != null ? packet.GetInstanceID().ToString() : "null";
            var colorKey = $"{baseColor.r:F3}_{baseColor.g:F3}_{baseColor.b:F3}_{baseColor.a:F3}";
            var specularKey = $"{specularColor.r:F3}_{specularColor.g:F3}_{specularColor.b:F3}_{specularColor.a:F3}";
            var emissionKey = $"{(emissionEnabled ? 1 : 0)}_{emissionColor.r:F3}_{emissionColor.g:F3}_{emissionColor.b:F3}_{emissionColor.a:F3}";
            return $"{packetId}_{colorKey}_{smoothness:F3}_{metallic:F3}_{specularKey}_{heightStrength:F3}_{emissionKey}";
        }

        /// <summary>
        /// Clears the material cache. Call this when changing scenes or when memory is needed.
        /// </summary>
        public static void ClearCache() {
            foreach(var material in MaterialCache.Values) {
                if(material != null) {
                    Destroy(material);
                }
            }

            MaterialCache.Clear();
        }

        /// <summary>
        /// Destroys a material (handles both runtime and editor).
        /// </summary>
        private static void Destroy(Object obj) {
            if(obj == null) return;

#if UNITY_EDITOR
            if(!Application.isPlaying) {
                UnityEditor.EditorUtility.SetDirty(obj);
                Object.DestroyImmediate(obj);
                return;
            }
#endif
            Object.Destroy(obj);
        }
    }
}

