[[vk::binding(0, 3)]]
Texture2D<float4> BindlessImages[] : register(t0, space3);

[[vk::binding(1, 3)]]
SamplerState BindlessSamplers[] : register(s0, space3);

[[vk::push_constant]]
struct
{
    uint imageIndex;
    uint samplerIndex;
} SmokeTexture;

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
    output.Position = float4(input.Position, 1.0);
    output.UV = input.UV;
    output.Color = input.Color;
    return output;
}

float4 PSMain(VSOutput input) : SV_Target0
{
    float4 textureColor = BindlessImages[NonUniformResourceIndex(SmokeTexture.imageIndex)].Sample(
        BindlessSamplers[NonUniformResourceIndex(SmokeTexture.samplerIndex)],
        input.UV);

    return textureColor * float4(input.Color, 1.0);
}
