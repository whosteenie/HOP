Shader "HOP/Zone"
{
    Properties
    {
        [HDR] _MainColor ("Main Color", Color) = (0, 0.5, 1, 1)
        _RimPower ("Rim Power", Range(0.1, 8.0)) = 2.0
        _FresnelScale ("Fresnel Scale", Range(0.1, 10)) = 2.0
        
        _VerticalFadeStart ("Vertical Fade Start", Range(0, 1)) = 0.2
        _VerticalFadeLength ("Vertical Fade Softness", Range(0.01, 1)) = 0.5
        
        _ScrollSpeed ("Scroll Speed (XY)", Vector) = (0, 0.5, 0, 0)
        _NoiseTex ("Noise Texture", 2D) = "white" {}
        _NoiseScale ("Noise Scale", Float) = 1.0
        
        _PulseSpeed ("Pulse Speed", Float) = 1.0
        _PulseIntensity ("Pulse Intensity", Range(0, 1)) = 0.2
        
        _HeightVarFreq ("Height Variation Freq", Float) = 3.0
        _HeightVarAmp ("Height Variation Amp", Float) = 0.2
        _HeightVarSpeed ("Height Variation Speed", Float) = 0.5
        
        _BaseGlowHeight ("Base Glow Height (UV)", Range(0, 1)) = 0.15
        _BaseGlowIntensity ("Base Glow Intensity", Range(0, 5)) = 2.0
        
        _DepthSoftness ("Depth Softness", Range(0, 5)) = 0.5
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        LOD 100
        
        ZWrite Off
        Blend SrcAlpha One 
        Cull Off 

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normal : TEXCOORD1;
                float3 viewDir : TEXCOORD3;
                float3 worldPos : TEXCOORD4;
                float4 screenPos : TEXCOORD5;
            };

            sampler2D _NoiseTex;
            float4 _NoiseTex_ST;
            
            float4 _MainColor;
            float _RimPower;
            float _FresnelScale;
            
            float _VerticalFadeStart;
            float _VerticalFadeLength;
            
            float4 _ScrollSpeed;
            float _NoiseScale;
            
            float _PulseSpeed;
            float _PulseIntensity;
            
            float _HeightVarFreq;
            float _HeightVarAmp;
            float _HeightVarSpeed;
            
            float _BaseGlowHeight;
            float _BaseGlowIntensity;
            
            float _DepthSoftness;
            
            sampler2D _CameraDepthTexture;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _NoiseTex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldPos = worldPos;
                o.viewDir = normalize(UnityWorldSpaceViewDir(worldPos));
                
                o.screenPos = ComputeScreenPos(o.vertex);
                
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // 0. Remove Caps
                if (abs(i.normal.y) > 0.95) discard;

                // 1. Noise Scroll
                float2 scrolledUV = i.uv * _NoiseScale + _ScrollSpeed.xy * _Time.y;
                fixed4 noise = tex2D(_NoiseTex, scrolledUV);
                
                // 2. Fresnel / Hollow Ring
                float NdotV = saturate(abs(dot(i.normal, i.viewDir)));
                float fresnel = pow(1.0 - NdotV, _RimPower);
                
                // 3. Pulse Effect
                float pulse = 1.0 + sin(_Time.y * _PulseSpeed) * _PulseIntensity;
                
                float rimGlow = fresnel * _FresnelScale * pulse;
                float noiseGlow = noise.r * fresnel * 0.5 * pulse;
                float combinedAlpha = rimGlow + noiseGlow;
                
                // 4. Height Variation & Vertical Fade
                // SEAM FIX: Multiply UV.x by 2*PI and use an integer frequency to ensure seamless wrapping at the UV seam
                float PI = 3.14159265;
                // Layer 1: Base low-frequency wave 
                float freq1 = floor(_HeightVarFreq);
                float wave1 = sin(i.uv.x * freq1 * 2 * PI + _Time.y * _HeightVarSpeed);
                
                // Layer 2: Higher frequency wave (offset speed) to break up the pattern
                float freq2 = floor(_HeightVarFreq * 1.5 + 1.0); // Different integer freq
                float wave2 = sin(i.uv.x * freq2 * 2 * PI - _Time.y * _HeightVarSpeed * 1.3 + 2.0);
                
                // Combine: (Wave1 + Wave2 * 0.5) scaled to Amp
                // We divide by 1.5 to keep the total amplitude roughly within _HeightVarAmp
                float combinedWave = (wave1 + wave2 * 0.5) / 1.5;
                float wave = combinedWave * _HeightVarAmp;
                
                // HEIGHT FIX: Ensure the wave peaks never touch the top mesh edge (UV=1). 
                // We lower the base from 1.0 to (1.0 - Amp - margin)
                float topEdgeBase = 0.95 - _HeightVarAmp; 
                float topEdge = topEdgeBase + wave;
                
                // Fade out as we approach this dynamic top edge
                float fadeEnd = topEdge;
                float fadeStart = topEdge - _VerticalFadeLength;
                
                float verticalGradient = smoothstep(fadeEnd, fadeStart, i.uv.y);
                
                // 5. Base Glow (Floor Contact Ring)
                float baseGlow = smoothstep(_BaseGlowHeight, 0.0, i.uv.y);
                baseGlow = pow(baseGlow, 2.0); 
                baseGlow *= _BaseGlowIntensity;
                
                combinedAlpha += baseGlow;
                
                // 6. Depth Fade
                float sceneDepth = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture, UNITY_PROJ_COORD(i.screenPos)));
                float partDepth = i.screenPos.w;
                float depthDifference = sceneDepth - partDepth;
                float depthFade = saturate(depthDifference / _DepthSoftness);
                
                // 7. Bottom Fade (Seam Hider)
                // Force fade out at the very bottom to avoid hard geometry intersection 
                // independent of depth buffer (which can sometimes fail on transparents)
                float bottomFade = smoothstep(0.0, 0.05, i.uv.y); // Fade from 0 to 1 over bottom 5%
                
                // 8. Combined
                float4 finalColor = _MainColor;
                finalColor.a *= combinedAlpha * verticalGradient * depthFade * bottomFade;
                
                // Boost brightness for base glow, but also respect depth fade an bottom fade
                finalColor.rgb += baseGlow * 0.5 * depthFade * bottomFade;
                
                return finalColor;
            }
            ENDCG
        }
    }
}
