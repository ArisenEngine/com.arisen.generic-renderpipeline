using Arisen.Native.RHI;

namespace ArisenEngine.Rendering;

internal static class GenericRenderPipelineShaderAssets
{
    public static ShaderAsset CreateTonemap()
    {
        return new ShaderAsset(
            GenericRenderPipelineAssetRefs.TonemapShader.Ref.Guid,
            "GenericRP/Tonemap",
            [
                new ShaderStageAsset("Vertex", EProgramStage.Vertex, "VSMain"),
                new ShaderStageAsset("Fragment", EProgramStage.Fragment, "PSMain")
            ],
            ShaderVariantKey.VulkanDebug);
    }

    public static ShaderAsset CreateDirectionalShadow()
    {
        return new ShaderAsset(
            GenericRenderPipelineAssetRefs.DirectionalShadowShader.Ref.Guid,
            "GenericRP/DirectionalShadow",
            [
                new ShaderStageAsset("Vertex", EProgramStage.Vertex, "VSMain"),
                new ShaderStageAsset("Fragment", EProgramStage.Fragment, "PSMain")
            ],
            ShaderVariantKey.VulkanDebug);
    }

    public static ShaderAsset CreateEnvironmentSky()
    {
        return new ShaderAsset(
            GenericRenderPipelineAssetRefs.EnvironmentSkyShader.Ref.Guid,
            "GenericRP/EnvironmentSky",
            [
                new ShaderStageAsset("Vertex", EProgramStage.Vertex, "VSMain"),
                new ShaderStageAsset("Fragment", EProgramStage.Fragment, "PSMain")
            ],
            ShaderVariantKey.VulkanDebug);
    }

    public static ShaderAsset CreateOutdoorAtmosphere()
    {
        return new ShaderAsset(
            GenericRenderPipelineAssetRefs.OutdoorAtmosphereShader.Ref.Guid,
            "GenericRP/OutdoorAtmosphere",
            [
                new ShaderStageAsset("Vertex", EProgramStage.Vertex, "VSMain"),
                new ShaderStageAsset("Fragment", EProgramStage.Fragment, "PSMain")
            ],
            ShaderVariantKey.VulkanDebug);
    }

    public static ShaderAsset[] CreateRuntimeShaders()
    {
        return
        [
            CreateDirectionalShadow(),
            CreateEnvironmentSky(),
            CreateOutdoorAtmosphere(),
            CreateTonemap()
        ];
    }
}
