Shader "Custom/VoxelVertexColor"
{
    Properties
    {
        _Saturation ("Saturation", Range(0.0, 3.0)) = 1.3
        _Contrast ("Contrast", Range(0.0, 3.0)) = 1.2
        _AmbientBoost ("Ambient Boost", Range(0.0, 2.0)) = 0.6
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma prefer_hlslcc gles
            #pragma exclude_renderers d3d11_9x
            // Abilita la compilazione delle varianti di nebbia in URP
            #pragma multi_compile_fog
            // Varianti ombra della luce principale (cascade + screen-space) e qualità soft shadow:
            // senza queste il campionamento di GetMainLight(shadowCoord) qui sotto compila ma
            // restituisce sempre attenuazione 1 (nessuna ombra visibile), perché URP non genera
            // affatto le keyword necessarie a leggere la shadow map della luce direzionale.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half _Saturation;
                half _Contrast;
                half _AmbientBoost;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 color      : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 normalWS    : TEXCOORD0;
                float4 color       : COLOR;
                float  fogFactor   : TEXCOORD1; // Passa il fattore di nebbia al fragment shader
                float4 shadowCoord : TEXCOORD2; // Coordinate per campionare la shadow map della luce principale
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionHCS = TransformWorldToHClip(positionWS);
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);

                // Calcola il fattore di nebbia basandosi sulla coordinata di ritaglio Z
                OUT.fogFactor = ComputeFogFactor(OUT.positionHCS.z);
                OUT.shadowCoord = TransformWorldToShadowCoord(positionWS);

                OUT.color = IN.color;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // 1. Base vertex color adjustment (Saturation & Contrast)
                half3 baseColor = IN.color.rgb;

                // Saturation adjustment via Rec.709 luminance weights
                half luminance = dot(baseColor, half3(0.2126, 0.7152, 0.0722));
                baseColor = lerp(half3(luminance, luminance, luminance), baseColor, _Saturation);

                // Contrast adjustment (pivot around 0.5)
                baseColor = saturate((baseColor - 0.5) * _Contrast + 0.5);

                // 2. Lighting calculation
                float3 normalWS = normalize(IN.normalWS);
                Light mainLight = GetMainLight(IN.shadowCoord);

                half diffuseTerm = saturate(dot(normalWS, mainLight.direction)) * mainLight.shadowAttenuation;
                half3 lightColor = mainLight.color * diffuseTerm;
                half3 ambientColor = SampleSH(normalWS) * _AmbientBoost;

                half3 finalColor = baseColor * (lightColor + ambientColor);

                // Miscela il colore finale con il colore della nebbia globale di URP
                finalColor = MixFog(finalColor, IN.fogFactor);

                return half4(finalColor, IN.color.a);
            }
            ENDHLSL
        }

        Pass
        {
            // Senza questo pass URP non ha modo di renderizzare questi oggetti nella shadow map
            // della luce principale: restavano "invisibili" per il sistema ombre, quindi non
            // proiettavano mai ombra su terreno o su altri oggetti, indipendentemente da
            // Shadow Strength o da qualunque impostazione della luce.
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex vertShadow
            #pragma fragment fragShadow
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // Impostati automaticamente da URP prima di renderizzare questo pass per ciascuna luce
            // che proietta ombre (vedi ShadowCasterPass di URP stesso, stesso meccanismo usato dai
            // pass ShadowCaster di Lit/SimpleLit).
            float3 _LightDirection;
            float3 _LightPosition;

            struct AttributesShadow
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct VaryingsShadow
            {
                float4 positionCS : SV_POSITION;
            };

            float4 GetShadowPositionHClip(AttributesShadow IN)
            {
                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(IN.normalOS);

#if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
#else
                float3 lightDirectionWS = _LightDirection;
#endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));

#if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
#else
                positionCS.z = max(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
#endif
                return positionCS;
            }

            VaryingsShadow vertShadow(AttributesShadow IN)
            {
                VaryingsShadow OUT;
                OUT.positionCS = GetShadowPositionHClip(IN);
                return OUT;
            }

            half4 fragShadow(VaryingsShadow IN) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }
}
