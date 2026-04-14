using Arisen.Native.RHI;
using ArisenEngine.Core.RHI;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Threading;
using ArisenKernel.Services;
using ArisenKernel.Lifecycle;
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
        // 1. Add the clear pass as the first step in the frame
        graph.AddPass(new ClearPass(m_ClearColor, "GenericClearPass"));
    }


    protected override void OnDisposed()
    {
        
    }
}
