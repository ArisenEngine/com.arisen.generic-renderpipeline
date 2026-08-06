using ArisenKernel.Packages;
using ArisenKernel.Services;
using ArisenKernel.Diagnostics;
using ArisenEngine.Core.Assets;
using ArisenEngine.Rendering.Resources;
using ArisenEngine.Resources.Serialization;

namespace ArisenEngine.Rendering;

/// <summary>
/// Entry point for the Generic Render Pipeline package.
/// </summary>
public class GenericRenderPipelinePackage : IPackageEntry
{
    private GenericRenderPipelineProvider? m_Provider;
    private GenericRenderMaterialLibrary? m_MaterialLibrary;
    private DeferredRenderResourceDisposalQueue? m_DisposalQueue;
    private GenericPreparedAssetProvider? m_PreparedAssetProvider;
    private GenericRenderPipelineFeatureRegistry? m_FeatureRegistry;
    private GenericRenderPipelineRuntimeShaderRegistry? m_RuntimeShaderRegistry;
    private IRuntimeAssetResidencyService? m_ResidencyService;

    public void OnLoad(IServiceRegistry registry)
    {
        KernelLog.Info("[GenericRP] Registering render-pipeline provider...");

        var assetDatabase = registry.GetService<IAssetDatabase>();
        m_RuntimeShaderRegistry = new GenericRenderPipelineRuntimeShaderRegistry();
        registry.RegisterService<IGenericRenderPipelineRuntimeShaderRegistry>(
            m_RuntimeShaderRegistry);
        registry.GetService<IRuntimeAssetCookerRegistry>().RegisterCooker(
            new GenericRenderPipelineRuntimeAssetCooker(
                assetDatabase,
                registry.GetService<IRuntimeShaderCookRecipeRegistry>(),
                GenericRenderPipelineAssetRefs.StandardLitMaterial.Ref,
                GenericRenderPipelineAssetRefs.FacetedCrystalMesh.Ref,
                GenericRenderPipelineShaderAssets.CreateRuntimeShaders(),
                m_RuntimeShaderRegistry));
        m_DisposalQueue = new DeferredRenderResourceDisposalQueue();
        m_MaterialLibrary = new GenericRenderMaterialLibrary(assetDatabase, m_DisposalQueue);
        m_MaterialLibrary.RegisterDefaultMaterial(GenericRenderPipelineAssetRefs.StandardLitMaterial.Ref);
        registry.RegisterService<IRenderMaterialLibrary>(m_MaterialLibrary);
        registry.RegisterService<IRHITexture2DResourceCache>(
            m_MaterialLibrary.TextureResourceCache);
        m_FeatureRegistry = new GenericRenderPipelineFeatureRegistry();
        registry.RegisterService<IGenericRenderPipelineFeatureRegistry>(m_FeatureRegistry);
        m_ResidencyService = registry.GetService<IRuntimeAssetResidencyService>();
        m_PreparedAssetProvider = new GenericPreparedAssetProvider(
            assetDatabase,
            m_MaterialLibrary,
            m_DisposalQueue,
            m_ResidencyService);
        registry.RegisterService<IGenericRenderPipelinePreparedAssetSource>(
            m_PreparedAssetProvider);
        m_ResidencyService.RegisterPreparedProvider(m_PreparedAssetProvider);
        m_Provider = new GenericRenderPipelineProvider(
            assetDatabase,
            m_MaterialLibrary,
            m_DisposalQueue,
            m_PreparedAssetProvider,
            m_FeatureRegistry,
            m_ResidencyService);
        registry.RegisterService<IRenderPipelineProvider>(m_Provider);

        KernelLog.Info("[GenericRP] Provider registered; project selection activates it during RenderSubsystem initialization.");
    }

    public void OnUnload(IServiceRegistry registry)
    {
        m_Provider?.ReleaseDeviceResources();
        m_Provider?.Deactivate();
        m_Provider = null;
        m_ResidencyService?.UnregisterPreparedProvider(GenericPreparedAssetProvider.Id);
        m_ResidencyService = null;
        m_PreparedAssetProvider = null;
        m_MaterialLibrary?.Dispose();
        m_MaterialLibrary = null;
        m_DisposalQueue = null;
        m_FeatureRegistry = null;
        m_RuntimeShaderRegistry = null;
        KernelLog.Info("[GenericRP] Unloaded.");
    }
}
