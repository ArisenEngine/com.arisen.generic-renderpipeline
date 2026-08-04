using ArisenKernel.Diagnostics;

namespace ArisenEngine.Rendering;

internal sealed class GenericRenderPipelineFeatureRegistry : IGenericRenderPipelineFeatureRegistry
{
    private readonly GenericRenderPipelineFeatureRegistryCore<IGenericRenderPipelineFeature> m_Core = new();

    public int Count => m_Core.Count;
    public bool IsPipelineActive => m_Core.IsPipelineActive;

    public void Register(IGenericRenderPipelineFeature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        KernelLog.InfoFormat(
            "[GenericRP.Features] Registering feature '{0}' at order {1}.",
            feature.FeatureId,
            feature.Order);
        m_Core.Register(feature, feature.FeatureId, feature.Order);
    }

    public bool IsRegistered(IGenericRenderPipelineFeature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        return m_Core.IsRegistered(feature);
    }

    public bool Unregister(IGenericRenderPipelineFeature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        KernelLog.InfoFormat(
            "[GenericRP.Features] Unregistering feature '{0}'.",
            feature.FeatureId);
        return m_Core.Unregister(feature, feature.FeatureId);
    }

    internal IGenericRenderPipelineFeature[] BeginPipelineActivation()
    {
        var activeFeatures = m_Core.BeginPipelineActivation();
        KernelLog.InfoFormat(
            "[GenericRP.Features] Froze {0} feature(s) for pipeline activation.",
            activeFeatures.Length);
        return activeFeatures;
    }

    internal void EndPipelineActivation()
    {
        m_Core.EndPipelineActivation();
    }
}
