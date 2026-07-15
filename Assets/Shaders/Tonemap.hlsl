[[vk::binding(0, 3)]]
Texture2D<float4> BindlessImages[] : register(t0, space3);

[[vk::binding(1, 3)]]
SamplerState BindlessSamplers[] : register(s0, space3);

[[vk::push_constant]]
struct
{
    float4 toneMapParams;
    uint sceneColorImageIndex;
    uint sceneColorSamplerIndex;
    uint padding0;
    uint padding1;
} TonemapConstants;

struct VSOutput
{
    float4 Position : SV_Position;
    float2 UV : TEXCOORD0;
};

VSOutput VSMain(uint vertexId : SV_VertexID)
{
    float2 position = vertexId == 0
        ? float2(-1.0, -1.0)
        : vertexId == 1
            ? float2(3.0, -1.0)
            : float2(-1.0, 3.0);

    VSOutput output;
    output.Position = float4(position, 0.0, 1.0);
    output.UV = position * 0.5 + 0.5;
    return output;
}

float3 LinearToSRgb(float3 linearColor)
{
    linearColor = saturate(linearColor);
    float3 lowRange = linearColor * 12.92;
    float3 highRange = 1.055 * pow(linearColor, 1.0 / 2.4) - 0.055;
    return lerp(lowRange, highRange, step(0.0031308, linearColor));
}

float3 AcesFilm(float3 color)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return saturate((color * (a * color + b)) / (color * (c * color + d) + e));
}

float4 PSMain(VSOutput input) : SV_Target0
{
    Texture2D<float4> sceneColorImage =
        BindlessImages[NonUniformResourceIndex(TonemapConstants.sceneColorImageIndex)];
    SamplerState sceneColorSampler =
        BindlessSamplers[NonUniformResourceIndex(TonemapConstants.sceneColorSamplerIndex)];

    float exposure = max(TonemapConstants.toneMapParams.x, 0.0);
    float2 sceneColorUV = float2(input.UV.x, 1.0 - input.UV.y);
    float3 hdrColor = max(sceneColorImage.Sample(sceneColorSampler, sceneColorUV).rgb, 0.0);
    float3 outputColor = AcesFilm(hdrColor * exposure);
    if (TonemapConstants.toneMapParams.y > 0.5)
    {
        outputColor = LinearToSRgb(outputColor);
    }

    return float4(outputColor, 1.0);
}
