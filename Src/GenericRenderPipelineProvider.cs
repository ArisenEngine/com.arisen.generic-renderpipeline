using ArisenEngine.Core.Assets;
using ArisenKernel.Diagnostics;
using ArisenKernel.Packages;

namespace ArisenEngine.Rendering;

public sealed class GenericRenderPipelineProvider : IRenderPipelineProvider
{
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly GenericRenderMaterialLibrary m_MaterialLibrary;
    private readonly DeferredRenderResourceDisposalQueue m_DisposalQueue;
    private GenericRenderPipelineAsset? m_ActiveAsset;

    public GenericRenderPipelineProvider(
        IAssetDatabase assetDatabase,
        GenericRenderMaterialLibrary materialLibrary,
        DeferredRenderResourceDisposalQueue disposalQueue)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        m_MaterialLibrary = materialLibrary ?? throw new ArgumentNullException(nameof(materialLibrary));
        m_DisposalQueue = disposalQueue ?? throw new ArgumentNullException(nameof(disposalQueue));
    }

    public string ProviderPackageId => GenericRenderPipelineSettingsLoader.ProviderPackageId;

    public string SettingsAssetType => GenericRenderPipelineSettingsLoader.AssetType;

    public void Activate(ProjectAssetReference settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsValid)
        {
            throw new InvalidOperationException(
                "[GenericRP] Pipeline activation requires a valid settings Guid and PackageId.");
        }

        var settingsRef = new AssetRef<RenderPipelineSettingsSourceAsset>(
            settings.Guid,
            SettingsAssetType,
            settings.PackageId);
        var loadedSettings = GenericRenderPipelineSettingsLoader.LoadSource(
            m_AssetDatabase,
            settingsRef);

        Deactivate();
        m_ActiveAsset = new GenericRenderPipelineAsset(
            loadedSettings,
            m_AssetDatabase,
            m_MaterialLibrary,
            m_DisposalQueue);
        Graphics.SetCurrentRenderPipeline(m_ActiveAsset);
        KernelLog.InfoFormat(
            "[GenericRP] Activated settings '{0}' ({1}) | Shadow: {2}, {3}x{3}, PCF radius {4}.",
            loadedSettings.Name,
            settings.Guid,
            loadedSettings.Shadows.Enabled ? "enabled" : "disabled",
            loadedSettings.Shadows.MapSize,
            loadedSettings.Shadows.PcfRadius);
    }

    public void Deactivate()
    {
        if (m_ActiveAsset != null &&
            ReferenceEquals(Graphics.CurrentRenderPipelineAsset, m_ActiveAsset))
        {
            Graphics.SetCurrentRenderPipeline(null);
        }

        m_ActiveAsset = null;
    }
}
