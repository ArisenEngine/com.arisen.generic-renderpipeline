using ArisenEngine.Rendering.Resources;

namespace ArisenEngine.Rendering;

internal readonly record struct StaticMeshMaterialPipelineSignature(
    Guid ShaderGuid,
    AssetDependencyStamp ShaderDependencyStamp,
    string ShaderVariantIdentity,
    MaterialRenderState RenderState,
    RenderQueueInfo RenderQueue);

internal static class StaticMeshMaterialPipelinePolicy
{
    public static bool RequiresPipelineRebuild(
        bool currentSlotValid,
        in StaticMeshMaterialPipelineSignature current,
        in StaticMeshMaterialPipelineSignature incoming)
    {
        return !currentSlotValid || current != incoming;
    }
}
