using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Math;

using ArisenKernel.Packages;

namespace ArisenEngine.Rendering;

[ArisenPackage("com.arisen.generic-renderpipeline")]
public class GenericRenderPipelineAsset : RenderPipelineAsset
{
    public Color ClearColor = new Color(1.0f, 0.4f, 0.7f, 1.0f);
    private readonly IAssetDatabase? m_AssetDatabase;
    private readonly GenericRenderMaterialLibrary? m_MaterialLibrary;
    private readonly DeferredRenderResourceDisposalQueue? m_DisposalQueue;

    public GenericRenderPipelineAsset()
    {
    }

    public GenericRenderPipelineAsset(
        IAssetDatabase assetDatabase,
        GenericRenderMaterialLibrary materialLibrary,
        DeferredRenderResourceDisposalQueue disposalQueue)
    {
        m_AssetDatabase = assetDatabase;
        m_MaterialLibrary = materialLibrary;
        m_DisposalQueue = disposalQueue;
    }

    protected override void AfterDeserialize()
    {
        IsDirty = true;
    }

    protected override void BeforeSerialize() { }

    protected override RenderPipeline CreatePipeline()
    {
        return new GenericRenderPipeline(
            ClearColor,
            m_AssetDatabase ?? throw new InvalidOperationException("[GenericRP] Asset database service was not assigned."),
            m_MaterialLibrary ?? throw new InvalidOperationException("[GenericRP] Material library service was not assigned."),
            m_DisposalQueue ?? throw new InvalidOperationException("[GenericRP] Deferred disposal queue was not assigned."));
    }
}
