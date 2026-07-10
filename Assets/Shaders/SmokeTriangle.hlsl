// @arisen.material.texture2d BaseColor
// @arisen.material.vector4 BaseColorFactor

[[vk::binding(0, 3)]]
Texture2D<float4> BindlessImages[] : register(t0, space3);

[[vk::binding(1, 3)]]
SamplerState BindlessSamplers[] : register(s0, space3);

[[vk::push_constant]]
struct
{
    float4 modelViewProjectionColumn0;
    float4 modelViewProjectionColumn1;
    float4 modelViewProjectionColumn2;
    float4 modelViewProjectionColumn3;
    float4 baseColorFactor;
    uint imageIndex;
    uint samplerIndex;
    uint2 padding;
} DrawConstants;

struct VSInput
{
    float3 Position : POSITION0;
    float2 UV : TEXCOORD0;
    float3 Color : COLOR0;
};

struct VSOutput
{
    float4 Position : SV_Position;
    float2 UV : TEXCOORD0;
    float3 Color : COLOR0;
};

VSOutput VSMain(VSInput input)
{
    VSOutput output;
    float4 localPosition = float4(input.Position, 1.0);
    output.Position = float4(
        dot(localPosition, DrawConstants.modelViewProjectionColumn0),
        dot(localPosition, DrawConstants.modelViewProjectionColumn1),
        dot(localPosition, DrawConstants.modelViewProjectionColumn2),
        dot(localPosition, DrawConstants.modelViewProjectionColumn3));
    output.UV = input.UV;
    output.Color = input.Color;
    return output;
}

float4 PSMain(VSOutput input) : SV_Target0
{
    float4 textureColor = BindlessImages[NonUniformResourceIndex(DrawConstants.imageIndex)].Sample(
        BindlessSamplers[NonUniformResourceIndex(DrawConstants.samplerIndex)],
        input.UV);

    return textureColor * float4(input.Color, 1.0) * DrawConstants.baseColorFactor;
}
