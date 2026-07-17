Shader "GenericRP/SmokeStaticMesh"
{
    MaterialContract
    {
        Texture2D BaseColor
        Vector4 BaseColorFactor
    }

    SubShader
    {
        Pass
        {
            Name "StaticMesh"
            Cull Off
            Blend One Zero
            BlendOp Add

            HLSLPROGRAM
            #pragma vertex VSMain
            #pragma fragment PSMain
            #pragma target 6_4

            [[vk::binding(0, 3)]]
            Texture2D<float4> BindlessImages[] : register(t0, space3);

            [[vk::binding(1, 3)]]
            SamplerState BindlessSamplers[] : register(s0, space3);

            struct StaticMeshObjectData
            {
                float4 modelViewProjectionColumn0;
                float4 modelViewProjectionColumn1;
                float4 modelViewProjectionColumn2;
                float4 modelViewProjectionColumn3;
                float4 localToWorldColumn0;
                float4 localToWorldColumn1;
                float4 localToWorldColumn2;
                float4 shadowModelViewProjectionColumn0;
                float4 shadowModelViewProjectionColumn1;
                float4 shadowModelViewProjectionColumn2;
                float4 shadowModelViewProjectionColumn3;
                float4 shadowTextureIndices;
                float4 shadowParameters;
                float4 emissiveFactor;
                float4 emissiveTextureIndices;
                float4 metallicRoughnessTextureIndices;
                float4 occlusionTextureIndices;
                float4 pbrMaterialParameters;
                float4 environmentTextureIndices0;
                float4 environmentTextureIndices1;
                float4 environmentParameters;
            };

            [[vk::binding(2, 3)]]
            StructuredBuffer<StaticMeshObjectData> BindlessObjectBuffers[] : register(t2, space3);

            [[vk::push_constant]]
            struct
            {
                float4 baseColorFactor;
                float metallicFactor;
                float roughnessFactor;
                uint baseColorImageIndex;
                uint baseColorSamplerIndex;
                uint normalImageIndex;
                uint normalSamplerIndex;
                uint objectBufferIndex;
                uint objectIndex;
            } DrawConstants;

            struct VSInput
            {
                float3 Position : POSITION0;
                float3 Normal : NORMAL0;
                float4 Tangent : TANGENT0;
                float2 UV : TEXCOORD0;
                float3 Color : COLOR0;
            };

            struct VSOutput
            {
                float4 Position : SV_Position;
                float2 UV : TEXCOORD0;
                float3 Color : COLOR0;
                float3 Normal : NORMAL0;
                float4 Tangent : TANGENT0;
            };

            VSOutput VSMain(VSInput input)
            {
                VSOutput output;
                StaticMeshObjectData objectData =
                    BindlessObjectBuffers[NonUniformResourceIndex(DrawConstants.objectBufferIndex)][DrawConstants.objectIndex];
                float4 localPosition = float4(input.Position, 1.0);
                output.Position = float4(
                    dot(localPosition, objectData.modelViewProjectionColumn0),
                    dot(localPosition, objectData.modelViewProjectionColumn1),
                    dot(localPosition, objectData.modelViewProjectionColumn2),
                    dot(localPosition, objectData.modelViewProjectionColumn3));
                output.UV = input.UV;
                output.Color = input.Color;
                output.Tangent = input.Tangent;
                output.Normal = normalize(float3(
                    dot(input.Normal, objectData.localToWorldColumn0.xyz),
                    dot(input.Normal, objectData.localToWorldColumn1.xyz),
                    dot(input.Normal, objectData.localToWorldColumn2.xyz)));
                return output;
            }

            float4 PSMain(VSOutput input) : SV_Target0
            {
                float4 textureColor = BindlessImages[NonUniformResourceIndex(DrawConstants.baseColorImageIndex)].Sample(
                    BindlessSamplers[NonUniformResourceIndex(DrawConstants.baseColorSamplerIndex)],
                    input.UV);

                float3 lightDirection = normalize(float3(0.35, 0.55, 0.76));
                float ndotl = saturate(dot(normalize(input.Normal), lightDirection));
                float lighting = 0.28 + ndotl * 0.72;
                return textureColor * float4(input.Color * lighting, 1.0) * DrawConstants.baseColorFactor;
            }
            ENDHLSL
        }
    }
}
