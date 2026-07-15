[[vk::push_constant]]
struct
{
    float4 skyColorIntensity;
    float4 horizonColor;
    float4 groundColor;
    float4 outputEncoding;
} SkyConstants;

struct VSOutput
{
    float4 Position : SV_Position;
    float2 UV : TEXCOORD0;
};

float3 LinearToSRgb(float3 linearColor)
{
    linearColor = saturate(linearColor);
    float3 lowRange = linearColor * 12.92;
    float3 highRange = 1.055 * pow(linearColor, 1.0 / 2.4) - 0.055;
    return lerp(lowRange, highRange, step(0.0031308, linearColor));
}

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

float4 PSMain(VSOutput input) : SV_Target0
{
    float vertical = saturate(input.UV.y);
    float lowerBlend = smoothstep(0.0, 0.5, vertical);
    float upperBlend = pow(saturate((vertical - 0.5) * 2.0), 0.7);
    float3 lowerSky = lerp(SkyConstants.groundColor.rgb, SkyConstants.horizonColor.rgb, lowerBlend);
    float3 upperSky = lerp(SkyConstants.horizonColor.rgb, SkyConstants.skyColorIntensity.rgb, upperBlend);
    float3 sky = (vertical < 0.5 ? lowerSky : upperSky) * max(SkyConstants.skyColorIntensity.w, 0.0);
    if (SkyConstants.outputEncoding.x > 0.5)
    {
        sky = LinearToSRgb(sky);
    }

    return float4(sky, 1.0);
}
