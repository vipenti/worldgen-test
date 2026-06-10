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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            // MODIFICA 1: Includiamo la libreria URP per gestire le luci
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                // MODIFICA 2: Chiediamo i normali della mesh in Object Space
                float3 normalOS   : NORMAL; 
                float4 color      : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                // MODIFICA 3: Spazio per passare i normali trasformati in World Space al fragment shader
                float3 normalWS    : TEXCOORD0; 
                float4 color       : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                // MODIFICA 4: Trasformiamo i normali da Object Space a World Space
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                
                OUT.color = IN.color;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // Normalizziamo il vettore per sicurezza
                float3 normalWS = normalize(IN.normalWS);
                
                // MODIFICA 5: Recuperiamo i dati della luce principale della scena (il sole)
                Light mainLight = GetMainLight();
                
                // Calcoliamo l'intensità della luce (il classico Lambertian Diffuse)
                // saturate() blocca il valore tra 0 e 1, evitando valori negativi dove il sole non batte
                half diffuseTerm = saturate(dot(normalWS, mainLight.direction));
                
                // Calcoliamo il colore della luce del sole combinato con la sua intensità
                half3 lightColor = mainLight.color * diffuseTerm;
                
                // Recuperiamo anche una base di luce ambientale (Skybox/Environment) 
                // così le zone in ombra non diventano completamente nere come la pece
                half3 ambientColor = SampleSH(normalWS);
                
                // Il colore finale sarà il colore del tuo voxel combinato con (Luce Sole + Luce Ambiente)
                half3 finalColor = IN.color.rgb * (lightColor + ambientColor);
                
                return half4(finalColor, IN.color.a);
            }
            ENDHLSL
        }
    }
}