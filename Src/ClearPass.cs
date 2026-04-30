using ArisenEngine.Core.RHI;
using ArisenEngine.Core.Math;
using Arisen.Native.RHI;
using System;

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
        
        // 1.a Transition the image layout to COLOR_ATTACHMENT_OPTIMAL.
        // Dynamic rendering does not perform automatic layout transitions. We MUST transition it explicitly.
        commandBuffer.TransitionImageLayout(context.TargetImage, EImageLayout.IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL);

        // Phase 5: Pass specific recording
        // We use BeginRendering (Vulkan Dynamic Rendering) for the clear operation.
        
        // Diagnostic Log: Verify if this is actually running and what color it's using
        if (context.FrameIndex % 60 == 0)
        {
            ArisenEngine.Core.Diagnostics.Logger.Log($"[ClearPass] Record | Surface: 0x{context.SurfaceId:X} | Color: ({m_ClearColor.r:F2}, {m_ClearColor.g:F2}, {m_ClearColor.b:F2}, {m_ClearColor.a:F2}) | ImageView: 0x{colorImageView.Index:X}");
        }

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
