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

        // Virtual surfaces (editor viewport) export their swapchain images via a Win32 NT handle
        // for D3D11/Avalonia composition. Vulkan->D3D11 requires an explicit queue-family
        // ownership acquire from VK_QUEUE_FAMILY_EXTERNAL at frame start, and a matching release
        // at frame end. Non-virtual surfaces (native windows) keep the in-family path.
        bool isSharedOutput = (context.SurfaceId & RHISystem.VirtualSurfaceIDMask) != 0;

        // 1.a Transition the image layout to COLOR_ATTACHMENT_OPTIMAL.
        // We use UNDEFINED as the old layout because we are clearing the entire image anyway.
        if (isSharedOutput)
        {
            // B11: For shared surfaces, we MUST acquire ownership back from External (D3D11)
            // if it was released in the previous frame. Using UNDEFINED as oldLayout safely
            // handles both the first frame and subsequent re-acquires.
            commandBuffer.TransitionImageLayout(context.TargetImage,
                EImageLayout.IMAGE_LAYOUT_UNDEFINED,
                EImageLayout.IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
                RHIQueueFamily.External, RHIQueueFamily.Ignored);
        }
        else
        {
            commandBuffer.TransitionImageLayout(context.TargetImage,
                EImageLayout.IMAGE_LAYOUT_UNDEFINED,
                EImageLayout.IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL);
        }

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

        // B11: Finalize Layout.
        // We leave the image in COLOR_ATTACHMENT_OPTIMAL. 
        // Subsequent passes (like GeometryPass) will continue writing to it.
        // The FINAL pass in the pipeline is responsible for transitioning to SHADER_READ_ONLY_OPTIMAL
        // and releasing ownership to EXTERNAL.
    }
}
