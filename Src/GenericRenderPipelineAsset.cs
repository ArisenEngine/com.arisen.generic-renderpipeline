using ArisenEngine.Core.Assets;

using ArisenKernel.Packages;

namespace ArisenEngine.Rendering;

[ArisenPackage("com.arisen.generic-renderpipeline")]
public class GenericRenderPipelineAsset : RenderPipelineAsset
{
    private readonly GenericRenderPipelineSettings m_Settings = GenericRenderPipelineSettings.Default;
    private readonly IAssetDatabase? m_AssetDatabase;
    private readonly GenericRenderMaterialLibrary? m_MaterialLibrary;
    private readonly DeferredRenderResourceDisposalQueue? m_DisposalQueue;
    private readonly GenericPreparedAssetProvider? m_PreparedAssetProvider;
    private readonly IGenericRenderPipelineFeature[] m_Features =
        Array.Empty<IGenericRenderPipelineFeature>();

    public GenericRenderPipelineAsset()
    {
    }

    public GenericRenderPipelineAsset(
        GenericRenderPipelineSettings settings,
        IAssetDatabase assetDatabase,
        GenericRenderMaterialLibrary materialLibrary,
        DeferredRenderResourceDisposalQueue disposalQueue,
        GenericPreparedAssetProvider preparedAssetProvider,
        IGenericRenderPipelineFeature[] features)
    {
        m_Settings = settings;
        m_AssetDatabase = assetDatabase;
        m_MaterialLibrary = materialLibrary;
        m_DisposalQueue = disposalQueue;
        m_PreparedAssetProvider = preparedAssetProvider;
        ArgumentNullException.ThrowIfNull(features);
        m_Features = features.Length == 0
            ? Array.Empty<IGenericRenderPipelineFeature>()
            : (IGenericRenderPipelineFeature[])features.Clone();
    }

    protected override void AfterDeserialize()
    {
        IsDirty = true;
    }

    protected override void BeforeSerialize() { }

    protected override RenderPipeline CreatePipeline()
    {
        return new GenericRenderPipeline(
            m_Settings,
            m_AssetDatabase ?? throw new InvalidOperationException("[GenericRP] Asset database service was not assigned."),
            m_MaterialLibrary ?? throw new InvalidOperationException("[GenericRP] Material library service was not assigned."),
            m_DisposalQueue ?? throw new InvalidOperationException("[GenericRP] Deferred disposal queue was not assigned."),
            m_PreparedAssetProvider ?? throw new InvalidOperationException("[GenericRP] Prepared asset provider was not assigned."),
            m_Features);
    }
}
