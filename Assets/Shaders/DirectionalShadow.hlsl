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
    uint baseColorImageIndex;
    uint baseColorSamplerIndex;
    float alphaCutoff;
    uint alphaTest;
} DrawConstants;

struct VSInput
{
    float3 Position : POSITION0;
    float2 UV : TEXCOORD0;
};

struct VSOutput
{
    float4 Position : SV_Position;
    float2 UV : TEXCOORD0;
};

VSOutput VSMain(VSInput input)
{
    float4 localPosition = float4(input.Position, 1.0);
    VSOutput output;
    output.Position = float4(
        dot(localPosition, DrawConstants.modelViewProjectionColumn0),
        dot(localPosition, DrawConstants.modelViewProjectionColumn1),
        dot(localPosition, DrawConstants.modelViewProjectionColumn2),
        dot(localPosition, DrawConstants.modelViewProjectionColumn3));
    output.UV = input.UV;
    return output;
}

void PSMain(VSOutput input)
{
    if (DrawConstants.alphaTest == 0)
    {
        return;
    }

    float alpha = BindlessImages[
        NonUniformResourceIndex(DrawConstants.baseColorImageIndex)].Sample(
            BindlessSamplers[
                NonUniformResourceIndex(DrawConstants.baseColorSamplerIndex)],
            input.UV).a * DrawConstants.baseColorFactor.a;
    clip(alpha - DrawConstants.alphaCutoff);
}
