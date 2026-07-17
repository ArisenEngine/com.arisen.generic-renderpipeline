[[vk::binding(0, 3)]]
Texture2D<float4> BindlessImages[] : register(t0, space3);

[[vk::binding(1, 3)]]
SamplerState BindlessSamplers[] : register(s0, space3);

[[vk::push_constant]]
struct
{
    float4 skyColorIntensity;
    float4 horizonColor;
    float4 groundColor;
    float4 cameraRightTanHalfFov;
    float4 cameraUpAspect;
    float4 cameraForward;
    uint environmentImageIndex;
    uint environmentSamplerIndex;
    float environmentRotationRadians;
    float environmentTextureIntensity;
} SkyConstants;

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

float4 PSMain(VSOutput input) : SV_Target0
{
    if (SkyConstants.environmentImageIndex != 0xFFFFFFFFu &&
        SkyConstants.environmentSamplerIndex != 0xFFFFFFFFu)
    {
        float2 screen = input.UV * 2.0 - 1.0;
        float3 viewDirection = normalize(
            SkyConstants.cameraForward.xyz +
            SkyConstants.cameraRightTanHalfFov.xyz *
                (screen.x * SkyConstants.cameraUpAspect.w * SkyConstants.cameraRightTanHalfFov.w) +
            SkyConstants.cameraUpAspect.xyz *
                (screen.y * SkyConstants.cameraRightTanHalfFov.w));
        float longitude = atan2(viewDirection.x, viewDirection.z) +
            SkyConstants.environmentRotationRadians;
        float2 environmentUV = float2(
            frac(0.5 + longitude * (1.0 / 6.28318530718)),
            acos(clamp(viewDirection.y, -1.0, 1.0)) * (1.0 / 3.14159265359));

        float3 environmentColor = BindlessImages[
            NonUniformResourceIndex(SkyConstants.environmentImageIndex)].Sample(
                BindlessSamplers[
                    NonUniformResourceIndex(SkyConstants.environmentSamplerIndex)],
                environmentUV).rgb;
        float intensity = max(SkyConstants.environmentTextureIntensity, 0.0) *
            max(SkyConstants.skyColorIntensity.w, 0.0);
        return float4(max(environmentColor, 0.0) * intensity, 1.0);
    }

    float vertical = saturate(input.UV.y);
    float lowerBlend = smoothstep(0.0, 0.5, vertical);
    float upperBlend = pow(saturate((vertical - 0.5) * 2.0), 0.7);
    float3 lowerSky = lerp(SkyConstants.groundColor.rgb, SkyConstants.horizonColor.rgb, lowerBlend);
    float3 upperSky = lerp(SkyConstants.horizonColor.rgb, SkyConstants.skyColorIntensity.rgb, upperBlend);
    float3 sky = (vertical < 0.5 ? lowerSky : upperSky) * max(SkyConstants.skyColorIntensity.w, 0.0);
    return float4(sky, 1.0);
}
