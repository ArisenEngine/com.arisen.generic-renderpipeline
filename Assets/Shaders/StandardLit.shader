Shader "GenericRP/StandardLit"
{
    MaterialContract
    {
        Texture2D BaseColor
        Texture2D Normal
        Scalar MetallicFactor
        Scalar RoughnessFactor
        Vector4 BaseColorFactor
        Vector4 EmissiveFactor
    }

    SubShader
    {
        Pass
        {
            Name "ForwardLit"
            Cull Back

            HLSLPROGRAM
            #pragma vertex VSMain
            #pragma fragment PSMain
            #pragma target 6_4
            #pragma shader_feature USE_NORMAL_MAP
            #pragma shader_feature ALPHA_TEST
            #pragma shader_feature USE_TRIPLANAR

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
                float4 lightDirectionIntensity;
                float4 lightColorAmbient;
                float4 environmentAmbient;
                float4 cameraWorldPosition;
                float metallicFactor;
                float roughnessFactor;
                uint baseColorImageIndex;
                uint baseColorSamplerIndex;
                uint normalImageIndex;
                uint normalSamplerIndex;
                uint objectBufferIndex;
                uint objectIndex;
                uint pointLightDataStart;
                uint packedLocalLightCounts;
                uint encodeOutputToSrgb;
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
                float3 WorldNormal : NORMAL0;
                float3 WorldTangent : TANGENT0;
                float TangentSign : TANGENT1;
                float3 WorldPosition : TEXCOORD1;
                float4 ShadowClipPosition : TEXCOORD2;
                nointerpolation float4 ShadowTextureIndices : TEXCOORD3;
                nointerpolation float4 ShadowParameters : TEXCOORD4;
                nointerpolation float4 EmissiveFactor : TEXCOORD5;
                nointerpolation float4 EmissiveTextureIndices : TEXCOORD6;
                nointerpolation float4 MetallicRoughnessTextureIndices : TEXCOORD7;
                nointerpolation float4 OcclusionTextureIndices : TEXCOORD8;
                nointerpolation float4 PbrMaterialParameters : TEXCOORD9;
                nointerpolation float4 EnvironmentTextureIndices0 : TEXCOORD10;
                nointerpolation float4 EnvironmentTextureIndices1 : TEXCOORD11;
                nointerpolation float4 EnvironmentParameters : TEXCOORD12;
            };

            static const float PI = 3.14159265359;
            static const uint MAX_POINT_LIGHTS = 4;
            static const uint MAX_SPOT_LIGHTS = 4;

            float3 TransformDirection(float3 direction, StaticMeshObjectData objectData)
            {
                return normalize(float3(
                    dot(direction, objectData.localToWorldColumn0.xyz),
                    dot(direction, objectData.localToWorldColumn1.xyz),
                    dot(direction, objectData.localToWorldColumn2.xyz)));
            }

            float3 TransformPosition(float4 position, StaticMeshObjectData objectData)
            {
                return float3(
                    dot(position, objectData.localToWorldColumn0),
                    dot(position, objectData.localToWorldColumn1),
                    dot(position, objectData.localToWorldColumn2));
            }

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
                output.WorldNormal = TransformDirection(input.Normal, objectData);
                output.WorldTangent = TransformDirection(input.Tangent.xyz, objectData);
                output.TangentSign = input.Tangent.w;
                output.WorldPosition = TransformPosition(localPosition, objectData);
                output.ShadowClipPosition = float4(
                    dot(localPosition, objectData.shadowModelViewProjectionColumn0),
                    dot(localPosition, objectData.shadowModelViewProjectionColumn1),
                    dot(localPosition, objectData.shadowModelViewProjectionColumn2),
                    dot(localPosition, objectData.shadowModelViewProjectionColumn3));
                output.ShadowTextureIndices = objectData.shadowTextureIndices;
                output.ShadowParameters = objectData.shadowParameters;
                output.EmissiveFactor = objectData.emissiveFactor;
                output.EmissiveTextureIndices = objectData.emissiveTextureIndices;
                output.MetallicRoughnessTextureIndices = objectData.metallicRoughnessTextureIndices;
                output.OcclusionTextureIndices = objectData.occlusionTextureIndices;
                output.PbrMaterialParameters = objectData.pbrMaterialParameters;
                output.EnvironmentTextureIndices0 = objectData.environmentTextureIndices0;
                output.EnvironmentTextureIndices1 = objectData.environmentTextureIndices1;
                output.EnvironmentParameters = objectData.environmentParameters;
                return output;
            }

            float3 ResolveNormal(VSOutput input)
            {
                float3 normal = normalize(input.WorldNormal);
#if USE_NORMAL_MAP
                float3 tangent = normalize(input.WorldTangent);
                float3 bitangent = normalize(cross(normal, tangent) * input.TangentSign);
                float3 tangentNormal = BindlessImages[NonUniformResourceIndex(DrawConstants.normalImageIndex)].Sample(
                    BindlessSamplers[NonUniformResourceIndex(DrawConstants.normalSamplerIndex)],
                    input.UV).xyz * 2.0 - 1.0;
                normal = normalize(
                    tangent * tangentNormal.x +
                    bitangent * tangentNormal.y +
                    normal * tangentNormal.z);
#endif
                return normal;
            }

            float4 SampleBaseColor(VSOutput input, float3 normal)
            {
#if USE_TRIPLANAR
                const float textureScale = 0.65;
                float3 blendWeights = pow(abs(normal), 4.0);
                blendWeights /= max(blendWeights.x + blendWeights.y + blendWeights.z, 0.0001);

                Texture2D<float4> baseColorImage =
                    BindlessImages[NonUniformResourceIndex(DrawConstants.baseColorImageIndex)];
                SamplerState baseColorSampler =
                    BindlessSamplers[NonUniformResourceIndex(DrawConstants.baseColorSamplerIndex)];
                float4 xProjection = baseColorImage.Sample(baseColorSampler, input.WorldPosition.zy * textureScale);
                float4 yProjection = baseColorImage.Sample(baseColorSampler, input.WorldPosition.xz * textureScale);
                float4 zProjection = baseColorImage.Sample(baseColorSampler, input.WorldPosition.xy * textureScale);
                return xProjection * blendWeights.x +
                    yProjection * blendWeights.y +
                    zProjection * blendWeights.z;
#else
                return BindlessImages[NonUniformResourceIndex(DrawConstants.baseColorImageIndex)].Sample(
                    BindlessSamplers[NonUniformResourceIndex(DrawConstants.baseColorSamplerIndex)],
                    input.UV);
#endif
            }

            float3 SafeNormalize(float3 value, float3 fallback)
            {
                float lengthSquared = dot(value, value);
                return lengthSquared > 0.000001 ? value * rsqrt(lengthSquared) : fallback;
            }

            float3 LinearToSRgb(float3 linearColor)
            {
                linearColor = saturate(linearColor);
                float3 lowRange = linearColor * 12.92;
                float3 highRange = 1.055 * pow(linearColor, 1.0 / 2.4) - 0.055;
                return lerp(lowRange, highRange, step(0.0031308, linearColor));
            }

            float DistributionGGX(float3 normal, float3 halfway, float roughness)
            {
                float alpha = roughness * roughness;
                float alphaSquared = alpha * alpha;
                float ndoth = saturate(dot(normal, halfway));
                float denominator = ndoth * ndoth * (alphaSquared - 1.0) + 1.0;
                return alphaSquared / max(PI * denominator * denominator, 0.0001);
            }

            float GeometrySchlickGGX(float ndotDirection, float roughness)
            {
                float offsetRoughness = roughness + 1.0;
                float k = (offsetRoughness * offsetRoughness) * 0.125;
                return ndotDirection / max(ndotDirection * (1.0 - k) + k, 0.0001);
            }

            float GeometrySmith(float3 normal, float3 viewDirection, float3 lightDirection, float roughness)
            {
                float ndotv = saturate(dot(normal, viewDirection));
                float ndotl = saturate(dot(normal, lightDirection));
                return GeometrySchlickGGX(ndotv, roughness) * GeometrySchlickGGX(ndotl, roughness);
            }

            float3 FresnelSchlick(float cosine, float3 reflectanceAtNormal)
            {
                return reflectanceAtNormal +
                    (1.0 - reflectanceAtNormal) * pow(saturate(1.0 - cosine), 5.0);
            }

            float3 FresnelSchlickRoughness(
                float cosine,
                float3 reflectanceAtNormal,
                float roughness)
            {
                float3 grazingReflectance = max(
                    float3(1.0 - roughness, 1.0 - roughness, 1.0 - roughness),
                    reflectanceAtNormal);
                return reflectanceAtNormal +
                    (grazingReflectance - reflectanceAtNormal) *
                    pow(saturate(1.0 - cosine), 5.0);
            }

            float2 DirectionToLatLongUV(float3 direction, float rotationRadians)
            {
                float longitude = atan2(direction.x, direction.z) + rotationRadians;
                return float2(
                    frac(0.5 + longitude * (1.0 / 6.28318530718)),
                    acos(clamp(direction.y, -1.0, 1.0)) * (1.0 / PI));
            }

            float3 EvaluatePbrDirectLight(
                float3 normal,
                float3 viewDirection,
                float3 albedo,
                float metallic,
                float roughness,
                float3 reflectanceAtNormal,
                float3 lightDirection,
                float3 radiance,
                out float ndotl)
            {
                ndotl = saturate(dot(normal, lightDirection));
                float ndotv = saturate(dot(normal, viewDirection));
                float3 halfway = SafeNormalize(lightDirection + viewDirection, normal);
                float distribution = DistributionGGX(normal, halfway, roughness);
                float geometry = GeometrySmith(normal, viewDirection, lightDirection, roughness);
                float3 fresnel = FresnelSchlick(saturate(dot(halfway, viewDirection)), reflectanceAtNormal);
                float3 specular = distribution * geometry * fresnel /
                    max(4.0 * ndotv * ndotl, 0.0001);
                float3 diffuseWeight = (1.0 - fresnel) * (1.0 - metallic);
                return (diffuseWeight * albedo / PI + specular) * radiance * ndotl;
            }

            float SampleDirectionalShadow(VSOutput input, float ndotl)
            {
                if (input.ShadowParameters.w < 0.5 || input.ShadowClipPosition.w <= 0.0)
                {
                    return 1.0;
                }

                float3 projected = input.ShadowClipPosition.xyz / input.ShadowClipPosition.w;
                if (projected.x < -1.0 || projected.x > 1.0 ||
                    projected.y < -1.0 || projected.y > 1.0 ||
                    projected.z < 0.0 || projected.z > 1.0)
                {
                    return 1.0;
                }

                uint shadowImageIndex = (uint)input.ShadowTextureIndices.x;
                uint shadowSamplerIndex = (uint)input.ShadowTextureIndices.y;
                float2 shadowUV = float2(projected.x * 0.5 + 0.5, 0.5 - projected.y * 0.5);
                float receiverDepth = projected.z;
                float baseBias = max(input.ShadowParameters.x, 0.0);
                float slopeBias = max(input.ShadowTextureIndices.w, 0.0);
                float bias = baseBias + slopeBias * (1.0 - saturate(ndotl));
                float strength = saturate(input.ShadowParameters.y);
                float texelSize = max(input.ShadowParameters.z, 0.00001);
                int pcfRadius = clamp((int)(input.ShadowTextureIndices.z + 0.5), 0, 3);

                Texture2D<float4> shadowMap = BindlessImages[NonUniformResourceIndex(shadowImageIndex)];
                SamplerState shadowSampler = BindlessSamplers[NonUniformResourceIndex(shadowSamplerIndex)];

                float visible = 0.0;
                float sampleCount = 0.0;
                [loop]
                for (int y = -pcfRadius; y <= pcfRadius; y++)
                {
                    [loop]
                    for (int x = -pcfRadius; x <= pcfRadius; x++)
                    {
                        float2 sampleUV = shadowUV + float2(x, y) * texelSize;
                        float sampledDepth = shadowMap.SampleLevel(shadowSampler, sampleUV, 0.0).r;
                        visible += receiverDepth - bias <= sampledDepth ? 1.0 : 0.0;
                        sampleCount += 1.0;
                    }
                }

                visible /= max(sampleCount, 1.0);
                return lerp(1.0 - strength, 1.0, visible);
            }

            float3 ResolveEmissive(VSOutput input)
            {
                float3 emissive = max(input.EmissiveFactor.rgb, 0.0) * max(input.EmissiveFactor.w, 0.0);
                if (input.EmissiveTextureIndices.z > 0.5)
                {
                    uint emissiveImageIndex = (uint)input.EmissiveTextureIndices.x;
                    uint emissiveSamplerIndex = (uint)input.EmissiveTextureIndices.y;
                    float3 emissiveTexel = BindlessImages[NonUniformResourceIndex(emissiveImageIndex)].Sample(
                        BindlessSamplers[NonUniformResourceIndex(emissiveSamplerIndex)],
                        input.UV).rgb;
                    emissive *= max(emissiveTexel, 0.0);
                }

                return emissive;
            }

            void ResolvePbrSurface(
                VSOutput input,
                out float metallic,
                out float roughness,
                out float occlusion)
            {
                metallic = saturate(DrawConstants.metallicFactor);
                roughness = DrawConstants.roughnessFactor;
                occlusion = 1.0;

                float4 packedMetallicRoughness = float4(1.0, 1.0, 1.0, 1.0);
                bool hasMetallicRoughnessTexture = input.MetallicRoughnessTextureIndices.z > 0.5;
                if (hasMetallicRoughnessTexture)
                {
                    uint imageIndex = (uint)input.MetallicRoughnessTextureIndices.x;
                    uint samplerIndex = (uint)input.MetallicRoughnessTextureIndices.y;
                    packedMetallicRoughness = BindlessImages[NonUniformResourceIndex(imageIndex)].Sample(
                        BindlessSamplers[NonUniformResourceIndex(samplerIndex)],
                        input.UV);
                    roughness *= packedMetallicRoughness.g;
                    metallic *= packedMetallicRoughness.b;
                }

                roughness = clamp(roughness, 0.08, 1.0);
                metallic = saturate(metallic);

                if (input.OcclusionTextureIndices.z > 0.5)
                {
                    uint imageIndex = (uint)input.OcclusionTextureIndices.x;
                    uint samplerIndex = (uint)input.OcclusionTextureIndices.y;
                    bool sharesPackedSample = hasMetallicRoughnessTexture &&
                        imageIndex == (uint)input.MetallicRoughnessTextureIndices.x &&
                        samplerIndex == (uint)input.MetallicRoughnessTextureIndices.y;
                    float occlusionTexel = sharesPackedSample
                        ? packedMetallicRoughness.r
                        : BindlessImages[NonUniformResourceIndex(imageIndex)].Sample(
                            BindlessSamplers[NonUniformResourceIndex(samplerIndex)],
                            input.UV).r;
                    occlusion = lerp(
                        1.0,
                        saturate(occlusionTexel),
                        saturate(input.PbrMaterialParameters.x));
                }
            }

            float4 PSMain(VSOutput input) : SV_Target0
            {
                float3 normal = ResolveNormal(input);
                float4 baseColor = SampleBaseColor(input, normal) * DrawConstants.baseColorFactor;

#if ALPHA_TEST
                clip(baseColor.a - input.PbrMaterialParameters.y);
#endif

                float3 albedo = max(baseColor.rgb * input.Color, 0.0);
                float3 lightDirection = SafeNormalize(
                    DrawConstants.lightDirectionIntensity.xyz,
                    float3(0.0, 1.0, 0.0));
                float3 viewDirection = SafeNormalize(
                    DrawConstants.cameraWorldPosition.xyz - input.WorldPosition,
                    float3(0.0, 0.0, -1.0));
                float lightIntensity = max(DrawConstants.lightDirectionIntensity.w, 0.0);
                float3 lightColor = max(DrawConstants.lightColorAmbient.rgb, 0.0);
                float3 ambientColor = max(DrawConstants.environmentAmbient.rgb, 0.0);
                float ambientIntensity = max(DrawConstants.environmentAmbient.w, 0.0);
                float metallic;
                float roughness;
                float occlusion;
                ResolvePbrSurface(input, metallic, roughness, occlusion);
                float3 reflectanceAtNormal = lerp(float3(0.04, 0.04, 0.04), albedo, metallic);

                float ndotl;
                float3 directLighting = EvaluatePbrDirectLight(
                    normal,
                    viewDirection,
                    albedo,
                    metallic,
                    roughness,
                    reflectanceAtNormal,
                    lightDirection,
                    lightColor * lightIntensity,
                    ndotl);
                directLighting *= SampleDirectionalShadow(input, ndotl);

                uint pointLightCount = min(DrawConstants.packedLocalLightCounts & 0xFFFFu, MAX_POINT_LIGHTS);
                uint spotLightCount = min((DrawConstants.packedLocalLightCounts >> 16) & 0xFFFFu, MAX_SPOT_LIGHTS);
                [unroll]
                for (uint lightIndex = 0; lightIndex < MAX_POINT_LIGHTS; lightIndex++)
                {
                    if (lightIndex >= pointLightCount)
                    {
                        break;
                    }

                    StaticMeshObjectData pointLightData =
                        BindlessObjectBuffers[NonUniformResourceIndex(DrawConstants.objectBufferIndex)]
                            [DrawConstants.pointLightDataStart + lightIndex];
                    float4 positionRange = pointLightData.modelViewProjectionColumn0;
                    float4 colorIntensity = pointLightData.modelViewProjectionColumn1;
                    float3 toLight = positionRange.xyz - input.WorldPosition;
                    float distanceSquared = dot(toLight, toLight);
                    float range = max(positionRange.w, 0.001);
                    if (distanceSquared >= range * range)
                    {
                        continue;
                    }

                    float distanceToLight = sqrt(max(distanceSquared, 0.000001));
                    float attenuation = saturate(1.0 - distanceToLight / range);
                    attenuation *= attenuation;
                    float3 pointLightDirection = toLight / distanceToLight;
                    float pointNdotL;
                    directLighting += EvaluatePbrDirectLight(
                        normal,
                        viewDirection,
                        albedo,
                        metallic,
                        roughness,
                        reflectanceAtNormal,
                        pointLightDirection,
                        max(colorIntensity.rgb, 0.0) * max(colorIntensity.w, 0.0) * attenuation,
                        pointNdotL);
                }

                uint spotLightDataStart = DrawConstants.pointLightDataStart + pointLightCount;
                [unroll]
                for (uint lightIndex = 0; lightIndex < MAX_SPOT_LIGHTS; lightIndex++)
                {
                    if (lightIndex >= spotLightCount)
                    {
                        break;
                    }

                    StaticMeshObjectData spotLightData =
                        BindlessObjectBuffers[NonUniformResourceIndex(DrawConstants.objectBufferIndex)]
                            [spotLightDataStart + lightIndex];
                    float4 positionRange = spotLightData.modelViewProjectionColumn0;
                    float4 colorIntensity = spotLightData.modelViewProjectionColumn1;
                    float4 directionInner = spotLightData.modelViewProjectionColumn2;
                    float outerConeCosine = directionInner.w > spotLightData.modelViewProjectionColumn3.x
                        ? spotLightData.modelViewProjectionColumn3.x
                        : directionInner.w - 0.001;
                    float3 toLight = positionRange.xyz - input.WorldPosition;
                    float distanceSquared = dot(toLight, toLight);
                    float range = max(positionRange.w, 0.001);
                    if (distanceSquared >= range * range)
                    {
                        continue;
                    }

                    float distanceToLight = sqrt(max(distanceSquared, 0.000001));
                    float3 spotLightDirection = toLight / distanceToLight;
                    float3 spotAxis = SafeNormalize(directionInner.xyz, float3(0.0, 0.0, 1.0));
                    float coneCosine = dot(-spotLightDirection, spotAxis);
                    if (coneCosine <= outerConeCosine)
                    {
                        continue;
                    }

                    float coneAttenuation = saturate(
                        (coneCosine - outerConeCosine) /
                        max(directionInner.w - outerConeCosine, 0.001));
                    coneAttenuation *= coneAttenuation;
                    float rangeAttenuation = saturate(1.0 - distanceToLight / range);
                    rangeAttenuation *= rangeAttenuation;
                    float spotNdotL;
                    directLighting += EvaluatePbrDirectLight(
                        normal,
                        viewDirection,
                        albedo,
                        metallic,
                        roughness,
                        reflectanceAtNormal,
                        spotLightDirection,
                        max(colorIntensity.rgb, 0.0) * max(colorIntensity.w, 0.0) *
                            rangeAttenuation * coneAttenuation,
                        spotNdotL);
                }

                float directionalAmbient = max(DrawConstants.lightColorAmbient.w, 0.0) * 0.35;
                float3 ambientRadiance =
                    ambientColor * ambientIntensity + lightColor * directionalAmbient;
                float3 ambientDiffuse = ambientRadiance * albedo * (1.0 - metallic);
                float3 ambientSpecular = ambientRadiance * reflectanceAtNormal *
                    lerp(0.22, 0.08, roughness);

                if (input.EnvironmentTextureIndices1.w > 0.5)
                {
                    uint irradianceImageIndex = (uint)input.EnvironmentTextureIndices0.x;
                    uint irradianceSamplerIndex = (uint)input.EnvironmentTextureIndices0.y;
                    uint specularImageIndex = (uint)input.EnvironmentTextureIndices0.z;
                    uint specularSamplerIndex = (uint)input.EnvironmentTextureIndices0.w;
                    uint brdfImageIndex = (uint)input.EnvironmentTextureIndices1.x;
                    uint brdfSamplerIndex = (uint)input.EnvironmentTextureIndices1.y;
                    float specularMaxLod = max(input.EnvironmentTextureIndices1.z, 0.0);
                    float environmentRotation = input.EnvironmentParameters.x;
                    float environmentIntensity = max(input.EnvironmentParameters.y, 0.0) *
                        ambientIntensity;

                    float2 irradianceUV = DirectionToLatLongUV(normal, environmentRotation);
                    float3 irradiance = max(
                        BindlessImages[NonUniformResourceIndex(irradianceImageIndex)].SampleLevel(
                            BindlessSamplers[NonUniformResourceIndex(irradianceSamplerIndex)],
                            irradianceUV,
                            0.0).rgb,
                        0.0);

                    float3 reflectionDirection = SafeNormalize(
                        reflect(-viewDirection, normal),
                        normal);
                    float2 specularUV = DirectionToLatLongUV(
                        reflectionDirection,
                        environmentRotation);
                    float3 prefilteredSpecular = max(
                        BindlessImages[NonUniformResourceIndex(specularImageIndex)].SampleLevel(
                            BindlessSamplers[NonUniformResourceIndex(specularSamplerIndex)],
                            specularUV,
                            roughness * specularMaxLod).rgb,
                        0.0);

                    float normalDotView = saturate(dot(normal, viewDirection));
                    float2 brdf = max(
                        BindlessImages[NonUniformResourceIndex(brdfImageIndex)].SampleLevel(
                            BindlessSamplers[NonUniformResourceIndex(brdfSamplerIndex)],
                            float2(normalDotView, roughness),
                            0.0).rg,
                        0.0);
                    float3 environmentFresnel = FresnelSchlickRoughness(
                        normalDotView,
                        reflectanceAtNormal,
                        roughness);
                    float3 diffuseWeight = (1.0 - environmentFresnel) * (1.0 - metallic);
                    ambientDiffuse = irradiance * albedo * diffuseWeight * environmentIntensity;
                    ambientSpecular = prefilteredSpecular *
                        (reflectanceAtNormal * brdf.x + brdf.y) * environmentIntensity;
                }

                float3 emissive = ResolveEmissive(input);
                float3 indirectLighting = (ambientDiffuse + ambientSpecular) * occlusion;
                float3 outputColor = directLighting + indirectLighting + emissive;
                if (DrawConstants.encodeOutputToSrgb != 0)
                {
                    outputColor = LinearToSRgb(outputColor);
                }

                return float4(outputColor, baseColor.a);
            }
            ENDHLSL
        }
    }
}
