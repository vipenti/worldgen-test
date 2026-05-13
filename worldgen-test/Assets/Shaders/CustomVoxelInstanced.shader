Shader "Custom/VoxelInstanced"
  {
      Properties { _BaseColor ("Color", Color) = (1,1,1,1) }
      SubShader
      {
          Tags { "RenderType"="Opaque" }
          Pass
          {
              CGPROGRAM
              #pragma vertex vert
              #pragma fragment frag
              #pragma multi_compile_instancing
              #include "UnityCG.cginc"

              UNITY_INSTANCING_BUFFER_START(Props)
                  UNITY_DEFINE_INSTANCED_PROP(float4, _BaseColor)
              UNITY_INSTANCING_BUFFER_END(Props)

              struct appdata { float4 vertex : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
              struct v2f    { float4 pos : SV_POSITION; float4 col : COLOR;
                              UNITY_VERTEX_INPUT_INSTANCE_ID };

              v2f vert(appdata v)
              {
                  v2f o;
                  UNITY_SETUP_INSTANCE_ID(v);
                  UNITY_TRANSFER_INSTANCE_ID(v, o);
                  o.pos = UnityObjectToClipPos(v.vertex);
                  o.col = UNITY_ACCESS_INSTANCED_PROP(Props, _BaseColor);
                  return o;
              }

              fixed4 frag(v2f i) : SV_Target
              {
                  UNITY_SETUP_INSTANCE_ID(i);
                  return i.col;
              }
              ENDCG
          }
      }
  }