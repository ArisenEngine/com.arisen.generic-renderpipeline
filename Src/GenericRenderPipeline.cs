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
    private RHICommandBufferPool? m_CommandPool;

    protected override ulong Render(RenderContext context, ReadOnlySpan<Camera> cameras)
    {
        // 1. Get the TaskGraph system for parallel recording
        var taskSystem = EngineKernel.Instance.Services.GetService<ITaskGraph>();
        if (taskSystem == null) return 0;

        // 2. Build the RenderGraph (In a real scenario, this is cached)
        using var renderGraph = new RenderGraph(taskSystem);

        // 3. Add passes
        var clear = renderGraph.AddPass(new ClearPass(new Color(0.1f, 0.1f, 0.1f, 1.0f)));
        
        // 4. Compile and Execute the graph
        return renderGraph.Execute(context);
    }


    protected override void OnDisposed()
    {
        
    }
}
