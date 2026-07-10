using Arisen.Native.RHI;
using ArisenEngine.Core.Math;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.RHI;
using ArisenEngine.Rendering.Resources;
using System.IO;

namespace ArisenEngine.Rendering;

public class GenericRenderPipeline : RenderPipeline
{
    private readonly Color m_ClearColor;
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly GenericRenderMaterialLibrary m_MaterialLibrary;
    private readonly StaticMeshPass m_StaticMeshPass;
    private readonly RenderResourceReloadQueue m_ReloadQueue = new();
    private readonly DeferredRenderResourceDisposalQueue m_DisposalQueue;
    private readonly Dictionary<Guid, RHIStaticMeshResource> m_SceneMeshes = new();
    private readonly Matrix4x4 m_FallbackStaticMeshLocalToWorld = Matrix4x4.CreateScale(1.24f, 1.24f, 1.0f);
    private MeshDrawCommand[] m_SceneDrawCommands = Array.Empty<MeshDrawCommand>();
    private int m_SceneDrawCommandCount;
    private RHIDevice m_LastDevice;
    private ulong m_LastSubmittedTicket;
    private RHIStaticMeshResource? m_TexturedQuadMesh;

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
        m_StaticMeshPass = new StaticMeshPass(assetDatabase, "GenericStaticMeshPass");
        m_AssetDatabase.AssetChanged += OnAssetChanged;
    }

    protected override void SetupGraph(RenderGraph graph, RenderContext context)
    {
        EnsureSmokeAssetsLoaded(context);
        var fallbackMaterial = m_MaterialLibrary.GetPreparedMaterial(m_MaterialLibrary.DefaultMaterialID);
        m_StaticMeshPass.SetStaticMeshResources(fallbackMaterial, m_TexturedQuadMesh!);
        m_MaterialLibrary.ApplyMaterialSlots(m_StaticMeshPass);
        m_StaticMeshPass.SetPreparedDraws(GetPreparedDraws(context));
        m_StaticMeshPass.SetViewProjection(GetViewProjection(context));
        m_StaticMeshPass.SetFallbackLocalToWorld(m_FallbackStaticMeshLocalToWorld);

        if (context.FrameIndex % 60 == 0)
        {
            ArisenEngine.Core.Diagnostics.Logger.Log($"[GenericRenderPipeline] SetupGraph | Frame: {context.FrameIndex} | Surface: 0x{context.SurfaceId:X} | Cameras: {context.CameraCount} | Draws: {context.DrawListCount} | ClearColor: {m_ClearColor}");
        }

        // 1. Clear writes the frame color target.
        graph.AddPass(
            new ClearPass(m_ClearColor, "GenericClearPass"),
            builder => builder.WriteColorAttachment(graph.FrameColor));

        // 2. Static mesh pass verifies imported mesh cooking, material binding, and draw submission.
        m_StaticMeshPass.Prepare(context);
        graph.AddPass(
            m_StaticMeshPass,
            builder => builder
                .ReadColorAttachment(graph.FrameColor)
                .WriteColorAttachment(graph.FrameColor)
                .WriteDepthAttachment(graph.FrameDepth));
    }

    protected override void OnDisposed()
    {
        m_AssetDatabase.AssetChanged -= OnAssetChanged;
        WaitForLastSubmittedFrame();
        m_DisposalQueue.Drain(m_LastDevice);
        m_StaticMeshPass.Dispose();
        m_MaterialLibrary.ReleasePreparedResources();
        ReleaseSceneMeshes(disposeImmediately: true);
        m_TexturedQuadMesh?.Dispose();
        m_TexturedQuadMesh = null;
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

        if (m_TexturedQuadMesh is { IsValid: true } && m_TexturedQuadMesh.IsSourceStale())
        {
            ArisenEngine.Core.Diagnostics.Logger.Log("[GenericRenderPipeline] Textured quad mesh dependency changed; reloading GPU mesh.");
            m_DisposalQueue.Enqueue(m_TexturedQuadMesh, m_LastSubmittedTicket);
            m_TexturedQuadMesh = null;
        }

        if (m_TexturedQuadMesh is not { IsValid: true })
        {
            var texturedQuadMesh = new MeshAsset(
                GenericRenderPipelineAssetRefs.TexturedQuadMesh.Ref.Guid,
                "GenericRP/TexturedQuad",
                MeshVariantKey.Default,
                MeshSourceFormat.WavefrontObj);
            m_TexturedQuadMesh = new RHIStaticMeshResource(device, m_AssetDatabase, texturedQuadMesh);
            ArisenEngine.Core.Diagnostics.Logger.Log(
                $"[GenericRenderPipeline] Loaded GPU mesh asset | Name: {texturedQuadMesh.Name} | Vertices: {m_TexturedQuadMesh.VertexCount} | Indices: {m_TexturedQuadMesh.IndexCount}");
        }

        PrepareSceneDrawCommands(device, context.StaticMeshItems);
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

        if (m_TexturedQuadMesh is { IsValid: true } &&
            ContainsGuid(dirtyGuids, GenericRenderPipelineAssetRefs.TexturedQuadMesh.Ref.Guid))
        {
            ArisenEngine.Core.Diagnostics.Logger.Log("[GenericRenderPipeline] Asset change invalidated textured quad mesh; releasing GPU mesh.");
            m_DisposalQueue.Enqueue(m_TexturedQuadMesh, m_LastSubmittedTicket);
            m_TexturedQuadMesh = null;
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

    private void PrepareSceneDrawCommands(RHIDevice device, ReadOnlySpan<StaticMeshRenderItem> items)
    {
        m_SceneDrawCommandCount = 0;
        if (items.IsEmpty)
        {
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
            int written = mesh.CreateDrawCommands(
                destination,
                item.LocalToWorld,
                materialId,
                firstSubmeshIndex,
                requestedSubmeshes);
            m_SceneDrawCommandCount += written;
        }

        if (m_SceneDrawCommandCount > 0)
        {
            Profiler.PlotValue("Render.SceneDrawCommandCount", m_SceneDrawCommandCount);
        }
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
}
