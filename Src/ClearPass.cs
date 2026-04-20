using ArisenEngine.Core.RHI;
using ArisenEngine.Core.Math;
using Arisen.Native.RHI;
using System.Diagnostics;

namespace ArisenEngine.Rendering;

/// <summary>
/// A simple pass that clears the current render target.
/// </summary>
public sealed class ClearPass : RenderPassNode
{
    private readonly Color m_ClearColor;

    public ClearPass(Color color, string name = "ClearPass") : base(name)
    {
        m_ClearColor = color;
    }

    protected override void Record(RenderContext context, RHICommandBuffer commandBuffer)
    {
        // 1. Begin dynamic rendering (modern Vulkan/RHI path)
        // We use the SwapChain's current image view for clearing
        var colorImageView = context.SwapChain.GetImageView(context.FrameIndex);
        
        commandBuffer.BeginRendering(
            colorImageView, 
            EImageLayout.IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
            EAttachmentLoadOp.ATTACHMENT_LOAD_OP_CLEAR,
            EAttachmentStoreOp.ATTACHMENT_STORE_OP_STORE,
            m_ClearColor.r, m_ClearColor.g, m_ClearColor.b, m_ClearColor.a,
            0, 0, context.Width, context.Height
        );

        // 2. End rendering
        commandBuffer.EndRendering();
    }
}
