using Arisen.Native.RHI;
using ArisenEngine.Core.Math;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.RHI;
using ArisenEngine.Rendering.Resources;
using ArisenEngine.Threading;
using System.IO;
using System.Numerics;

namespace ArisenEngine.Rendering;

public class GenericRenderPipeline : RenderPipeline
{
    private readonly Color m_ClearColor;
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly GenericRenderMaterialLibrary m_MaterialLibrary;
    private readonly DirectionalShadowPass m_DirectionalShadowPass;
    private readonly EnvironmentSkyPass m_EnvironmentSkyPass;
    private readonly StaticMeshPass m_StaticMeshPass;
    private readonly TonemapPass m_TonemapPass;
    private readonly RenderResourceReloadQueue m_ReloadQueue = new();
    private readonly DeferredRenderResourceDisposalQueue m_DisposalQueue;
    private readonly Dictionary<Guid, RHIStaticMeshResource> m_SceneMeshes = new();
    private RHIEnvironmentTextureResource? m_EnvironmentTexture;
    private RHIEnvironmentLightingResource? m_EnvironmentLighting;
    private Guid m_FailedEnvironmentTextureGuid;
    private AssetDependencyStamp m_FailedEnvironmentTextureStamp = AssetDependencyStamp.Empty;
    private Guid m_FailedEnvironmentLightingGuid;
    private AssetDependencyStamp m_FailedEnvironmentLightingStamp = AssetDependencyStamp.Empty;
    private readonly Matrix4x4 m_FallbackStaticMeshLocalToWorld = Matrix4x4.CreateScale(1.24f, 1.24f, 1.0f);
    private MeshDrawCommand[] m_SceneDrawCommands = Array.Empty<MeshDrawCommand>();
    private int m_SceneDrawCommandCount;
    private StaticMeshCullingStats m_StaticMeshCullingStats;
    private RHIDevice m_LastDevice;
    private ulong m_LastSubmittedTicket;
    private RHIStaticMeshResource? m_FallbackMesh;
    private DirectionalShadowTarget? m_DirectionalShadowTarget;

    public GenericRenderPipeline(
        Color clearColor,
        IAssetDatabase assetDatabase,
        GenericRenderMaterialLibrary materialLibrary,
        DeferredRenderResourceDisposalQueue disposalQueue)
    {
        m_ClearColor = clearColor;
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        m_MaterialLibrary = materialLibrary ?? throw new ArgumentNullException(nameof(materialLibrary));
        m_DisposalQueue = disposalQueue ?? throw new ArgumentNullException(nameof(disposalQueue));
        m_DirectionalShadowPass = new DirectionalShadowPass(assetDatabase);
        m_EnvironmentSkyPass = new EnvironmentSkyPass(assetDatabase);
        m_StaticMeshPass = new StaticMeshPass(assetDatabase, "GenericStaticMeshPass");
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
        var sceneColorTexture = graph.CreateTransientTexture(
            context,
            "SceneColor",
            RenderGraphTextureDescriptor.ColorAttachmentSampled2D(
                "GenericSceneColorHDR",
                context.Width,
                context.Height,
                EFormat.FORMAT_R16G16B16A16_SFLOAT));
        var directionalShadowTarget = EnsureDirectionalShadowTarget(context);
        var sceneColorResource = sceneColorTexture.Resource;
        var shadowMapResource = graph.CreateTransientResource("DirectionalShadowMap", RenderResourceType.Texture);
        var fallbackMaterial = m_MaterialLibrary.GetPreparedMaterial(m_MaterialLibrary.DefaultMaterialID);
        var preparedDraws = GetPreparedDraws(context);
        var directionalLight = GetPrimaryDirectionalLight(context);
        var sceneEnvironment = GetSceneEnvironment(context, directionalLight);
        var environmentTexture = EnsureEnvironmentTexture(context, sceneEnvironment);
        var environmentLighting = EnsureEnvironmentLighting(context, environmentTexture);
        int registeredMaterialCount = m_MaterialLibrary.MaterialCount;
        int preparedMaterialCount = m_MaterialLibrary.PreparedMaterialCount;
        int visibleDrawCommandCount = preparedDraws.Length;

        m_DirectionalShadowPass.SetTarget(directionalShadowTarget);
        m_DirectionalShadowPass.SetDirectionalLight(directionalLight);
        m_DirectionalShadowPass.SetPreparedDraws(preparedDraws);
        m_DirectionalShadowPass.Prepare(context);

        m_StaticMeshPass.SetStaticMeshResources(fallbackMaterial, m_FallbackMesh!);
        m_StaticMeshPass.SetColorTarget(sceneColorTexture.ImageView, sceneColorTexture.Format);
        m_MaterialLibrary.ApplyMaterialSlots(m_StaticMeshPass);
        m_StaticMeshPass.SetPreparedDraws(preparedDraws);
        m_StaticMeshPass.SetViewProjection(GetViewProjection(context));
        m_StaticMeshPass.SetCameraPosition(GetCameraPosition(context));
        m_StaticMeshPass.SetPointLights(context.PointLights);
        m_StaticMeshPass.SetSpotLights(context.SpotLights);
        m_StaticMeshPass.SetDirectionalShadow(
            m_DirectionalShadowPass.ViewProjection,
            directionalShadowTarget.BindlessImageIndex,
            directionalShadowTarget.BindlessSamplerIndex,
            1.0f / directionalShadowTarget.Size,
            m_DirectionalShadowPass.HasRenderableShadowMap);
        m_EnvironmentSkyPass.SetEnvironment(sceneEnvironment);
        m_EnvironmentSkyPass.SetEnvironmentTexture(environmentTexture);
        m_EnvironmentSkyPass.SetColorTarget(
            sceneColorTexture.ImageView,
            sceneColorTexture.Format);
        m_EnvironmentSkyPass.Prepare(context);
        m_StaticMeshPass.SetDirectionalLight(directionalLight);
        m_StaticMeshPass.SetSceneEnvironment(sceneEnvironment);
        m_StaticMeshPass.SetEnvironmentLighting(environmentLighting);
        m_StaticMeshPass.SetFallbackLocalToWorld(m_FallbackStaticMeshLocalToWorld);
        m_TonemapPass.SetSceneColor(
            sceneColorTexture.Image,
            sceneColorTexture.BindlessImageIndex,
            sceneColorTexture.BindlessSamplerIndex);
        m_TonemapPass.SetExposure(sceneEnvironment.Exposure);
        m_TonemapPass.Prepare(context);

        if (context.FrameIndex % 60 == 0)
        {
            ArisenEngine.Core.Diagnostics.Logger.Log($"[GenericRenderPipeline] SetupGraph | Frame: {context.FrameIndex} | Surface: 0x{context.SurfaceId:X} | Cameras: {context.CameraCount} | DirectionalLights: {context.DirectionalLightCount} | PointLights: {context.PointLightCount} | SpotLights: {context.SpotLightCount} | Environments: {context.SceneEnvironmentCount} | EnvironmentTexture: {(environmentTexture is { IsValid: true } ? environmentTexture.Asset.Name : "ProceduralFallback")} | EnvironmentIBL: {(environmentLighting is { IsValid: true } ? $"Ready (irr={environmentLighting.IrradianceImageIndex}, spec={environmentLighting.PrefilteredSpecularImageIndex}, brdf={environmentLighting.BrdfIntegrationLutImageIndex})" : "Unavailable")} | Materials: {preparedMaterialCount}/{registeredMaterialCount} prepared | LegacyDraws: {context.DrawListCount} | SceneItems: {m_StaticMeshCullingStats.SourceItemCount} | VisibleItems: {m_StaticMeshCullingStats.VisibleItemCount} | CulledItems: {m_StaticMeshCullingStats.CulledItemCount} | VisibleDrawCommands: {visibleDrawCommandCount} | SceneColor: {sceneColorTexture.Width}x{sceneColorTexture.Height} {sceneColorTexture.Format} | ClearColorFallback: {m_ClearColor}");
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
        Profiler.PlotValue("Render.VisibleDrawCommandCount", visibleDrawCommandCount);
        Profiler.PlotValue("Render.SceneColor.Width", sceneColorTexture.Width);
        Profiler.PlotValue("Render.SceneColor.Height", sceneColorTexture.Height);
        Profiler.PlotValue("Render.SceneColor.Format", (double)(uint)sceneColorTexture.Format);
        Profiler.PlotValue("Render.ShadowMap.Size", directionalShadowTarget.Size);
        Profiler.PlotValue("Render.ShadowMap.Enabled", m_DirectionalShadowPass.HasRenderableShadowMap ? 1 : 0);

        // 1. Directional shadow depth is rendered before lighting samples it.
        graph.AddPass(
            m_DirectionalShadowPass,
            builder => builder.WriteDepthAttachment(shadowMapResource));

        // 2. The environment sky initializes and fills the HDR scene color target.
        graph.AddPass(
            m_EnvironmentSkyPass,
            builder => builder.WriteColorAttachment(sceneColorResource));

        // 3. Static mesh rendering preserves linear HDR lighting in scene color.
        m_StaticMeshPass.Prepare(context);
        graph.AddPass(
            m_StaticMeshPass,
            builder => builder
                .ReadShader(shadowMapResource)
                .ReadColorAttachment(sceneColorResource)
                .WriteColorAttachment(sceneColorResource)
                .WriteDepthAttachment(graph.FrameDepth));

        // 4. Tonemap scene color into the active presentation target.
        graph.AddPass(
            m_TonemapPass,
            builder => builder
                .ReadShader(sceneColorResource)
                .WriteColorAttachment(graph.FrameColor));
    }

    protected override void OnDisposed()
    {
        m_AssetDatabase.AssetChanged -= OnAssetChanged;
        WaitForLastSubmittedFrame();
        m_DisposalQueue.Drain(m_LastDevice);
        m_DirectionalShadowPass.Dispose();
        m_EnvironmentSkyPass.Dispose();
        m_StaticMeshPass.Dispose();
        m_TonemapPass.Dispose();
        m_MaterialLibrary.ReleasePreparedResources();
        ReleaseSceneMeshes(disposeImmediately: true);
        m_EnvironmentLighting?.Dispose();
        m_EnvironmentLighting = null;
        m_EnvironmentTexture?.Dispose();
        m_EnvironmentTexture = null;
        m_DirectionalShadowTarget?.Dispose();
        m_DirectionalShadowTarget = null;
        m_FallbackMesh?.Dispose();
        m_FallbackMesh = null;
    }

    protected override void OnFrameSubmitted(RenderContext context, ulong submittedTicket)
    {
        m_LastSubmittedTicket = submittedTicket;
        m_DisposalQueue.ReleaseCompleted(context.Device);
    }

    private void OnAssetChanged(AssetChangeEvent change)
    {
        m_ReloadQueue.MarkDirty(change);
    }

    private void EnsureSmokeAssetsLoaded(RenderContext context)
    {
        var device = context.Device;
        m_LastDevice = device;

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

    private DirectionalShadowTarget EnsureDirectionalShadowTarget(RenderContext context)
    {
        const uint shadowMapSize = 2048;
        const EFormat shadowFormat = EFormat.FORMAT_D32_SFLOAT;

        if (m_DirectionalShadowTarget is { IsValid: true } current &&
            current.Size == shadowMapSize &&
            current.Format == shadowFormat)
        {
            return current;
        }

        if (m_DirectionalShadowTarget != null)
        {
            m_DisposalQueue.Enqueue(m_DirectionalShadowTarget, m_LastSubmittedTicket);
        }

        var target = new DirectionalShadowTarget();
        target.Ensure(
            context.Device.GetFactory(),
            shadowMapSize,
            shadowFormat);
        m_DirectionalShadowTarget = target;
        return target;
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
            asset = EnvironmentTextureAssetLoader.LoadSource(m_AssetDatabase, desiredGuid);
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

    private void PrepareSceneDrawCommands(
        RHIDevice device,
        ReadOnlySpan<StaticMeshRenderItem> items,
        Matrix4x4 viewProjection,
        bool enableFrustumCulling)
    {
        m_SceneDrawCommandCount = 0;
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

            if (enableFrustumCulling &&
                !StaticMeshFrustumCuller.IsVisible(item, mesh.Bounds, viewProjection))
            {
                culledItemCount++;
                continue;
            }

            visibleItemCount++;

            int firstSubmeshIndex = Math.Max(0, item.FirstSubmeshIndex);
            if (firstSubmeshIndex >= mesh.SubmeshCount)
            {
                continue;
            }

            int availableSubmeshes = mesh.SubmeshCount - firstSubmeshIndex;
            int requestedSubmeshes = item.SubmeshCount < 0
                ? availableSubmeshes
                : Math.Min(item.SubmeshCount, availableSubmeshes);
            if (requestedSubmeshes <= 0)
            {
                continue;
            }

            EnsureSceneDrawCapacity(m_SceneDrawCommandCount + requestedSubmeshes);
            var destination = new Span<MeshDrawCommand>(
                m_SceneDrawCommands,
                m_SceneDrawCommandCount,
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
            m_SceneDrawCommandCount += written;
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

    private RHIStaticMeshResource GetOrCreateSceneMesh(RHIDevice device, Guid meshGuid)
    {
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
        if (!m_AssetDatabase.TryGetAsset(meshGuid, out var asset))
        {
            throw new InvalidOperationException($"[GenericRenderPipeline] Scene mesh asset '{meshGuid}' was not found.");
        }

        if (!string.Equals(asset.AssetType, "Mesh", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[GenericRenderPipeline] Scene asset '{meshGuid}' has type '{asset.AssetType}', expected Mesh.");
        }

        return new MeshAsset(
            meshGuid,
            Path.GetFileNameWithoutExtension(asset.SourcePath),
            MeshVariantKey.Default,
            ResolveMeshSourceFormat(asset.SourcePath));
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

    private void EnsureSceneDrawCapacity(int capacity)
    {
        if (m_SceneDrawCommands.Length < capacity)
        {
            Array.Resize(ref m_SceneDrawCommands, Math.Max(capacity, m_SceneDrawCommands.Length * 2));
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
        m_StaticMeshCullingStats = default;
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

    private static Vector3 GetCameraPosition(RenderContext context)
    {
        if (context.CameraCount <= 0)
        {
            return Vector3.Zero;
        }

        return context.Cameras[0].Position;
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
