using ArisenEngine.Core.Math;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.RHI;
using ArisenEngine.Rendering.Resources;

namespace ArisenEngine.Rendering;

public class GenericRenderPipeline : RenderPipeline
{
    private static readonly Guid SmokeCheckerTextureGuid = Guid.Parse("c320bf66-0495-4e70-8f27-d54e90dd6c8d");
    private static readonly Guid SmokeTriangleMeshGuid = Guid.Parse("95a9e255-5ed4-48cb-ac65-7673a1002f9e");
    private static readonly Texture2DAsset SmokeCheckerTexture = new(
        SmokeCheckerTextureGuid,
        "GenericRP/SmokeChecker",
        Texture2DVariantKey.DefaultSRgb);
    private static readonly MeshAsset SmokeTriangleMesh = new(
        SmokeTriangleMeshGuid,
        "GenericRP/SmokeTriangle",
        MeshVariantKey.Default);

    private readonly Color m_ClearColor;
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly SmokeTrianglePass m_SmokeTrianglePass;
    private readonly GeometryPass m_GeometryPass = new("GenericGeometryPass");
    private RHITexture2DResource? m_SmokeCheckerTexture;
    private RHIStaticMeshResource? m_SmokeTriangleMesh;

    public GenericRenderPipeline(Color clearColor, IAssetDatabase assetDatabase)
    {
        m_ClearColor = clearColor;
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        m_SmokeTrianglePass = new SmokeTrianglePass(assetDatabase, "GenericSmokeTrianglePass");
    }

    protected override void SetupGraph(RenderGraph graph, RenderContext context)
    {
        EnsureSmokeAssetsLoaded(context.Device);
        m_SmokeTrianglePass.SetSmokeResources(m_SmokeCheckerTexture!, m_SmokeTriangleMesh!);

        if (context.FrameIndex % 60 == 0)
        {
            ArisenEngine.Core.Diagnostics.Logger.Log($"[GenericRenderPipeline] SetupGraph | Frame: {context.FrameIndex} | Surface: 0x{context.SurfaceId:X} | Cameras: {context.CameraCount} | Draws: {context.DrawListCount} | ClearColor: {m_ClearColor}");
        }

        // 1. Clear writes the frame color target.
        graph.AddPass(
            new ClearPass(m_ClearColor, "GenericClearPass"),
            builder => builder.Write(graph.FrameColor));

        // 2. Temporary smoke pass verifies shader compilation, pipeline state, and draw submission.
        // Production scene rendering should replace this once real draw data is available.
        m_SmokeTrianglePass.Prepare(context);
        graph.AddPass(
            m_SmokeTrianglePass,
            builder => builder.Read(graph.FrameColor).Write(graph.FrameColor));

        // 3. Geometry reads the updated frame color and writes scene content when available.
        // The RenderGraph derives Clear -> SmokeTriangle -> Geometry from these declarations.
        graph.AddPass(
            m_GeometryPass,
            builder => builder.Read(graph.FrameColor).Write(graph.FrameColor));
    }

    protected override void OnDisposed()
    {
        m_SmokeTrianglePass.Dispose();
        m_SmokeTriangleMesh?.Dispose();
        m_SmokeCheckerTexture?.Dispose();
        m_SmokeTriangleMesh = null;
        m_SmokeCheckerTexture = null;
    }

    private void EnsureSmokeAssetsLoaded(RHIDevice device)
    {
        if (m_SmokeCheckerTexture is not { IsValid: true })
        {
            m_SmokeCheckerTexture = new RHITexture2DResource(device, m_AssetDatabase, SmokeCheckerTexture);
            ArisenEngine.Core.Diagnostics.Logger.Log(
                $"[GenericRenderPipeline] Loaded GPU texture asset | Name: {SmokeCheckerTexture.Name} | Size: {m_SmokeCheckerTexture.Width}x{m_SmokeCheckerTexture.Height} | BindlessImage: {m_SmokeCheckerTexture.BindlessImageIndex} | BindlessSampler: {m_SmokeCheckerTexture.BindlessSamplerIndex}");
        }

        if (m_SmokeTriangleMesh is not { IsValid: true })
        {
            m_SmokeTriangleMesh = new RHIStaticMeshResource(device, m_AssetDatabase, SmokeTriangleMesh);
            ArisenEngine.Core.Diagnostics.Logger.Log(
                $"[GenericRenderPipeline] Loaded GPU mesh asset | Name: {SmokeTriangleMesh.Name} | Vertices: {m_SmokeTriangleMesh.VertexCount} | Indices: {m_SmokeTriangleMesh.IndexCount}");
        }
    }
}
