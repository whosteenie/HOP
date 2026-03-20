using System.Collections.Generic;
using Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Player.Visual {
    public struct PlayerMaterialGenerationRequest {
        public Color BaseColor { get; set; }
        public float Smoothness { get; set; }
        public float Metallic { get; set; }
        public Color? SpecularColor { get; set; }
        public float? HeightStrength { get; set; }
        public bool EmissionEnabled { get; set; }
        public Color? EmissionColor { get; set; }
    }

    /// <summary>
    /// Static utility for generating URP/Lit materials from material packets and customization values.
    /// Handles material caching to avoid creating duplicate materials.
    /// </summary>
    public static class PlayerMaterialGenerator {
        private static readonly Dictionary<string, Material> MaterialCache = new();
        private static readonly Queue<string> MaterialCacheOrder = new();
        private const int MaxCachedMaterials = 256;

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
        /// <param name="request">Generation request containing customization overrides.</param>
        public static Material GenerateMaterial(PlayerMaterialPacket packet, in PlayerMaterialGenerationRequest request) {
            if(packet == null) {
                DevLog.LogWarning("[PlayerMaterialGenerator] Packet is null, using default material.");
                return CreateDefaultMaterial(in request);
            }

            var options = ResolveGenerationOptions(packet, in request);
            var cacheKey = GetCacheKey(packet, request.BaseColor, request.Smoothness, request.Metallic,
                options.FinalSpecularColor, options.FinalHeightStrength, options.FinalEmissionEnabled,
                options.FinalEmissionColor);

            if(MaterialCache.TryGetValue(cacheKey, out var cachedMaterial) && cachedMaterial != null) {
                return cachedMaterial;
            }

            var material = CreateLitMaterial(packet, in request, in options);
            if(material == null) {
                return CreateDefaultMaterial(new PlayerMaterialGenerationRequest {
                    BaseColor = request.BaseColor,
                    Smoothness = request.Smoothness,
                    Metallic = request.Metallic,
                    EmissionEnabled = options.FinalEmissionEnabled,
                    EmissionColor = options.FinalEmissionColor
                });
            }

            StoreMaterialInCache(cacheKey, material);

            return material;
        }

        private static MaterialGenerationOptions ResolveGenerationOptions(PlayerMaterialPacket packet,
            in PlayerMaterialGenerationRequest request) {
            var finalSpecularColor = request.SpecularColor ?? packet.defaultSpecularColor;
            var finalNormalStrength = packet.normalMapStrength;
            var finalHeightStrength = request.HeightStrength ?? packet.heightMapStrength;
            var finalEmissionColor = request.EmissionColor ?? packet.defaultEmissionColor;
            var supportsEmission = packet.emissionMap != null;
            var finalEmissionEnabled = request.EmissionEnabled &&
                                       (supportsEmission || finalEmissionColor.maxColorComponent > 0.001f);

            return new MaterialGenerationOptions(
                finalSpecularColor,
                finalNormalStrength,
                finalHeightStrength,
                finalEmissionColor,
                supportsEmission,
                finalEmissionEnabled);
        }

        private static Material CreateLitMaterial(PlayerMaterialPacket packet, in PlayerMaterialGenerationRequest request,
            in MaterialGenerationOptions options) {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if(shader == null) {
                DevLog.LogError("[PlayerMaterialGenerator] URP/Lit shader not found! Falling back to default material.");
                return null;
            }

            var material = new Material(shader) {
                name = $"PlayerMaterial_{packet.packetName}_{request.BaseColor}_{request.Smoothness}_{request.Metallic}"
            };

            ApplyWorkflowMode(material, packet);
            ApplyBaseAndNormal(material, packet, request.BaseColor, options.FinalNormalStrength);
            ApplyHeight(material, packet, options.FinalHeightStrength);
            ApplyOcclusionAndMetallicMap(material, packet);
            ApplyWorkflowValues(material, packet, request.Smoothness, request.Metallic, options.FinalSpecularColor);
            ApplyEmission(material, packet, options);

            material.renderQueue = (int)RenderQueue.Geometry;
            return material;
        }

        private static void ApplyWorkflowMode(Material material, PlayerMaterialPacket packet) {
            material.SetFloat(WorkflowModeId, packet.useMetallicWorkflow ? 0f : 1f);
        }

        private static void ApplyBaseAndNormal(Material material, PlayerMaterialPacket packet, Color baseColor,
            float normalStrength) {
            if(packet.albedoTexture != null) {
                SetTextureWithTilingAndOffset(material, BaseMapId, packet.albedoTexture, packet);
            }

            material.SetColor(BaseColorId, baseColor);

            if(packet.normalMap == null) return;
            SetTextureWithTilingAndOffset(material, NormalMapId, packet.normalMap, packet);
            material.SetFloat(NormalScaleId, normalStrength);
        }

        private static void ApplyHeight(Material material, PlayerMaterialPacket packet, float heightStrength) {
            if(packet.heightMap != null) {
                SetTextureWithTilingAndOffset(material, HeightMapId, packet.heightMap, packet);
                material.SetFloat(ParallaxId, heightStrength);
                material.EnableKeyword("_PARALLAXMAP");
                return;
            }

            material.DisableKeyword("_PARALLAXMAP");
        }

        private static void ApplyOcclusionAndMetallicMap(Material material, PlayerMaterialPacket packet) {
            if(packet.occlusionMap != null) {
                SetTextureWithTilingAndOffset(material, OcclusionMapId, packet.occlusionMap, packet);
            }

            if(packet.useMetallicWorkflow && packet.metallicMap != null) {
                SetTextureWithTilingAndOffset(material, MetallicMapId, packet.metallicMap, packet);
            }
        }

        private static void ApplyWorkflowValues(Material material, PlayerMaterialPacket packet, float smoothness,
            float metallic, Color specularColor) {
            material.SetFloat(SmoothnessId, smoothness);

            if(packet.useMetallicWorkflow) {
                material.SetFloat(MetallicId, metallic);
            } else {
                material.SetColor(SpecularColorId, specularColor);
            }
        }

        private static void ApplyEmission(Material material, PlayerMaterialPacket packet,
            in MaterialGenerationOptions options) {
            if(options.SupportsEmission && packet.emissionMap != null) {
                SetTextureWithTilingAndOffset(material, EmissionMapId, packet.emissionMap, packet);
            }

            if(options.FinalEmissionEnabled) {
                material.SetColor(EmissionColorId, options.FinalEmissionColor);
                material.EnableKeyword("_EMISSION");
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;

                if(!material.HasProperty(EmissionIntensityId)) return;
                var intensity = options.FinalEmissionColor.maxColorComponent;
                material.SetFloat(EmissionIntensityId, intensity > 0.001f ? intensity : 1f);

                return;
            }

            material.DisableKeyword("_EMISSION");
            material.SetColor(EmissionColorId, Color.black);
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
        }

        private static void SetTextureWithTilingAndOffset(Material material, int propertyId, Texture texture,
            PlayerMaterialPacket packet) {
            material.SetTexture(propertyId, texture);
            material.SetTextureScale(propertyId, packet.tiling);
            material.SetTextureOffset(propertyId, packet.offset);
        }

        private static void StoreMaterialInCache(string cacheKey, Material material) {
            MaterialCache[cacheKey] = material;
            MaterialCacheOrder.Enqueue(cacheKey);
            TrimMaterialCacheIfNeeded();
        }

        private readonly struct MaterialGenerationOptions {
            public MaterialGenerationOptions(Color finalSpecularColor, float finalNormalStrength,
                float finalHeightStrength, Color finalEmissionColor, bool supportsEmission, bool finalEmissionEnabled) {
                FinalSpecularColor = finalSpecularColor;
                FinalNormalStrength = finalNormalStrength;
                FinalHeightStrength = finalHeightStrength;
                FinalEmissionColor = finalEmissionColor;
                SupportsEmission = supportsEmission;
                FinalEmissionEnabled = finalEmissionEnabled;
            }

            public Color FinalSpecularColor { get; }
            public float FinalNormalStrength { get; }
            public float FinalHeightStrength { get; }
            public Color FinalEmissionColor { get; }
            public bool SupportsEmission { get; }
            public bool FinalEmissionEnabled { get; }
        }

        private static void TrimMaterialCacheIfNeeded() {
            while(MaterialCache.Count > MaxCachedMaterials && MaterialCacheOrder.Count > 0) {
                var evictKey = MaterialCacheOrder.Dequeue();
                if(!MaterialCache.Remove(evictKey, out var evictMaterial)) continue;
                if(evictMaterial != null) {
                    Destroy(evictMaterial);
                }
            }
        }

        /// <summary>
        /// Creates a default URP/Lit material with just base color, smoothness, and metallic (no textures).
        /// </summary>
        private static Material CreateDefaultMaterial(in PlayerMaterialGenerationRequest request) {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if(shader == null) {
                DevLog.LogError("[PlayerMaterialGenerator] URP/Lit shader not found! Creating fallback material.");
                return new Material(Shader.Find("Standard"));
            }

            var material = new Material(shader) {
                name = $"PlayerMaterial_Default_{request.BaseColor}_{request.Smoothness}_{request.Metallic}"
            };

            material.SetColor(BaseColorId, request.BaseColor);
            material.SetFloat(SmoothnessId, request.Smoothness);
            material.SetFloat(MetallicId, request.Metallic);
            material.SetFloat(WorkflowModeId, 0f); // Metallic workflow

            if(request is { EmissionEnabled: true, EmissionColor: not null }) {
                material.SetColor(EmissionColorId, request.EmissionColor.Value);
                
                material.EnableKeyword("_EMISSION");
                
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;

                if(!material.HasProperty(EmissionIntensityId)) return material;
                var intensity = request.EmissionColor.Value.maxColorComponent;
                material.SetFloat(EmissionIntensityId, intensity > 0.001f ? intensity : 1f);
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