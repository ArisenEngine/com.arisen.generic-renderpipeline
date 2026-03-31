using Arisen.Native.RHI;
using ArisenEngine.Core.RHI;
using ArisenEngine.Core.Diagnostics;

namespace ArisenEngine.Rendering;

public class GenericRenderPipeline : RenderPipeline
{
    private RHICommandBufferPool? m_CommandPool;

    protected override void Render(RenderContext context, Camera[] cameras)
    {
       
    }

    protected override void OnDisposed()
    {
        
    }
}
