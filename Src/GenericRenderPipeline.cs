using ArisenEngine.Core.Math;

namespace ArisenEngine.Rendering;

public class GenericRenderPipeline : RenderPipeline
{
    private readonly Color m_ClearColor;
    private readonly SmokeTrianglePass m_SmokeTrianglePass = new("GenericSmokeTrianglePass");
    private readonly GeometryPass m_GeometryPass = new("GenericGeometryPass");

    public GenericRenderPipeline(Color clearColor)
    {
        m_ClearColor = clearColor;
    }

    protected override void SetupGraph(RenderGraph graph, RenderContext context)
    {
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
    }
}
