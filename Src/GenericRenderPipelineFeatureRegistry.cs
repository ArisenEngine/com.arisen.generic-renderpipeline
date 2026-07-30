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
        m_Core.Register(feature, feature.FeatureId, feature.Order);
        KernelLog.InfoFormat(
            "[GenericRP.Features] Registered feature '{0}' at order {1}.",
            feature.FeatureId,
            feature.Order);
    }

    public bool Unregister(IGenericRenderPipelineFeature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        bool removed = m_Core.Unregister(feature, feature.FeatureId);
        if (removed)
        {
            KernelLog.InfoFormat(
                "[GenericRP.Features] Unregistered feature '{0}'.",
                feature.FeatureId);
        }

        return removed;
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
