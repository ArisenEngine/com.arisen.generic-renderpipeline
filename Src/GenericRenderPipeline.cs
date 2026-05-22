using ArisenEngine.Core.Math;

namespace ArisenEngine.Rendering;

public class GenericRenderPipeline : RenderPipeline
{
    private readonly Color m_ClearColor;

    public GenericRenderPipeline(Color clearColor)
    {
        m_ClearColor = clearColor;
    }

    protected override void SetupGraph(RenderGraph graph, RenderContext context, ReadOnlySpan<Camera> cameras)
    {
        if (context.FrameIndex % 60 == 0)
        {
            ArisenEngine.Core.Diagnostics.Logger.Log($"[GenericRenderPipeline] SetupGraph | Frame: {context.FrameIndex} | Surface: 0x{context.SurfaceId:X} | ClearColor: {m_ClearColor}");
        }

                        // 1. Clear writes the frame color target.
        graph.AddPass(
            new ClearPass(m_ClearColor, "GenericClearPass"),
            builder => builder.Write(graph.FrameColor));

        // 2. Geometry reads the cleared frame color and writes updated color.
        // The RenderGraph derives Clear -> Geometry from these resource declarations.
        graph.AddPass(
            new GeometryPass("GenericGeometryPass"),
            builder => builder.Read(graph.FrameColor).Write(graph.FrameColor));
    }

    protected override void OnDisposed()
    {
        
    }
}
