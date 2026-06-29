Shader "Custom/VoxelVertexColor"
{
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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
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
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                
                // Calcola il fattore di nebbia basandosi sulla coordinata di ritaglio Z
                OUT.fogFactor = ComputeFogFactor(OUT.positionHCS.z);
                
                OUT.color = IN.color;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 normalWS = normalize(IN.normalWS);
                Light mainLight = GetMainLight();
                
                half diffuseTerm = saturate(dot(normalWS, mainLight.direction));
                half3 lightColor = mainLight.color * diffuseTerm;
                half3 ambientColor = SampleSH(normalWS);
                
                half3 finalColor = IN.color.rgb * (lightColor + ambientColor);
                
                // Miscela il colore finale con il colore della nebbia globale di URP
                finalColor = MixFog(finalColor, IN.fogFactor);
                
                return half4(finalColor, IN.color.a);
            }
            ENDHLSL
        }
    }
}