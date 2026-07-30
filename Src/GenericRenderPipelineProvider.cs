using ArisenEngine.Core.Assets;
using ArisenEngine.Resources.Serialization;
using ArisenKernel.Diagnostics;
using ArisenKernel.Packages;

namespace ArisenEngine.Rendering;

public sealed class GenericRenderPipelineProvider : IRenderPipelineProvider
{
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly GenericRenderMaterialLibrary m_MaterialLibrary;
    private readonly DeferredRenderResourceDisposalQueue m_DisposalQueue;
    private readonly GenericPreparedAssetProvider m_PreparedAssetProvider;
    private readonly GenericRenderPipelineFeatureRegistry m_FeatureRegistry;
    private readonly IRuntimeAssetResidencyService m_ResidencyService;
    private IGenericRenderPipelineFeature[] m_ActiveFeatures =
        Array.Empty<IGenericRenderPipelineFeature>();
    private GenericRenderPipelineAsset? m_ActiveAsset;

    internal GenericRenderPipelineProvider(
        IAssetDatabase assetDatabase,
        GenericRenderMaterialLibrary materialLibrary,
        DeferredRenderResourceDisposalQueue disposalQueue,
        GenericPreparedAssetProvider preparedAssetProvider,
        GenericRenderPipelineFeatureRegistry featureRegistry,
        IRuntimeAssetResidencyService residencyService)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        m_MaterialLibrary = materialLibrary ?? throw new ArgumentNullException(nameof(materialLibrary));
        m_DisposalQueue = disposalQueue ?? throw new ArgumentNullException(nameof(disposalQueue));
        m_PreparedAssetProvider = preparedAssetProvider
            ?? throw new ArgumentNullException(nameof(preparedAssetProvider));
        m_FeatureRegistry = featureRegistry
            ?? throw new ArgumentNullException(nameof(featureRegistry));
        m_ResidencyService = residencyService
            ?? throw new ArgumentNullException(nameof(residencyService));
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
        var loadedSettings = GenericRenderPipelineSettingsLoader.Load(
            m_AssetDatabase,
            settingsRef);

        Deactivate();
        var activeFeatures = m_FeatureRegistry.BeginPipelineActivation();
        try
        {
            m_ActiveAsset = new GenericRenderPipelineAsset(
                loadedSettings,
                m_AssetDatabase,
                m_MaterialLibrary,
                m_DisposalQueue,
                m_PreparedAssetProvider,
                activeFeatures);
            m_ActiveFeatures = activeFeatures;
            Graphics.SetCurrentRenderPipeline(m_ActiveAsset);
            KernelLog.InfoFormat(
                "[GenericRP] Activated settings '{0}' ({1}) | Shadow: {2}, {3}x{3}, PCF radius {4} | Features: {5}.",
                loadedSettings.Name,
                settings.Guid,
                loadedSettings.Shadows.Enabled ? "enabled" : "disabled",
                loadedSettings.Shadows.MapSize,
                loadedSettings.Shadows.PcfRadius,
                activeFeatures.Length);
        }
        catch
        {
            m_ActiveAsset = null;
            m_ActiveFeatures = Array.Empty<IGenericRenderPipelineFeature>();
            m_FeatureRegistry.EndPipelineActivation();
            throw;
        }
    }

    public void Deactivate()
    {
        if (m_ActiveAsset != null &&
            ReferenceEquals(Graphics.CurrentRenderPipelineAsset, m_ActiveAsset))
        {
            Graphics.SetCurrentRenderPipeline(null);
        }

        m_ActiveAsset = null;
        m_ActiveFeatures = Array.Empty<IGenericRenderPipelineFeature>();
        m_FeatureRegistry.EndPipelineActivation();
    }

    public void ReleaseDeviceResources()
    {
        GenericRenderPipelineFeatureDispatcher.ReleaseDeviceResources(m_ActiveFeatures);
        m_ResidencyService.InvalidatePreparedProvider(
            GenericPreparedAssetProvider.Id,
            "Generic RP prepared resources are waiting for the active RHI device.");
        m_PreparedAssetProvider.ReleaseAllDeviceResources();
    }
}
