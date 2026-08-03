using Arisen.Native.RHI;
using ArisenEngine.Core.Math;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.RHI;
using ArisenEngine.Rendering.Resources;
using ArisenEngine.Threading;
using ArisenKernel.Diagnostics;
using System.IO;
using System.Numerics;

namespace ArisenEngine.Rendering;

public class GenericRenderPipeline : RenderPipeline
{
    private const int MaximumDirectionalShadowDrawCommands = 65536;

    private readonly GenericRenderPipelineSettings m_Settings;
    private readonly Color m_ClearColor;
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly GenericRenderMaterialLibrary m_MaterialLibrary;
    private readonly DirectionalShadowPass m_DirectionalShadowPass;
    private readonly DirectionalShadowFrameBuffer m_DirectionalShadowFrameBuffer = new();
    private readonly EnvironmentFrameBuffer m_EnvironmentFrameBuffer = new();
    private readonly EnvironmentSkyPass m_EnvironmentSkyPass;
    private readonly OutdoorAtmospherePass m_OutdoorAtmospherePass;
    private readonly StaticMeshPass m_StaticMeshPass;
    private readonly StaticMeshPass m_TransparentStaticMeshPass;
    private readonly TonemapPass m_TonemapPass;
    private readonly RenderResourceReloadQueue m_ReloadQueue = new();
    private readonly DeferredRenderResourceDisposalQueue m_DisposalQueue;
    private readonly GenericPreparedAssetProvider m_PreparedAssetProvider;
    private readonly IGenericRenderPipelineFeature[] m_Features;
    private readonly Dictionary<Guid, RHIStaticMeshResource> m_SceneMeshes = new();
    private RHIEnvironmentTextureResource? m_EnvironmentTexture;
    private RHIEnvironmentLightingResource? m_EnvironmentLighting;
    private Guid m_FailedEnvironmentTextureGuid;
    private AssetDependencyStamp m_FailedEnvironmentTextureStamp = AssetDependencyStamp.Empty;
    private Guid m_FailedEnvironmentLightingGuid;
    private AssetDependencyStamp m_FailedEnvironmentLightingStamp = AssetDependencyStamp.Empty;
    private readonly Matrix4x4 m_FallbackStaticMeshLocalToWorld = Matrix4x4.CreateScale(1.24f, 1.24f, 1.0f);
    private MeshDrawCommand[] m_SceneDrawCommands = Array.Empty<MeshDrawCommand>();
    private MeshDrawCommand[] m_DepthWritingDrawCommands = Array.Empty<MeshDrawCommand>();
    private MeshDrawCommand[] m_TransparentDrawCommands = Array.Empty<MeshDrawCommand>();
    private TransparentDrawSortKey[] m_TransparentDrawSortKeys = Array.Empty<TransparentDrawSortKey>();
    private MeshDrawCommand[] m_ShadowDrawCommands = Array.Empty<MeshDrawCommand>();
    private int m_SceneDrawCommandCount;
    private int m_DepthWritingDrawCommandCount;
    private int m_TransparentDrawCommandCount;
    private int m_OpaqueDrawCommandCount;
    private int m_AlphaTestDrawCommandCount;
    private int m_SkippedAlphaDrawCommandCount;
    private int m_ShadowDrawCommandCount;
    private int m_DroppedShadowDrawCommandCount;
    private StaticMeshCullingStats m_StaticMeshCullingStats;
    private StaticMeshCullingStats m_ShadowCasterCullingStats;
    private DirectionalShadowBoundsAccumulator m_ShadowReceiverBounds;
    private DirectionalShadowCascadeDrawRangeSet m_ShadowDrawRanges;
    private DirectionalShadowFrameData m_DirectionalShadowFrameData;
    private EnvironmentFrameData m_EnvironmentFrameData;
    private RHIDevice m_LastDevice;
    private ulong m_DeviceGeneration;
    private ulong m_LastSubmittedTicket;
    private RHIStaticMeshResource? m_FallbackMesh;
    private GenericRenderPipelineFeatureFrameContext m_CurrentFeatureFrameContext;

    public GenericRenderPipeline(
        GenericRenderPipelineSettings settings,
        IAssetDatabase assetDatabase,
        GenericRenderMaterialLibrary materialLibrary,
        DeferredRenderResourceDisposalQueue disposalQueue,
        GenericPreparedAssetProvider preparedAssetProvider,
        IGenericRenderPipelineFeature[] features)
    {
        m_Settings = settings;
        m_ClearColor = settings.FallbackClearColor;
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        m_MaterialLibrary = materialLibrary ?? throw new ArgumentNullException(nameof(materialLibrary));
        m_DisposalQueue = disposalQueue ?? throw new ArgumentNullException(nameof(disposalQueue));
        m_PreparedAssetProvider = preparedAssetProvider
            ?? throw new ArgumentNullException(nameof(preparedAssetProvider));
        ArgumentNullException.ThrowIfNull(features);
        m_Features = features.Length == 0
            ? Array.Empty<IGenericRenderPipelineFeature>()
            : (IGenericRenderPipelineFeature[])features.Clone();
        m_DirectionalShadowPass = new DirectionalShadowPass(assetDatabase, materialLibrary);
        m_EnvironmentSkyPass = new EnvironmentSkyPass(assetDatabase);
        m_OutdoorAtmospherePass = new OutdoorAtmospherePass(assetDatabase);
        m_StaticMeshPass = new StaticMeshPass(assetDatabase, "GenericStaticMeshPass");
        m_TransparentStaticMeshPass = new StaticMeshPass(
            assetDatabase,
            "GenericTransparentStaticMeshPass",
            StaticMeshPassConfiguration.Transparent);
        m_TonemapPass = new TonemapPass(assetDatabase);
        m_AssetDatabase.AssetChanged += OnAssetChanged;
    }

    protected override RenderGraph CreateRenderGraph(ITaskGraph taskGraph)
    {
        return new RenderGraph(taskGraph, m_DisposalQueue);
    }

    protected override void SetupGraph(RenderGraph graph, RenderContext context)
    {
        EnsureSmokeAssetsLoaded(context);
        var directionalLight = GetPrimaryDirectionalLight(context);
        var sceneEnvironment = GetSceneEnvironment(context, directionalLight);
        var environmentTexture = EnsureEnvironmentTexture(context, sceneEnvironment);
        var environmentLighting = EnsureEnvironmentLighting(context, environmentTexture);
        var outdoorProfile = environmentTexture?.OutdoorProfile ?? OutdoorEnvironmentProfile.Disabled;
        bool atmosphereEnabled = outdoorProfile.IsAtmosphereEnabled;
        var sceneColorTexture = graph.CreateTransientTexture(
            context,
            "SceneColor",
            RenderGraphTextureDescriptor.ColorAttachmentSampled2D(
                "GenericSceneColorHDR",
                context.Width,
                context.Height,
                EFormat.FORMAT_R16G16B16A16_SFLOAT));
        var frameDepthDescriptor = atmosphereEnabled
            ? RenderGraphTextureDescriptor.DepthAttachmentSampled2D(
                "GenericFrameDepth",
                context.Width,
                context.Height,
                EFormat.FORMAT_D32_SFLOAT)
            : RenderGraphTextureDescriptor.DepthAttachment2D(
                "GenericFrameDepth",
                context.Width,
                context.Height,
                EFormat.FORMAT_D32_SFLOAT);
        if (IsVisualSummaryEnabled)
        {
            frameDepthDescriptor = frameDepthDescriptor.WithAdditionalUsage(
                EImageUsageFlagBits.IMAGE_USAGE_TRANSFER_SRC_BIT);
        }

        var frameDepthTexture = graph.CreateTransientTexture(
            context,
            "FrameDepth",
            frameDepthDescriptor);
        PublishFrameDepth(frameDepthTexture);
        var directionalShadowTexture = graph.CreateTransientTexture(
            context,
            "DirectionalShadowMap",
            RenderGraphTextureDescriptor.DepthAttachmentSampled2DArray(
                "GenericDirectionalShadowMap",
                m_Settings.Shadows.MapSize,
                m_Settings.Shadows.MapSize,
                EFormat.FORMAT_D32_SFLOAT,
                checked((uint)m_Settings.Shadows.CascadeCount)));
        var sceneColorResource = sceneColorTexture.Resource;
        var frameDepthResource = frameDepthTexture.Resource;
        var shadowMapResource = directionalShadowTexture.Resource;
        var fallbackMaterial = m_MaterialLibrary.GetPreparedMaterial(m_MaterialLibrary.DefaultMaterialID);
        var preparedDraws = GetPreparedDraws(context);
        var cameraPosition = GetCameraPosition(context);
        var viewProjection = GetViewProjection(context);
        PrepareCameraDrawQueues(preparedDraws, GetViewMatrix(context));
        var depthWritingDraws = new ReadOnlySpan<MeshDrawCommand>(
            m_DepthWritingDrawCommands,
            0,
            m_DepthWritingDrawCommandCount);
        var transparentDraws = new ReadOnlySpan<MeshDrawCommand>(
            m_TransparentDrawCommands,
            0,
            m_TransparentDrawCommandCount);
        Camera shadowCamera = GetShadowCamera(context);
        DirectionalShadowCascadeSet shadowCascades = DirectionalShadowFitter.CreateCascades(
            shadowCamera,
            directionalLight.Direction,
            m_Settings.Shadows);
        ReadOnlySpan<MeshDrawCommand> preparedShadowDraws;
        if (m_Settings.Shadows.Enabled)
        {
            preparedShadowDraws = PrepareShadowDrawCommands(
                context.Device,
                context.StaticMeshItems,
                depthWritingDraws,
                shadowCascades);
        }
        else
        {
            ResetShadowDrawCommands(shadowCascades.Count);
            preparedShadowDraws = ReadOnlySpan<MeshDrawCommand>.Empty;
        }
        m_DirectionalShadowFrameData = m_DirectionalShadowFrameBuffer.Prepare(
            context,
            shadowCascades,
            directionalShadowTexture,
            m_Settings.Shadows,
            m_Settings.Shadows.Enabled);
        m_EnvironmentFrameData = m_EnvironmentFrameBuffer.Prepare(
            context,
            sceneEnvironment,
            directionalLight,
            environmentTexture,
            frameDepthTexture,
            outdoorProfile,
            DeviceDepthConvention.ForwardZeroToOne);
        m_CurrentFeatureFrameContext = new GenericRenderPipelineFeatureFrameContext(
            context,
            viewProjection,
            cameraPosition,
            directionalLight,
            sceneEnvironment,
            m_DirectionalShadowFrameData,
            IsVisualSummaryEnabled);
        GenericRenderPipelineFeatureDispatcher.ConsumeExtractedFrame(
            m_Features,
            m_CurrentFeatureFrameContext);
        GenericRenderPipelineFeatureDispatcher.PrepareResources(
            m_Features,
            m_CurrentFeatureFrameContext);
        if (IsVisualSummaryEnabled)
        {
            LogShadowValidationFrame(context.FrameIndex, shadowCascades);
        }
        int registeredMaterialCount = m_MaterialLibrary.MaterialCount;
        int preparedMaterialCount = m_MaterialLibrary.PreparedMaterialCount;
        int visibleDrawCommandCount = preparedDraws.Length;
        int shadowDrawCommandCount = preparedShadowDraws.Length;

        m_DirectionalShadowPass.SetDepthTargets(
            directionalShadowTexture,
            shadowCascades.Count);
        m_DirectionalShadowPass.SetCascades(
            shadowCascades,
            m_ShadowDrawRanges,
            m_DirectionalShadowFrameData);
        m_DirectionalShadowPass.SetPreparedDraws(preparedShadowDraws);
        m_DirectionalShadowPass.Prepare(context);

        m_StaticMeshPass.SetStaticMeshResources(fallbackMaterial, m_FallbackMesh!);
        ConfigureStaticMeshPass(
            m_StaticMeshPass,
            context,
            sceneColorTexture,
            frameDepthTexture,
            depthWritingDraws,
            directionalLight,
            sceneEnvironment,
            environmentLighting,
            directionalShadowTexture,
            cameraPosition,
            viewProjection);
        ConfigureStaticMeshPass(
            m_TransparentStaticMeshPass,
            context,
            sceneColorTexture,
            frameDepthTexture,
            transparentDraws,
            directionalLight,
            sceneEnvironment,
            environmentLighting,
            directionalShadowTexture,
            cameraPosition,
            viewProjection);
        m_EnvironmentSkyPass.SetFrameData(m_EnvironmentFrameData, sceneEnvironment);
        m_EnvironmentSkyPass.SetColorTarget(
            sceneColorTexture.ImageView,
            sceneColorTexture.Format);
        m_EnvironmentSkyPass.Prepare(context);
        if (atmosphereEnabled)
        {
            m_OutdoorAtmospherePass.SetFrameData(m_EnvironmentFrameData);
            m_OutdoorAtmospherePass.SetColorTarget(
                sceneColorTexture.ImageView,
                sceneColorTexture.Format);
            m_OutdoorAtmospherePass.Prepare(context);
        }
        if (IsVisualSummaryEnabled)
        {
            LogAtmosphereValidationFrame(
                context.FrameIndex,
                outdoorProfile,
                m_EnvironmentFrameData.EffectiveExposure,
                environmentLighting is { IsValid: true },
                DeviceDepthConvention.ForwardZeroToOne);
        }
        m_TonemapPass.SetSceneColor(
            sceneColorTexture.Image,
            sceneColorTexture.BindlessImageIndex,
            sceneColorTexture.BindlessSamplerIndex);
        m_TonemapPass.SetExposure(m_EnvironmentFrameData.EffectiveExposure);
        m_TonemapPass.Prepare(context);
        var featureGraphContext = new GenericRenderPipelineFeatureGraphContext(
            graph,
            m_CurrentFeatureFrameContext,
            sceneColorTexture,
            frameDepthTexture,
            directionalShadowTexture,
            m_DirectionalShadowFrameData,
            m_Settings.Shadows,
            environmentLighting);

        if (RenderDiagnostics.IsEnabled(RenderDiagnosticCategory.Frame))
        {
            ArisenEngine.Core.Diagnostics.Logger.Log($"[GenericRenderPipeline] SetupGraph | Settings: {m_Settings.Name} | Frame: {context.FrameIndex} | Surface: 0x{context.SurfaceId:X} | Cameras: {context.CameraCount} | DirectionalLights: {context.DirectionalLightCount} | PointLights: {context.PointLightCount} | SpotLights: {context.SpotLightCount} | Environments: {context.SceneEnvironmentCount} | EnvironmentTexture: {(environmentTexture is { IsValid: true } ? environmentTexture.Asset.Name : "ProceduralFallback")} | EnvironmentSkyMode: {outdoorProfile.SkyMode} | Atmosphere: {(atmosphereEnabled ? "Enabled" : "Disabled")} | EffectiveExposure: {m_EnvironmentFrameData.EffectiveExposure:0.###} | EnvironmentIBL: {(environmentLighting is { IsValid: true } ? $"Ready (irr={environmentLighting.IrradianceImageIndex}, spec={environmentLighting.PrefilteredSpecularImageIndex}, brdf={environmentLighting.BrdfIntegrationLutImageIndex})" : "Unavailable")} | Materials: {preparedMaterialCount}/{registeredMaterialCount} prepared | LegacyDraws: {context.DrawListCount} | SceneItems: {m_StaticMeshCullingStats.SourceItemCount} | VisibleItems: {m_StaticMeshCullingStats.VisibleItemCount} | CulledItems: {m_StaticMeshCullingStats.CulledItemCount} | VisibleDrawCommands: {visibleDrawCommandCount} | OpaqueDraws: {m_OpaqueDrawCommandCount} | AlphaTestDraws: {m_AlphaTestDrawCommandCount} | TransparentDraws: {m_TransparentDrawCommandCount} | SkippedAlphaDraws: {m_SkippedAlphaDrawCommandCount} | ShadowCasters: {m_ShadowCasterCullingStats.VisibleItemCount} | ShadowCulled: {m_ShadowCasterCullingStats.CulledItemCount} | ShadowDrawCommands: {shadowDrawCommandCount} | ShadowDropped: {m_DroppedShadowDrawCommandCount} | ShadowCascades: {shadowCascades.Count} | SceneColor: {sceneColorTexture.Width}x{sceneColorTexture.Height} {sceneColorTexture.Format} | FrameDepth: {frameDepthTexture.Width}x{frameDepthTexture.Height} {frameDepthTexture.Format} | ShadowMap: {directionalShadowTexture.Width}x{directionalShadowTexture.Height}x{directionalShadowTexture.ArrayLayers} {directionalShadowTexture.Format} | ClearColorFallback: {m_ClearColor}");
            if (m_DroppedShadowDrawCommandCount > 0)
            {
                ArisenEngine.Core.Diagnostics.Logger.Warning(
                    $"[GenericRenderPipeline] Directional shadow draw budget exhausted | " +
                    $"Capacity: {MaximumDirectionalShadowDrawCommands} | " +
                    $"Dropped: {m_DroppedShadowDrawCommandCount}.");
            }
        }

        Profiler.PlotValue("Render.MaterialCount", registeredMaterialCount);
        Profiler.PlotValue("Render.PreparedMaterialCount", preparedMaterialCount);
        Profiler.PlotValue("Render.LightCount", context.DirectionalLightCount);
        Profiler.PlotValue("Render.PointLightCount", context.PointLightCount);
        Profiler.PlotValue("Render.SpotLightCount", context.SpotLightCount);
        Profiler.PlotValue("Render.EnvironmentCount", context.SceneEnvironmentCount);
        Profiler.PlotValue("Render.EnvironmentTextureEnabled", environmentTexture is { IsValid: true } ? 1 : 0);
        Profiler.PlotValue("Render.EnvironmentIBLEnabled", environmentLighting is { IsValid: true } ? 1 : 0);
        Profiler.PlotValue("Render.EnvironmentSpecularMaxLod", environmentLighting?.PrefilteredSpecularMaxLod ?? 0.0f);
        Profiler.PlotValue("Render.SceneExposure", sceneEnvironment.Exposure);
        Profiler.PlotValue("Render.EnvironmentEffectiveExposure", m_EnvironmentFrameData.EffectiveExposure);
        Profiler.PlotValue("Render.EnvironmentSkyMode", (double)(int)outdoorProfile.SkyMode);
        Profiler.PlotValue("Render.AtmosphereEnabled", atmosphereEnabled ? 1 : 0);
        Profiler.PlotValue("Render.AerialPerspectiveEnabled", outdoorProfile.AerialPerspectiveEnabled ? 1 : 0);
        Profiler.PlotValue("Render.HeightFogEnabled", outdoorProfile.HeightFogEnabled ? 1 : 0);
        Profiler.PlotValue("Render.VisibleDrawCommandCount", visibleDrawCommandCount);
        Profiler.PlotValue("Render.OpaqueDrawCommandCount", m_OpaqueDrawCommandCount);
        Profiler.PlotValue("Render.AlphaTestDrawCommandCount", m_AlphaTestDrawCommandCount);
        Profiler.PlotValue("Render.TransparentDrawCommandCount", m_TransparentDrawCommandCount);
        Profiler.PlotValue("Render.SkippedAlphaDrawCommandCount", m_SkippedAlphaDrawCommandCount);
        Profiler.PlotValue("Render.ShadowReceiverBoundedItemCount", m_ShadowReceiverBounds.Count);
        Profiler.PlotValue("Render.ShadowCasterItemCount", m_ShadowCasterCullingStats.VisibleItemCount);
        Profiler.PlotValue("Render.CulledShadowCasterItemCount", m_ShadowCasterCullingStats.CulledItemCount);
        Profiler.PlotValue("Render.ShadowCasterDrawCommandCount", shadowDrawCommandCount);
        Profiler.PlotValue("Render.ShadowCascadeCount", shadowCascades.Count);
        Profiler.PlotValue("Render.DroppedShadowDrawCommandCount", m_DroppedShadowDrawCommandCount);
        Profiler.PlotValue("Render.SceneColor.Width", sceneColorTexture.Width);
        Profiler.PlotValue("Render.SceneColor.Height", sceneColorTexture.Height);
        Profiler.PlotValue("Render.SceneColor.Format", (double)(uint)sceneColorTexture.Format);
        Profiler.PlotValue("Render.FrameDepth.Width", frameDepthTexture.Width);
        Profiler.PlotValue("Render.FrameDepth.Height", frameDepthTexture.Height);
        Profiler.PlotValue("Render.FrameDepth.Format", (double)(uint)frameDepthTexture.Format);
        Profiler.PlotValue("Render.ShadowMap.Size", directionalShadowTexture.Width);
        Profiler.PlotValue("Render.ShadowMap.Format", (double)(uint)directionalShadowTexture.Format);
        Profiler.PlotValue("Render.ShadowMap.Enabled", m_DirectionalShadowPass.HasRenderableShadowMap ? 1 : 0);
        Profiler.PlotValue("Render.ShadowMap.DepthBias", m_Settings.Shadows.DepthBias);
        Profiler.PlotValue("Render.ShadowMap.SlopeBias", m_Settings.Shadows.SlopeBias);
        Profiler.PlotValue("Render.ShadowMap.PcfRadius", m_Settings.Shadows.PcfRadius);

        // 1. Directional shadow depth is rendered before lighting samples it.
        graph.AddPass(
            m_DirectionalShadowPass,
            builder => builder.WriteDepthAttachment(
                shadowMapResource,
                RenderAttachmentIntent.ClearStore));
        GenericRenderPipelineFeatureDispatcher.AddRenderGraphPasses(
            m_Features,
            GenericRenderPipelineFeatureGraphStage.DirectionalShadow,
            featureGraphContext);

        // 2. The environment sky initializes and fills the HDR scene color target.
        graph.AddPass(
            m_EnvironmentSkyPass,
            builder => builder.WriteColorAttachment(
                sceneColorResource,
                RenderAttachmentIntent.ClearStore));

        // 3. Static mesh rendering preserves linear HDR lighting in scene color.
        m_StaticMeshPass.Prepare(context);
        graph.AddPass(
            m_StaticMeshPass,
            builder => builder
                .ReadShader(shadowMapResource)
                .ReadWriteColorAttachment(
                    sceneColorResource,
                    RenderAttachmentIntent.LoadStore)
                .ReadWriteDepthAttachment(
                    frameDepthResource,
                    RenderAttachmentIntent.ClearThenLoadStore));
        GenericRenderPipelineFeatureDispatcher.AddRenderGraphPasses(
            m_Features,
            GenericRenderPipelineFeatureGraphStage.Opaque,
            featureGraphContext);

        // 4. Transparent meshes preserve sorted order, test opaque depth, and never write it.
        m_TransparentStaticMeshPass.Prepare(context);
        graph.AddPass(
            m_TransparentStaticMeshPass,
            builder => builder
                .ReadShader(shadowMapResource)
                .ReadWriteColorAttachment(
                    sceneColorResource,
                    RenderAttachmentIntent.LoadStore)
                .ReadDepthAttachment(
                    frameDepthResource,
                    RenderAttachmentIntent.ReadOnlyLoadStore));

        // 5. Optional atmosphere samples graph-owned depth and blends into HDR scene color.
        if (atmosphereEnabled)
        {
            graph.AddPass(
                m_OutdoorAtmospherePass,
                builder => builder
                    .ReadShader(frameDepthResource)
                    .ReadWriteColorAttachment(
                        sceneColorResource,
                        RenderAttachmentIntent.LoadStore));
        }

        // 6. Tonemap scene color into the active presentation target.
        graph.AddPass(
            m_TonemapPass,
            builder => builder
                .ReadShader(sceneColorResource)
                .WriteColorAttachment(
                    graph.FrameColor,
                    RenderAttachmentIntent.ClearStore));
    }

    private void ConfigureStaticMeshPass(
        StaticMeshPass pass,
        RenderContext context,
        RenderGraphTexture sceneColorTexture,
        RenderGraphTexture frameDepthTexture,
        ReadOnlySpan<MeshDrawCommand> preparedDraws,
        DirectionalLight directionalLight,
        SceneEnvironment sceneEnvironment,
        RHIEnvironmentLightingResource? environmentLighting,
        RenderGraphTexture directionalShadowTexture,
        Vector3 cameraPosition,
        Matrix4x4 viewProjection)
    {
        pass.SetColorTarget(sceneColorTexture.ImageView, sceneColorTexture.Format);
        pass.SetDepthTarget(
            frameDepthTexture.ImageView,
            frameDepthTexture.Format,
            frameDepthTexture.Width,
            frameDepthTexture.Height);
        m_MaterialLibrary.ApplyMaterialSlots(pass);
        pass.SetPreparedDraws(preparedDraws);
        pass.SetViewProjection(viewProjection);
        pass.SetCameraPosition(cameraPosition);
        pass.SetPointLights(context.PointLights);
        pass.SetSpotLights(context.SpotLights);
        pass.SetDirectionalShadow(
            m_DirectionalShadowFrameData,
            m_Settings.Shadows.Enabled && m_DirectionalShadowPass.HasRenderableShadowMap);
        pass.SetDirectionalLight(directionalLight);
        pass.SetSceneEnvironment(sceneEnvironment);
        pass.SetEnvironmentLighting(environmentLighting);
        pass.SetFallbackLocalToWorld(m_FallbackStaticMeshLocalToWorld);
    }

    protected override void OnDisposed()
    {
        m_AssetDatabase.AssetChanged -= OnAssetChanged;
        WaitForLastSubmittedFrame();
        GenericRenderPipelineFeatureDispatcher.ReleaseDeviceResources(m_Features);
        if (m_LastDevice.IsValid)
        {
            m_DisposalQueue.Drain(
                m_LastDevice,
                m_DeviceGeneration,
                m_LastSubmittedTicket);
        }
        else if (m_DisposalQueue.PendingCount != 0)
        {
            throw new InvalidOperationException(
                $"GenericRP has {m_DisposalQueue.PendingCount} deferred resources without a bound RHI device.");
        }
        m_DirectionalShadowPass.Dispose();
        m_DirectionalShadowFrameBuffer.Dispose();
        m_EnvironmentFrameBuffer.Dispose();
        m_EnvironmentSkyPass.Dispose();
        m_OutdoorAtmospherePass.Dispose();
        m_TransparentStaticMeshPass.Dispose();
        m_StaticMeshPass.Dispose();
        m_TonemapPass.Dispose();
        m_MaterialLibrary.ReleasePreparedResources();
        ReleaseSceneMeshes(disposeImmediately: true);
        m_EnvironmentLighting?.Dispose();
        m_EnvironmentLighting = null;
        m_EnvironmentTexture?.Dispose();
        m_EnvironmentTexture = null;
        m_FallbackMesh?.Dispose();
        m_FallbackMesh = null;
    }

    protected override void OnFrameSubmitted(RenderContext context, ulong submittedTicket)
    {
        m_LastSubmittedTicket = submittedTicket;
        m_PreparedAssetProvider.UpdateSubmittedTicket(submittedTicket);
        m_DisposalQueue.ReleaseCompleted(
            context.Device,
            context.DeviceGeneration);
        var featureSubmissionContext = new GenericRenderPipelineFeatureSubmissionContext(
            m_CurrentFeatureFrameContext,
            submittedTicket);
        GenericRenderPipelineFeatureDispatcher.OnFrameSubmitted(
            m_Features,
            featureSubmissionContext);
    }

    private void OnAssetChanged(AssetChangeEvent change)
    {
        m_ReloadQueue.MarkDirty(change);
    }

    private void EnsureSmokeAssetsLoaded(RenderContext context)
    {
        var device = context.Device;
        m_LastDevice = device;
        m_DeviceGeneration = context.DeviceGeneration;
        m_DisposalQueue.BindDevice(device, m_DeviceGeneration);
        m_LastSubmittedTicket = m_PreparedAssetProvider.UpdateFrameContext(
            device,
            m_DeviceGeneration,
            m_LastSubmittedTicket);

        var dirtyGuids = m_ReloadQueue.Drain();
        if (dirtyGuids.Length > 0)
        {
            ApplyAssetInvalidations(dirtyGuids);
        }

        RegisterSceneMaterials(context.StaticMeshItems);
        m_MaterialLibrary.EnsurePrepared(device, m_LastSubmittedTicket);

        if (m_FallbackMesh is { IsValid: true } && m_FallbackMesh.IsSourceStale())
        {
            ArisenEngine.Core.Diagnostics.Logger.Log("[GenericRenderPipeline] Fallback mesh dependency changed; reloading GPU mesh.");
            m_DisposalQueue.Enqueue(m_FallbackMesh, m_LastSubmittedTicket);
            m_FallbackMesh = null;
        }

        if (m_FallbackMesh is not { IsValid: true })
        {
            var fallbackMesh = new MeshAsset(
                GenericRenderPipelineAssetRefs.FacetedCrystalMesh.Ref.Guid,
                "GenericRP/FacetedCrystal",
                MeshVariantKey.Default,
                MeshSourceFormat.WavefrontObj);
            m_FallbackMesh = new RHIStaticMeshResource(device, m_AssetDatabase, fallbackMesh);
            ArisenEngine.Core.Diagnostics.Logger.Log(
                $"[GenericRenderPipeline] Loaded GPU mesh asset | Name: {fallbackMesh.Name} | Vertices: {m_FallbackMesh.VertexCount} | Indices: {m_FallbackMesh.IndexCount}");
        }

        PrepareSceneDrawCommands(
            device,
            context.StaticMeshItems,
            GetViewProjection(context),
            context.CameraCount > 0);
    }

    private void WaitForLastSubmittedFrame()
    {
        if (m_LastDevice.IsValid && m_LastSubmittedTicket != 0)
        {
            m_LastDevice.WaitQueueTicket(m_LastSubmittedTicket);
        }
    }

    private void ApplyAssetInvalidations(ReadOnlySpan<Guid> dirtyGuids)
    {
        m_PreparedAssetProvider.InvalidateByAssetGuids(dirtyGuids);
        m_MaterialLibrary.InvalidateByAssetGuids(dirtyGuids, m_LastSubmittedTicket);
        m_FailedEnvironmentTextureGuid = Guid.Empty;
        m_FailedEnvironmentTextureStamp = AssetDependencyStamp.Empty;
        m_FailedEnvironmentLightingGuid = Guid.Empty;
        m_FailedEnvironmentLightingStamp = AssetDependencyStamp.Empty;

        if (m_EnvironmentTexture is { IsValid: true } environmentTexture &&
            (ContainsGuid(dirtyGuids, environmentTexture.Asset.Guid) ||
             ContainsGuid(dirtyGuids, environmentTexture.Asset.SourceTexture.Guid)))
        {
            ArisenEngine.Core.Diagnostics.Logger.Log(
                $"[GenericRenderPipeline] Asset change invalidated environment texture {environmentTexture.Asset.Guid}; releasing GPU texture.");
            ReleaseEnvironmentTexture();
        }

        if (m_FallbackMesh is { IsValid: true } &&
            ContainsGuid(dirtyGuids, GenericRenderPipelineAssetRefs.FacetedCrystalMesh.Ref.Guid))
        {
            ArisenEngine.Core.Diagnostics.Logger.Log("[GenericRenderPipeline] Asset change invalidated fallback mesh; releasing GPU mesh.");
            m_DisposalQueue.Enqueue(m_FallbackMesh, m_LastSubmittedTicket);
            m_FallbackMesh = null;
        }

        foreach (var dirtyGuid in dirtyGuids)
        {
            if (m_SceneMeshes.Remove(dirtyGuid, out var mesh))
            {
                ArisenEngine.Core.Diagnostics.Logger.Log($"[GenericRenderPipeline] Asset change invalidated scene mesh {dirtyGuid}; releasing GPU mesh.");
                m_DisposalQueue.Enqueue(mesh, m_LastSubmittedTicket);
            }
        }
    }

    private RHIEnvironmentTextureResource? EnsureEnvironmentTexture(
        RenderContext context,
        SceneEnvironment environment)
    {
        var desiredGuid = environment.EnvironmentTextureGuid;
        if (desiredGuid == Guid.Empty)
        {
            ReleaseEnvironmentTexture();
            m_FailedEnvironmentTextureGuid = Guid.Empty;
            m_FailedEnvironmentTextureStamp = AssetDependencyStamp.Empty;
            return null;
        }

        if (m_PreparedAssetProvider.TryGetEnvironment(
                desiredGuid,
                out RHIEnvironmentTextureResource preparedTexture,
                out _))
        {
            ReleaseEnvironmentTexture();
            m_FailedEnvironmentTextureGuid = Guid.Empty;
            m_FailedEnvironmentTextureStamp = AssetDependencyStamp.Empty;
            return preparedTexture;
        }

        if (m_EnvironmentTexture is { IsValid: true } current)
        {
            if (current.Asset.Guid == desiredGuid && !current.IsSourceStale())
            {
                return current;
            }

            ReleaseEnvironmentTexture();
        }

        EnvironmentTextureAsset? asset = null;
        var dependencyStamp = AssetDependencyTracker.GetAssetStamp(m_AssetDatabase, desiredGuid);
        try
        {
            asset = EnvironmentTextureAssetLoader.Load(m_AssetDatabase, desiredGuid);
            dependencyStamp = AssetDependencyTracker.GetEnvironmentTextureStamp(m_AssetDatabase, asset);
            if (m_FailedEnvironmentTextureGuid == desiredGuid &&
                m_FailedEnvironmentTextureStamp == dependencyStamp)
            {
                return null;
            }

            m_EnvironmentTexture = new RHIEnvironmentTextureResource(
                context.Device,
                m_AssetDatabase,
                asset);
            m_FailedEnvironmentTextureGuid = Guid.Empty;
            m_FailedEnvironmentTextureStamp = AssetDependencyStamp.Empty;
            return m_EnvironmentTexture;
        }
        catch (Exception ex)
        {
            if (m_FailedEnvironmentTextureGuid != desiredGuid ||
                m_FailedEnvironmentTextureStamp != dependencyStamp)
            {
                ArisenEngine.Core.Diagnostics.Logger.Warning(
                    $"[GenericRenderPipeline] Environment texture '{desiredGuid}' could not be prepared; using procedural sky fallback. {ex.Message}");
            }

            m_FailedEnvironmentTextureGuid = desiredGuid;
            m_FailedEnvironmentTextureStamp = dependencyStamp;
            return null;
        }
    }

    private void ReleaseEnvironmentTexture()
    {
        ReleaseEnvironmentLighting();
        if (m_EnvironmentTexture == null)
        {
            return;
        }

        m_DisposalQueue.Enqueue(m_EnvironmentTexture, m_LastSubmittedTicket);
        m_EnvironmentTexture = null;
    }

    private RHIEnvironmentLightingResource? EnsureEnvironmentLighting(
        RenderContext context,
        RHIEnvironmentTextureResource? environmentTexture)
    {
        if (environmentTexture is not { IsValid: true })
        {
            ReleaseEnvironmentLighting();
            m_FailedEnvironmentLightingGuid = Guid.Empty;
            m_FailedEnvironmentLightingStamp = AssetDependencyStamp.Empty;
            return null;
        }

        var asset = environmentTexture.Asset;
        if (m_PreparedAssetProvider.TryGetEnvironment(
                asset.Guid,
                out _,
                out RHIEnvironmentLightingResource preparedLighting))
        {
            ReleaseEnvironmentLighting();
            m_FailedEnvironmentLightingGuid = Guid.Empty;
            m_FailedEnvironmentLightingStamp = AssetDependencyStamp.Empty;
            return preparedLighting;
        }

        if (m_EnvironmentLighting is { IsValid: true } current)
        {
            if (current.Asset.Guid == asset.Guid && !current.IsSourceStale())
            {
                return current;
            }

            ReleaseEnvironmentLighting();
        }

        var dependencyStamp = environmentTexture.DependencyStamp;
        if (m_FailedEnvironmentLightingGuid == asset.Guid &&
            m_FailedEnvironmentLightingStamp == dependencyStamp)
        {
            return null;
        }

        try
        {
            m_EnvironmentLighting = new RHIEnvironmentLightingResource(
                context.Device,
                m_AssetDatabase,
                asset);
            m_FailedEnvironmentLightingGuid = Guid.Empty;
            m_FailedEnvironmentLightingStamp = AssetDependencyStamp.Empty;
            return m_EnvironmentLighting;
        }
        catch (Exception ex)
        {
            if (m_FailedEnvironmentLightingGuid != asset.Guid ||
                m_FailedEnvironmentLightingStamp != dependencyStamp)
            {
                ArisenEngine.Core.Diagnostics.Logger.Warning(
                    $"[GenericRenderPipeline] Environment IBL for '{asset.Guid}' could not be prepared; retaining the authored sky with ambient fallback. {ex.Message}");
            }

            m_FailedEnvironmentLightingGuid = asset.Guid;
            m_FailedEnvironmentLightingStamp = dependencyStamp;
            return null;
        }
    }

    private void ReleaseEnvironmentLighting()
    {
        if (m_EnvironmentLighting == null)
        {
            return;
        }

        m_DisposalQueue.Enqueue(m_EnvironmentLighting, m_LastSubmittedTicket);
        m_EnvironmentLighting = null;
    }

    private void RegisterSceneMaterials(ReadOnlySpan<StaticMeshRenderItem> items)
    {
        for (int i = 0; i < items.Length; i++)
        {
            ref readonly var item = ref items[i];
            if (!item.IsValid || item.MaterialGuid == Guid.Empty)
            {
                continue;
            }

            m_MaterialLibrary.RegisterMaterial(item.MaterialGuid);
        }
    }

    private ReadOnlySpan<MeshDrawCommand> GetPreparedDraws(RenderContext context)
    {
        if (m_SceneDrawCommandCount > 0 || context.StaticMeshItemCount > 0)
        {
            return new ReadOnlySpan<MeshDrawCommand>(m_SceneDrawCommands, 0, m_SceneDrawCommandCount);
        }

        return context.DrawList;
    }

    private void PrepareCameraDrawQueues(
        ReadOnlySpan<MeshDrawCommand> sourceDraws,
        Matrix4x4 viewMatrix)
    {
        m_DepthWritingDrawCommandCount = 0;
        m_TransparentDrawCommandCount = 0;
        m_OpaqueDrawCommandCount = 0;
        m_AlphaTestDrawCommandCount = 0;
        m_SkippedAlphaDrawCommandCount = 0;

        EnsureDrawCapacity(ref m_DepthWritingDrawCommands, sourceDraws.Length);
        EnsureDrawCapacity(ref m_TransparentDrawCommands, sourceDraws.Length);
        if (m_TransparentDrawSortKeys.Length < sourceDraws.Length)
        {
            Array.Resize(ref m_TransparentDrawSortKeys, sourceDraws.Length);
        }

        for (int i = 0; i < sourceDraws.Length; i++)
        {
            ref readonly var draw = ref sourceDraws[i];
            var renderQueue = m_MaterialLibrary.GetRenderQueue(draw.MaterialID);
            if (!IsDrawable(draw))
            {
                if (renderQueue.Class is RenderQueueClass.AlphaTest or RenderQueueClass.Transparent)
                {
                    m_SkippedAlphaDrawCommandCount++;
                }

                continue;
            }

            if (renderQueue.Class == RenderQueueClass.Transparent)
            {
                m_TransparentDrawCommands[m_TransparentDrawCommandCount++] = draw;
                continue;
            }

            m_DepthWritingDrawCommands[m_DepthWritingDrawCommandCount++] = draw;
            if (renderQueue.Class == RenderQueueClass.AlphaTest)
            {
                m_AlphaTestDrawCommandCount++;
            }
            else
            {
                m_OpaqueDrawCommandCount++;
            }
        }

        TransparentDrawOrdering.SortBackToFront(
            m_TransparentDrawCommands,
            m_TransparentDrawSortKeys,
            m_TransparentDrawCommandCount,
            viewMatrix);
    }

    private static bool IsDrawable(in MeshDrawCommand draw)
    {
        return draw.VertexBuffer.IsValid &&
               draw.IndexBuffer.IsValid &&
               draw.IndexCount != 0;
    }

    private ReadOnlySpan<MeshDrawCommand> PrepareShadowDrawCommands(
        RHIDevice device,
        ReadOnlySpan<StaticMeshRenderItem> items,
        ReadOnlySpan<MeshDrawCommand> cameraVisibleDraws,
        DirectionalShadowCascadeSet cascades)
    {
        m_ShadowDrawCommandCount = 0;
        m_DroppedShadowDrawCommandCount = 0;
        Span<DirectionalShadowCascadeDrawRange> ranges =
            stackalloc DirectionalShadowCascadeDrawRange[4];
        int cascadeCount = cascades.Count;
        if (items.IsEmpty)
        {
            EnsureDrawCapacity(
                ref m_ShadowDrawCommands,
                Math.Min(
                    MaximumDirectionalShadowDrawCommands,
                    checked(cameraVisibleDraws.Length * cascadeCount)));
            for (int cascadeIndex = 0; cascadeIndex < cascadeCount; cascadeIndex++)
            {
                int rangeStart = m_ShadowDrawCommandCount;
                int remaining = MaximumDirectionalShadowDrawCommands - rangeStart;
                int accepted = Math.Min(cameraVisibleDraws.Length, Math.Max(0, remaining));
                cameraVisibleDraws.Slice(0, accepted).CopyTo(
                    m_ShadowDrawCommands.AsSpan(rangeStart, accepted));
                m_ShadowDrawCommandCount += accepted;
                m_DroppedShadowDrawCommandCount += cameraVisibleDraws.Length - accepted;
                ranges[cascadeIndex] = new DirectionalShadowCascadeDrawRange(
                    rangeStart,
                    accepted);
            }

            m_ShadowCasterCullingStats = new StaticMeshCullingStats(
                checked(cameraVisibleDraws.Length * cascadeCount),
                m_ShadowDrawCommandCount,
                0);
            FinalizeShadowDrawRanges(cascadeCount, ranges);
            PlotShadowCasterCullingDiagnostics(m_ShadowDrawCommandCount);
            return new ReadOnlySpan<MeshDrawCommand>(
                m_ShadowDrawCommands,
                0,
                m_ShadowDrawCommandCount);
        }

        int visibleItemCount = 0;
        int culledItemCount = 0;
        for (int cascadeIndex = 0; cascadeIndex < cascadeCount; cascadeIndex++)
        {
            int rangeStart = m_ShadowDrawCommandCount;
            DirectionalShadowCascade cascade = cascades.GetCascade(cascadeIndex);
            for (int itemIndex = 0; itemIndex < items.Length; itemIndex++)
            {
                ref readonly StaticMeshRenderItem item = ref items[itemIndex];
                if (!item.IsValid)
                {
                    continue;
                }

                RHIStaticMeshResource mesh = GetOrCreateSceneMesh(device, item.MeshGuid);
                if (mesh is not { IsValid: true })
                {
                    continue;
                }

                if (StaticMeshFrustumCuller.TryGetWorldBounds(
                        item,
                        mesh.Bounds,
                        out MeshBounds worldBounds) &&
                    !StaticMeshFrustumCuller.IsVisible(
                        DirectionalShadowCoordinateSpace.ToCameraRelative(
                            worldBounds,
                            cascades.CameraPosition),
                        cascade.ViewProjection))
                {
                    culledItemCount++;
                    continue;
                }

                visibleItemCount++;
                int firstAppendedDraw = m_ShadowDrawCommandCount;
                int appendedDrawEnd = AppendShadowItemDrawCommands(
                    item,
                    mesh,
                    out int droppedDrawCount);
                m_DroppedShadowDrawCommandCount += droppedDrawCount;
                m_ShadowDrawCommandCount = CompactDepthWritingDrawCommands(
                    m_ShadowDrawCommands,
                    firstAppendedDraw,
                    appendedDrawEnd);
            }

            ranges[cascadeIndex] = new DirectionalShadowCascadeDrawRange(
                rangeStart,
                m_ShadowDrawCommandCount - rangeStart);
        }

        m_ShadowCasterCullingStats = new StaticMeshCullingStats(
            checked(items.Length * cascadeCount),
            visibleItemCount,
            culledItemCount);
        FinalizeShadowDrawRanges(cascadeCount, ranges);
        PlotShadowCasterCullingDiagnostics(m_ShadowDrawCommandCount);
        return new ReadOnlySpan<MeshDrawCommand>(
            m_ShadowDrawCommands,
            0,
            m_ShadowDrawCommandCount);
    }

    private void PrepareSceneDrawCommands(
        RHIDevice device,
        ReadOnlySpan<StaticMeshRenderItem> items,
        Matrix4x4 viewProjection,
        bool enableFrustumCulling)
    {
        m_SceneDrawCommandCount = 0;
        m_ShadowReceiverBounds = default;
        int visibleItemCount = 0;
        int culledItemCount = 0;
        if (items.IsEmpty)
        {
            m_StaticMeshCullingStats = new StaticMeshCullingStats(0, 0, 0);
            PlotStaticMeshCullingDiagnostics();
            return;
        }

        for (int i = 0; i < items.Length; i++)
        {
            ref readonly var item = ref items[i];
            if (!item.IsValid)
            {
                continue;
            }

            var mesh = GetOrCreateSceneMesh(device, item.MeshGuid);
            if (mesh is not { IsValid: true })
            {
                continue;
            }

            bool hasWorldBounds = StaticMeshFrustumCuller.TryGetWorldBounds(
                item,
                mesh.Bounds,
                out var worldBounds);
            if (enableFrustumCulling &&
                hasWorldBounds &&
                !StaticMeshFrustumCuller.IsVisible(worldBounds, viewProjection))
            {
                culledItemCount++;
                continue;
            }

            visibleItemCount++;
            if (hasWorldBounds)
            {
                m_ShadowReceiverBounds.Add(worldBounds);
            }

            m_SceneDrawCommandCount = AppendItemDrawCommands(
                ref m_SceneDrawCommands,
                m_SceneDrawCommandCount,
                item,
                mesh);
        }

        m_StaticMeshCullingStats = new StaticMeshCullingStats(
            items.Length,
            visibleItemCount,
            culledItemCount);
        PlotStaticMeshCullingDiagnostics();
    }

    private void PlotStaticMeshCullingDiagnostics()
    {
        Profiler.PlotValue("Render.StaticMeshItemCount", m_StaticMeshCullingStats.SourceItemCount);
        Profiler.PlotValue("Render.VisibleStaticMeshItemCount", m_StaticMeshCullingStats.VisibleItemCount);
        Profiler.PlotValue("Render.CulledStaticMeshItemCount", m_StaticMeshCullingStats.CulledItemCount);
        Profiler.PlotValue("Render.SceneDrawCommandCount", m_SceneDrawCommandCount);
    }

    private void PlotShadowCasterCullingDiagnostics(int drawCommandCount)
    {
        Profiler.PlotValue("Render.ShadowCasterSourceItemCount", m_ShadowCasterCullingStats.SourceItemCount);
        Profiler.PlotValue("Render.ShadowCasterItemCount", m_ShadowCasterCullingStats.VisibleItemCount);
        Profiler.PlotValue("Render.CulledShadowCasterItemCount", m_ShadowCasterCullingStats.CulledItemCount);
        Profiler.PlotValue("Render.ShadowCasterDrawCommandCount", drawCommandCount);
    }

    private void ResetShadowDrawCommands(int cascadeCount)
    {
        m_ShadowDrawCommandCount = 0;
        m_DroppedShadowDrawCommandCount = 0;
        m_ShadowCasterCullingStats = default;
        m_ShadowDrawRanges = new DirectionalShadowCascadeDrawRangeSet(
            cascadeCount,
            0,
            0,
            default,
            default,
            default,
            default);
        PlotShadowCasterCullingDiagnostics(0);
    }

    private int AppendShadowItemDrawCommands(
        in StaticMeshRenderItem item,
        RHIStaticMeshResource mesh,
        out int droppedDrawCount)
    {
        droppedDrawCount = 0;
        int firstSubmeshIndex = Math.Max(0, item.FirstSubmeshIndex);
        if (firstSubmeshIndex >= mesh.SubmeshCount)
        {
            return m_ShadowDrawCommandCount;
        }

        int availableSubmeshes = mesh.SubmeshCount - firstSubmeshIndex;
        int requestedSubmeshes = item.SubmeshCount < 0
            ? availableSubmeshes
            : Math.Min(item.SubmeshCount, availableSubmeshes);
        if (requestedSubmeshes <= 0)
        {
            return m_ShadowDrawCommandCount;
        }

        int remainingCapacity = Math.Max(
            0,
            MaximumDirectionalShadowDrawCommands - m_ShadowDrawCommandCount);
        int acceptedSubmeshes = Math.Min(requestedSubmeshes, remainingCapacity);
        droppedDrawCount = requestedSubmeshes - acceptedSubmeshes;
        if (acceptedSubmeshes == 0)
        {
            return m_ShadowDrawCommandCount;
        }

        EnsureDrawCapacity(
            ref m_ShadowDrawCommands,
            m_ShadowDrawCommandCount + acceptedSubmeshes);
        Span<MeshDrawCommand> destination = new(
            m_ShadowDrawCommands,
            m_ShadowDrawCommandCount,
            acceptedSubmeshes);
        uint materialId = ResolveMaterialId(item.MaterialGuid);
        int written = item.MaterialGuid == Guid.Empty
            ? mesh.CreateDrawCommands(
                destination,
                item.LocalToWorld,
                materialId,
                firstSubmeshIndex,
                acceptedSubmeshes)
            : mesh.CreateDrawCommandsWithMaterialOverride(
                destination,
                item.LocalToWorld,
                materialId,
                firstSubmeshIndex,
                acceptedSubmeshes);
        return m_ShadowDrawCommandCount + written;
    }

    private void FinalizeShadowDrawRanges(
        int cascadeCount,
        ReadOnlySpan<DirectionalShadowCascadeDrawRange> ranges)
    {
        m_ShadowDrawRanges = new DirectionalShadowCascadeDrawRangeSet(
            cascadeCount,
            m_ShadowDrawCommandCount,
            m_DroppedShadowDrawCommandCount,
            ranges[0],
            ranges[1],
            ranges[2],
            ranges[3]);
    }

    private int AppendItemDrawCommands(
        ref MeshDrawCommand[] drawCommands,
        int drawCommandCount,
        in StaticMeshRenderItem item,
        RHIStaticMeshResource mesh)
    {
        int firstSubmeshIndex = Math.Max(0, item.FirstSubmeshIndex);
        if (firstSubmeshIndex >= mesh.SubmeshCount)
        {
            return drawCommandCount;
        }

        int availableSubmeshes = mesh.SubmeshCount - firstSubmeshIndex;
        int requestedSubmeshes = item.SubmeshCount < 0
            ? availableSubmeshes
            : Math.Min(item.SubmeshCount, availableSubmeshes);
        if (requestedSubmeshes <= 0)
        {
            return drawCommandCount;
        }

        EnsureDrawCapacity(ref drawCommands, drawCommandCount + requestedSubmeshes);
        var destination = new Span<MeshDrawCommand>(
            drawCommands,
            drawCommandCount,
            requestedSubmeshes);
        uint materialId = ResolveMaterialId(item.MaterialGuid);
        int written = item.MaterialGuid == Guid.Empty
            ? mesh.CreateDrawCommands(
                destination,
                item.LocalToWorld,
                materialId,
                firstSubmeshIndex,
                requestedSubmeshes)
            : mesh.CreateDrawCommandsWithMaterialOverride(
                destination,
                item.LocalToWorld,
                materialId,
                firstSubmeshIndex,
                requestedSubmeshes);
        return drawCommandCount + written;
    }

    private int CompactDepthWritingDrawCommands(
        MeshDrawCommand[] drawCommands,
        int start,
        int end)
    {
        int writeIndex = start;
        for (int readIndex = start; readIndex < end; readIndex++)
        {
            ref readonly var draw = ref drawCommands[readIndex];
            if (m_MaterialLibrary.GetRenderQueue(draw.MaterialID).Class == RenderQueueClass.Transparent)
            {
                continue;
            }

            drawCommands[writeIndex++] = draw;
        }

        return writeIndex;
    }

    private RHIStaticMeshResource GetOrCreateSceneMesh(RHIDevice device, Guid meshGuid)
    {
        if (m_PreparedAssetProvider.TryGetMesh(meshGuid, out RHIStaticMeshResource preparedMesh))
        {
            if (m_SceneMeshes.Remove(meshGuid, out RHIStaticMeshResource? duplicate))
            {
                m_DisposalQueue.Enqueue(duplicate, m_LastSubmittedTicket);
            }

            return preparedMesh;
        }

        if (m_SceneMeshes.TryGetValue(meshGuid, out var mesh))
        {
            if (mesh.IsValid && !mesh.IsSourceStale())
            {
                return mesh;
            }

            ArisenEngine.Core.Diagnostics.Logger.Log($"[GenericRenderPipeline] Scene mesh dependency changed; reloading {meshGuid}.");
            m_SceneMeshes.Remove(meshGuid);
            m_DisposalQueue.Enqueue(mesh, m_LastSubmittedTicket);
        }

        var meshAsset = CreateMeshAsset(meshGuid);
        mesh = new RHIStaticMeshResource(device, m_AssetDatabase, meshAsset);
        m_SceneMeshes.Add(meshGuid, mesh);
        ArisenEngine.Core.Diagnostics.Logger.Log(
            $"[GenericRenderPipeline] Loaded scene GPU mesh | Guid: {meshGuid} | Name: {meshAsset.Name} | Vertices: {mesh.VertexCount} | Indices: {mesh.IndexCount} | Submeshes: {mesh.SubmeshCount}");
        return mesh;
    }

    private MeshAsset CreateMeshAsset(Guid meshGuid)
    {
        if (!m_AssetDatabase.TryGetAssetDescriptor(meshGuid, out AssetDescriptor descriptor))
        {
            throw new InvalidOperationException($"[GenericRenderPipeline] Scene mesh asset '{meshGuid}' was not found.");
        }

        if (!string.Equals(descriptor.AssetType, "Mesh", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[GenericRenderPipeline] Scene asset '{meshGuid}' has type '{descriptor.AssetType}', expected Mesh.");
        }

        if (m_AssetDatabase.CanReadSourceAssets &&
            m_AssetDatabase.TryGetAsset(meshGuid, out AssetRecord? sourceAsset))
        {
            return new MeshAsset(
                meshGuid,
                Path.GetFileNameWithoutExtension(sourceAsset.SourcePath),
                MeshVariantKey.Default,
                ResolveMeshSourceFormat(sourceAsset.SourcePath));
        }

        return new MeshAsset(
            meshGuid,
            $"RuntimeMesh/{meshGuid:N}",
            MeshVariantKey.Default,
            MeshSourceFormat.ArisenTextMesh);
    }

    private uint ResolveMaterialId(Guid materialGuid)
    {
        if (materialGuid == Guid.Empty)
        {
            return m_MaterialLibrary.DefaultMaterialID;
        }

        return m_MaterialLibrary.TryGetMaterialID(materialGuid, out var materialId)
            ? materialId
            : m_MaterialLibrary.DefaultMaterialID;
    }

    private static void EnsureDrawCapacity(
        ref MeshDrawCommand[] drawCommands,
        int capacity)
    {
        if (drawCommands.Length < capacity)
        {
            Array.Resize(ref drawCommands, Math.Max(capacity, drawCommands.Length * 2));
        }
    }

    private void ReleaseSceneMeshes(bool disposeImmediately)
    {
        foreach (var mesh in m_SceneMeshes.Values)
        {
            if (disposeImmediately)
            {
                mesh.Dispose();
            }
            else
            {
                m_DisposalQueue.Enqueue(mesh, m_LastSubmittedTicket);
            }
        }

        m_SceneMeshes.Clear();
        m_SceneDrawCommandCount = 0;
        m_DepthWritingDrawCommandCount = 0;
        m_TransparentDrawCommandCount = 0;
        m_OpaqueDrawCommandCount = 0;
        m_AlphaTestDrawCommandCount = 0;
        m_SkippedAlphaDrawCommandCount = 0;
        m_ShadowDrawCommandCount = 0;
        m_StaticMeshCullingStats = default;
        m_ShadowCasterCullingStats = default;
        m_ShadowReceiverBounds = default;
    }

    private static MeshSourceFormat ResolveMeshSourceFormat(string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath);
        if (string.Equals(extension, ".obj", StringComparison.OrdinalIgnoreCase))
        {
            return MeshSourceFormat.WavefrontObj;
        }

        if (string.Equals(extension, ".gltf", StringComparison.OrdinalIgnoreCase))
        {
            return MeshSourceFormat.GltfJson;
        }

        if (string.Equals(extension, ".glb", StringComparison.OrdinalIgnoreCase))
        {
            return MeshSourceFormat.GltfBinary;
        }

        return MeshSourceFormat.ArisenTextMesh;
    }

    private static bool ContainsGuid(ReadOnlySpan<Guid> guids, Guid guid)
    {
        for (int i = 0; i < guids.Length; i++)
        {
            if (guids[i] == guid)
            {
                return true;
            }
        }

        return false;
    }

    private void LogShadowValidationFrame(
        uint frameIndex,
        in DirectionalShadowCascadeSet cascades)
    {
        KernelLog.InfoFormat(
            "[GenericRP.ShadowValidation] Frame={0} Cascades={1} " +
            "MeshDraws={2},{3},{4},{5} Dropped={6} " +
            "SplitFar={7:F6},{8:F6},{9:F6},{10:F6}",
            frameIndex,
            cascades.Count,
            GetCascadeDrawCount(m_ShadowDrawRanges, 0),
            GetCascadeDrawCount(m_ShadowDrawRanges, 1),
            GetCascadeDrawCount(m_ShadowDrawRanges, 2),
            GetCascadeDrawCount(m_ShadowDrawRanges, 3),
            m_ShadowDrawRanges.DroppedDrawCount,
            GetCascadeSplitFar(cascades, 0),
            GetCascadeSplitFar(cascades, 1),
            GetCascadeSplitFar(cascades, 2),
            GetCascadeSplitFar(cascades, 3));
    }

    private static void LogAtmosphereValidationFrame(
        uint frameIndex,
        in OutdoorEnvironmentProfile profile,
        float effectiveExposure,
        bool environmentIblReady,
        DeviceDepthConvention depthConvention)
    {
        KernelLog.InfoFormat(
            "[GenericRP.AtmosphereValidation] Frame={0} SkyMode={1} " +
            "Atmosphere={2} Aerial={3} HeightFog={4} ExposurePolicy={5} " +
            "Exposure={6:F6} Depth={7} IBL={8} SunCoupling={9:F6} " +
            "AerialStart={10:F6} AerialDistance={11:F6} AerialStrength={12:F6} " +
            "FogDensity={13:F6} FogFalloff={14:F6}",
            frameIndex,
            profile.SkyMode,
            profile.IsAtmosphereEnabled ? 1 : 0,
            profile.AerialPerspectiveEnabled ? 1 : 0,
            profile.HeightFogEnabled ? 1 : 0,
            profile.ExposurePolicy,
            effectiveExposure,
            depthConvention,
            environmentIblReady ? 1 : 0,
            profile.SunSkyCoupling,
            profile.AerialStartDistance,
            profile.AerialDistance,
            profile.AerialStrength,
            profile.HeightFogDensity,
            profile.HeightFogFalloff);
    }

    private static int GetCascadeDrawCount(
        in DirectionalShadowCascadeDrawRangeSet ranges,
        int cascadeIndex) =>
        cascadeIndex < ranges.Count ? ranges.GetRange(cascadeIndex).Count : 0;

    private static float GetCascadeSplitFar(
        in DirectionalShadowCascadeSet cascades,
        int cascadeIndex) =>
        cascadeIndex < cascades.Count ? cascades.GetCascade(cascadeIndex).SplitFar : 0.0f;

    private static Matrix4x4 GetViewProjection(RenderContext context)
    {
        if (context.CameraCount <= 0)
        {
            return Matrix4x4.Identity;
        }

        var cameras = context.Cameras;
        ref readonly var camera = ref cameras[0];
        return camera.ViewMatrix * camera.ProjectionMatrix;
    }

    private static Matrix4x4 GetViewMatrix(RenderContext context)
    {
        return context.CameraCount > 0
            ? context.Cameras[0].ViewMatrix
            : Matrix4x4.Identity;
    }

    private static Vector3 GetCameraPosition(RenderContext context)
    {
        if (context.CameraCount <= 0)
        {
            return Vector3.Zero;
        }

        return context.Cameras[0].Position;
    }

    private Camera GetShadowCamera(RenderContext context)
    {
        if (context.CameraCount > 0)
        {
            return context.Cameras[0];
        }

        return new Camera
        {
            FieldOfView = 60.0f,
            NearClip = 0.1f,
            FarClip = MathF.Max(1.0f, m_Settings.Shadows.MaximumDistance),
            AspectRatio = context.Height > 0
                ? context.Width / (float)context.Height
                : 1.0f,
            OrthographicSize = 5.0f,
            ProjectionType = CameraProjectionType.Perspective,
            Position = Vector3.Zero,
            Rotation = Vector3.Zero
        };
    }

    private static DirectionalLight GetPrimaryDirectionalLight(RenderContext context)
    {
        if (context.DirectionalLightCount <= 0)
        {
            return DirectionalLight.Default;
        }

        var directionalLights = context.DirectionalLights;
        for (int i = 0; i < directionalLights.Length; i++)
        {
            var light = directionalLights[i];
            if (light.IsValid)
            {
                return light;
            }
        }

        return DirectionalLight.Default;
    }

    private SceneEnvironment GetSceneEnvironment(
        RenderContext context,
        DirectionalLight directionalLight)
    {
        if (context.SceneEnvironmentCount > 0 && context.SceneEnvironment.IsValid)
        {
            return context.SceneEnvironment;
        }

        return SceneEnvironment.CreateFlatFallback(
            new Vector3(m_ClearColor.r, m_ClearColor.g, m_ClearColor.b),
            directionalLight.AmbientIntensity);
    }
}
