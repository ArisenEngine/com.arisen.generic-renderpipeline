[[vk::binding(0, 3)]]
Texture2D<float4> BindlessImages[] : register(t0, space3);

[[vk::binding(2, 3)]]
ByteAddressBuffer BindlessBuffers[] : register(t2, space3);

[[vk::push_constant]]
struct
{
    uint environmentFrameBufferIndex;
    uint padding0;
    uint padding1;
    uint padding2;
} AtmosphereConstants;

struct VSOutput
{
    float4 Position : SV_Position;
    float2 UV : TEXCOORD0;
};

static const uint INVALID_BINDLESS_INDEX = 0xFFFFFFFFu;
static const uint PROJECTION_PERSPECTIVE = 0u;
static const uint DEPTH_FORWARD_ZERO_TO_ONE = 0u;

float4 LoadEnvironmentVector(uint vectorIndex)
{
    return asfloat(BindlessBuffers[
        NonUniformResourceIndex(AtmosphereConstants.environmentFrameBufferIndex)].Load4(
            vectorIndex * 16));
}

float3 CreateViewDirection(float2 screen)
{
    float4 cameraRightTanHalfFov = LoadEnvironmentVector(3);
    float4 cameraUpAspect = LoadEnvironmentVector(4);
    float3 cameraForward = LoadEnvironmentVector(5).xyz;
    return normalize(
        cameraForward +
        cameraRightTanHalfFov.xyz *
            (screen.x * cameraUpAspect.w * cameraRightTanHalfFov.w) +
        cameraUpAspect.xyz *
            (screen.y * cameraRightTanHalfFov.w));
}

float LinearizeDepth(
    float deviceDepth,
    float nearClip,
    float farClip,
    uint projectionType,
    uint depthConvention)
{
    float forwardDepth = depthConvention == DEPTH_FORWARD_ZERO_TO_ONE
        ? deviceDepth
        : 1.0 - deviceDepth;
    if (projectionType != PROJECTION_PERSPECTIVE)
    {
        return nearClip + forwardDepth * (farClip - nearClip);
    }

    return nearClip * farClip /
        max(farClip - forwardDepth * (farClip - nearClip), 0.000001);
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
    float4 depthResources = LoadEnvironmentVector(14);
    uint depthImageIndex = asuint(depthResources.x);
    uint width = (uint)depthResources.y;
    uint height = (uint)depthResources.z;
    uint depthConvention = asuint(depthResources.w);
    if (depthImageIndex == INVALID_BINDLESS_INDEX || width == 0 || height == 0)
    {
        return 0.0;
    }

    uint2 pixel = min(
        uint2(input.Position.xy),
        uint2(width - 1, height - 1));
    float deviceDepth = BindlessImages[
        NonUniformResourceIndex(depthImageIndex)].Load(int3(pixel, 0)).r;
    bool isClearDepth = depthConvention == DEPTH_FORWARD_ZERO_TO_ONE
        ? deviceDepth >= 0.999999
        : deviceDepth <= 0.000001;
    if (isClearDepth)
    {
        return 0.0;
    }

    float4 cameraDepthProjection = LoadEnvironmentVector(15);
    uint projectionType = asuint(cameraDepthProjection.z);
    float linearDepth = LinearizeDepth(
        saturate(deviceDepth),
        cameraDepthProjection.x,
        cameraDepthProjection.y,
        projectionType,
        depthConvention);
    float2 screen = input.UV * 2.0 - 1.0;
    float4 cameraRightTanHalfFov = LoadEnvironmentVector(3);
    float4 cameraUpAspect = LoadEnvironmentVector(4);
    float3 cameraForward = normalize(LoadEnvironmentVector(5).xyz);
    float3 viewDirection = CreateViewDirection(screen);
    float rayDistance;
    float3 cameraRelativePosition;
    if (projectionType == PROJECTION_PERSPECTIVE)
    {
        rayDistance = linearDepth /
            max(dot(viewDirection, cameraForward), 0.0001);
        cameraRelativePosition = viewDirection * rayDistance;
    }
    else
    {
        float halfHeight = max(cameraDepthProjection.w, 0.0001);
        float3 viewOriginOffset =
            cameraRightTanHalfFov.xyz *
                (screen.x * cameraUpAspect.w * halfHeight) +
            cameraUpAspect.xyz * (screen.y * halfHeight);
        rayDistance = linearDepth;
        cameraRelativePosition = viewOriginOffset + cameraForward * linearDepth;
        viewDirection = cameraForward;
    }

    float4 aerialProfile = LoadEnvironmentVector(12);
    float aerialAmount = 0.0;
    if (asuint(aerialProfile.x) != 0u)
    {
        float normalizedDistance = max(
            (rayDistance - aerialProfile.y) /
                max(aerialProfile.z, 0.01),
            0.0);
        aerialAmount =
            (1.0 - exp(-normalizedDistance * 2.0)) *
            saturate(aerialProfile.w);
    }

    float4 heightFogProfile = LoadEnvironmentVector(13);
    float heightFogAmount = 0.0;
    if (asuint(heightFogProfile.x) != 0u)
    {
        float3 cameraPosition = LoadEnvironmentVector(6).xyz;
        float cameraHeight = max(
            cameraPosition.y - heightFogProfile.y,
            0.0);
        float pointHeight = max(
            cameraPosition.y + cameraRelativePosition.y - heightFogProfile.y,
            0.0);
        float falloff = max(heightFogProfile.w, 0.0);
        float cameraDensity = heightFogProfile.z * exp(-cameraHeight * falloff);
        float pointDensity = heightFogProfile.z * exp(-pointHeight * falloff);
        float opticalDepth =
            max(0.5 * (cameraDensity + pointDensity) * rayDistance, 0.0);
        heightFogAmount = 1.0 - exp(-opticalDepth);
    }

    float fogAmount = saturate(
        1.0 - (1.0 - aerialAmount) * (1.0 - heightFogAmount));
    if (fogAmount <= 0.000001)
    {
        return 0.0;
    }

    float3 skyColor = max(LoadEnvironmentVector(0).rgb, 0.0);
    float3 horizonColor = max(LoadEnvironmentVector(1).rgb, 0.0);
    float4 skyProfile = LoadEnvironmentVector(10);
    float elevation = pow(
        saturate(abs(viewDirection.y)),
        max(skyProfile.y, 0.05));
    float3 hazeColor = lerp(horizonColor, skyColor, elevation * 0.35);
    float4 sunDirectionIntensity = LoadEnvironmentVector(7);
    float4 sunColorCoupling = LoadEnvironmentVector(8);
    float sunScatter = pow(
        saturate(dot(viewDirection, normalize(sunDirectionIntensity.xyz))),
        16.0);
    hazeColor +=
        max(sunColorCoupling.rgb, 0.0) *
        max(sunDirectionIntensity.w, 0.0) *
        saturate(sunColorCoupling.w) *
        sunScatter * 0.12;
    return float4(max(hazeColor, 0.0), fogAmount);
}
