using ArisenKernel.Packages;
using ArisenKernel.Services;
using ArisenKernel.Diagnostics;
using ArisenEngine.Core.Assets;

namespace ArisenEngine.Rendering;

/// <summary>
/// Entry point for the Generic Render Pipeline package.
/// </summary>
public class GenericRenderPipelinePackage : IPackageEntry
{
    private GenericRenderPipelineProvider? m_Provider;
    private GenericRenderMaterialLibrary? m_MaterialLibrary;
    private DeferredRenderResourceDisposalQueue? m_DisposalQueue;

    public void OnLoad(IServiceRegistry registry)
    {
        KernelLog.Info("[GenericRP] Registering render-pipeline provider...");

        var assetDatabase = registry.GetService<IAssetDatabase>();
        m_DisposalQueue = new DeferredRenderResourceDisposalQueue();
        m_MaterialLibrary = new GenericRenderMaterialLibrary(assetDatabase, m_DisposalQueue);
        m_MaterialLibrary.RegisterDefaultMaterial(GenericRenderPipelineAssetRefs.StandardLitMaterial.Ref);
        registry.RegisterService<IRenderMaterialLibrary>(m_MaterialLibrary);
        m_Provider = new GenericRenderPipelineProvider(
            assetDatabase,
            m_MaterialLibrary,
            m_DisposalQueue);
        registry.RegisterService<IRenderPipelineProvider>(m_Provider);

        KernelLog.Info("[GenericRP] Provider registered; project selection activates it during RenderSubsystem initialization.");
    }

    public void OnUnload(IServiceRegistry registry)
    {
        m_Provider?.Deactivate();
        m_Provider = null;
        m_MaterialLibrary?.Dispose();
        m_MaterialLibrary = null;
        m_DisposalQueue = null;
        KernelLog.Info("[GenericRP] Unloaded.");
    }
}
