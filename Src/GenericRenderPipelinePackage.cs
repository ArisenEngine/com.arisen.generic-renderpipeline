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
    private GenericRenderPipelineAsset? m_DefaultAsset;
    private GenericRenderMaterialLibrary? m_MaterialLibrary;
    private DeferredRenderResourceDisposalQueue? m_DisposalQueue;

    public void OnLoad(IServiceRegistry registry)
    {
        KernelLog.Info("[GenericRP] Initializing default render pipeline asset...");

        var assetDatabase = registry.GetService<IAssetDatabase>();
        m_DisposalQueue = new DeferredRenderResourceDisposalQueue();
        m_MaterialLibrary = new GenericRenderMaterialLibrary(assetDatabase, m_DisposalQueue);
        m_MaterialLibrary.RegisterDefaultMaterial(GenericRenderPipelineAssetRefs.SmokeMaterial.Ref);
        registry.RegisterService<IRenderMaterialLibrary>(m_MaterialLibrary);

        // For development, we auto-instantiate the asset if one isn't already assigned.
        // In the future, this will be loaded from the ProjectSettings asset via AssetDatabase.
        m_DefaultAsset = new GenericRenderPipelineAsset(assetDatabase, m_MaterialLibrary, m_DisposalQueue);
        
        // Instrumented check: This will appear in the Console/Terminal even if redirection is delayed
        Console.WriteLine($"[DEBUG] GenericRP Loading - ClearColor from Asset: {m_DefaultAsset.ClearColor}");

        // This effectively "turns on" the rendering logic for the project.
        Graphics.SetCurrentRenderPipeline(m_DefaultAsset);
        
        KernelLog.Info("[GenericRP] Default pipeline asset assigned to Graphics.");
    }

    public void OnUnload(IServiceRegistry registry)
    {
        if (Graphics.CurrentRenderPipelineAsset == m_DefaultAsset)
        {
            Graphics.SetCurrentRenderPipeline(null);
        }
        m_DefaultAsset = null;
        m_MaterialLibrary?.Dispose();
        m_MaterialLibrary = null;
        m_DisposalQueue = null;
        KernelLog.Info("[GenericRP] Unloaded.");
    }
}
