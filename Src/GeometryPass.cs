using Arisen.Native.RHI;
using ArisenEngine.Core.RHI;
using System;

namespace ArisenEngine.Rendering;

/// <summary>
/// A rendering pass that draws all opaque geometry collected during the ECS update.
/// </summary>
public sealed class GeometryPass : RenderPassNode
{
    public GeometryPass(string name = "GeometryPass") : base(name)
    {
    }

    protected override void Record(RenderContext context, RHICommandBuffer commandBuffer)
    {
        if (!context.DrawList.IsEmpty)
        {
            // 1. Begin rendering with "Load" Op for color (preserving the Clear pass results)
            var colorImageView = context.SwapChain.GetImageView(context.FrameIndex);
            
            commandBuffer.BeginRendering(
                colorImageView,
                EImageLayout.IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
                EAttachmentLoadOp.ATTACHMENT_LOAD_OP_LOAD,
                EAttachmentStoreOp.ATTACHMENT_STORE_OP_STORE,
                0, 0, 0, 0, // Clear values are ignored since we use ATTACHMENT_LOAD_OP_LOAD
                0, 0, context.Width, context.Height
            );

            // 2. Set dynamic state
            commandBuffer.SetViewport(0, 0, context.Width, context.Height);
            commandBuffer.SetScissor(0, 0, context.Width, context.Height);

            // 3. Iterate over the Draw List and issue draw calls
            foreach (ref readonly var cmd in context.DrawList)
            {
                if (!cmd.VertexBuffer.IsValid) continue;

                // In a full implementation, we would bind the material's pipeline here.
                // For the first draw call, we assume a default state is already set or 
                // handled by the RenderGraph's pass setup.
                
                // TODO: BindPipeline(cmd.Pipeline);
                // TODO: PushConstants(cmd.LocalToWorld);

                commandBuffer.BindVertexBuffers(cmd.VertexBuffer);
                if (cmd.IndexBuffer.IsValid)
                {
                    commandBuffer.BindIndexBuffer(cmd.IndexBuffer, 0, cmd.IndexType);
                    commandBuffer.DrawIndexed(cmd.IndexCount);
                }
            }

            // 4. End rendering
            commandBuffer.EndRendering();
        }

        // 5. IMPORTANT: Transition the image layout to SHADER_READ_ONLY_OPTIMAL for Avalonia / Presentation.
        // We MUST transition from COLOR_ATTACHMENT_OPTIMAL to SHADER_READ_ONLY_OPTIMAL
        // to ensure the pixels we just rendered (or cleared) are preserved.
        commandBuffer.TransitionImageLayout(context.TargetImage, EImageLayout.IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL, EImageLayout.IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL);
    }
}
