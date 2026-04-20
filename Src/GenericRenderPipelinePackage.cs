using ArisenKernel.Packages;
using ArisenKernel.Services;
using ArisenKernel.Diagnostics;

namespace ArisenEngine.Rendering;

/// <summary>
/// Entry point for the Generic Render Pipeline package.
/// </summary>
public class GenericRenderPipelinePackage : IPackageEntry
{
    private GenericRenderPipelineAsset? m_DefaultAsset;

    public void OnLoad(IServiceRegistry registry)
    {
        KernelLog.Info("[GenericRP] Initializing default render pipeline asset...");

        // For development, we auto-instantiate the asset if one isn't already assigned.
        // In the future, this will be loaded from the ProjectSettings asset via AssetDatabase.
        m_DefaultAsset = new GenericRenderPipelineAsset();
        
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
        KernelLog.Info("[GenericRP] Unloaded.");
    }
}
