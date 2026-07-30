[[vk::binding(0, 3)]]
Texture2D<float4> BindlessImages[] : register(t0, space3);

[[vk::binding(1, 3)]]
SamplerState BindlessSamplers[] : register(s0, space3);

[[vk::binding(2, 3)]]
ByteAddressBuffer BindlessBuffers[] : register(t2, space3);

[[vk::push_constant]]
struct
{
    uint environmentFrameBufferIndex;
    uint padding0;
    uint padding1;
    uint padding2;
} SkyConstants;

struct VSOutput
{
    float4 Position : SV_Position;
    float2 UV : TEXCOORD0;
};

static const float PI = 3.14159265359;
static const float TWO_PI = 6.28318530718;
static const uint INVALID_BINDLESS_INDEX = 0xFFFFFFFFu;
static const uint SKY_MODE_PANORAMA = 0u;
static const uint SKY_MODE_PROCEDURAL_OUTDOOR = 1u;

float4 LoadEnvironmentVector(uint vectorIndex)
{
    return asfloat(BindlessBuffers[
        NonUniformResourceIndex(SkyConstants.environmentFrameBufferIndex)].Load4(
            vectorIndex * 16));
}

float3 CreateViewDirection(float2 uv)
{
    float4 cameraRightTanHalfFov = LoadEnvironmentVector(3);
    float4 cameraUpAspect = LoadEnvironmentVector(4);
    float4 cameraForwardProjection = LoadEnvironmentVector(5);
    float2 screen = uv * 2.0 - 1.0;
    return normalize(
        cameraForwardProjection.xyz +
        cameraRightTanHalfFov.xyz *
            (screen.x * cameraUpAspect.w * cameraRightTanHalfFov.w) +
        cameraUpAspect.xyz *
            (screen.y * cameraRightTanHalfFov.w));
}

float3 EvaluateProceduralOutdoorSky(float3 viewDirection, bool includeSun)
{
    float4 skyColorIntensity = LoadEnvironmentVector(0);
    float3 horizonColor = max(LoadEnvironmentVector(1).rgb, 0.0);
    float3 groundColor = max(LoadEnvironmentVector(2).rgb, 0.0);
    float4 skyProfile = LoadEnvironmentVector(10);
    float horizonExponent = max(skyProfile.y, 0.05);
    float zenithExponent = max(skyProfile.z, 0.05);

    float3 gradient;
    if (viewDirection.y >= 0.0)
    {
        float horizonResponse = pow(saturate(viewDirection.y), horizonExponent);
        float zenithResponse = 1.0 - pow(1.0 - horizonResponse, zenithExponent);
        gradient = lerp(horizonColor, max(skyColorIntensity.rgb, 0.0), zenithResponse);
    }
    else
    {
        float groundResponse = pow(saturate(-viewDirection.y), horizonExponent);
        gradient = lerp(horizonColor, groundColor, groundResponse);
    }

    float3 sky = gradient * max(skyColorIntensity.w, 0.0);
    if (!includeSun)
    {
        return sky;
    }

    float4 sunDirectionIntensity = LoadEnvironmentVector(7);
    float4 sunColorCoupling = LoadEnvironmentVector(8);
    float4 sunProfileExposure = LoadEnvironmentVector(11);
    float sunAlignment = dot(
        viewDirection,
        normalize(sunDirectionIntensity.xyz));
    float angularRadius = max(skyProfile.w, 0.0001);
    float sunDisc = smoothstep(
        cos(angularRadius * 1.35),
        cos(angularRadius),
        sunAlignment);
    float sunGlow = pow(
        saturate(sunAlignment),
        max(sunProfileExposure.z, 1.0));
    float sunAmount =
        sunDisc * max(sunProfileExposure.x, 0.0) +
        sunGlow * max(sunProfileExposure.y, 0.0);
    float3 sunRadiance =
        max(sunColorCoupling.rgb, 0.0) *
        max(sunDirectionIntensity.w, 0.0) *
        saturate(sunColorCoupling.w) *
        sunAmount;
    return sky + sunRadiance;
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
    float3 viewDirection = CreateViewDirection(input.UV);
    float4 panoramaResources = LoadEnvironmentVector(9);
    float4 skyProfile = LoadEnvironmentVector(10);
    uint skyMode = asuint(skyProfile.x);
    uint environmentImageIndex = asuint(panoramaResources.x);
    uint environmentSamplerIndex = asuint(panoramaResources.y);
    if (skyMode == SKY_MODE_PANORAMA &&
        environmentImageIndex != INVALID_BINDLESS_INDEX &&
        environmentSamplerIndex != INVALID_BINDLESS_INDEX)
    {
        float longitude = atan2(viewDirection.x, viewDirection.z) +
            panoramaResources.z;
        float2 environmentUV = float2(
            frac(0.5 + longitude / TWO_PI),
            acos(clamp(viewDirection.y, -1.0, 1.0)) / PI);
        float3 environmentColor = BindlessImages[
            NonUniformResourceIndex(environmentImageIndex)].Sample(
                BindlessSamplers[
                    NonUniformResourceIndex(environmentSamplerIndex)],
                environmentUV).rgb;
        float skyIntensity = max(LoadEnvironmentVector(0).w, 0.0);
        return float4(
            max(environmentColor, 0.0) *
                max(panoramaResources.w, 0.0) *
                skyIntensity,
            1.0);
    }

    bool includeSun = skyMode == SKY_MODE_PROCEDURAL_OUTDOOR;
    return float4(
        EvaluateProceduralOutdoorSky(viewDirection, includeSun),
        1.0);
}
