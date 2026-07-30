namespace ArisenEngine.Rendering;

/// <summary>
/// Setup-only registration surface for optional Generic RP feature packages.
/// </summary>
public interface IGenericRenderPipelineFeatureRegistry
{
    int Count { get; }
    bool IsPipelineActive { get; }

    void Register(IGenericRenderPipelineFeature feature);

    /// <summary>
    /// Removes the exact registered feature instance. Returns false when it was already absent.
    /// </summary>
    bool Unregister(IGenericRenderPipelineFeature feature);
}
