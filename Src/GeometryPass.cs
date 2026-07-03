using Arisen.Native.RHI;
using ArisenEngine.Core.RHI;

namespace ArisenEngine.Rendering;

/// <summary>
/// A rendering pass that draws all opaque geometry collected during the ECS update.
/// </summary>
public sealed class GeometryPass : RenderPassNode
{
    private const int DrawsPerWorkItem = 256;

    public GeometryPass(string name = "GeometryPass") : base(name)
    {
    }

    protected override int GetWorkItemCount(RenderContext context)
    {
        var drawCount = context.DrawListCount;
        if (drawCount <= 0)
        {
            return 0;
        }

        return Math.Max(1, (drawCount + DrawsPerWorkItem - 1) / DrawsPerWorkItem);
    }

    protected override RenderPassWorkItem GetWorkItem(RenderContext context, int workItemIndex)
    {
        var drawStart = workItemIndex * DrawsPerWorkItem;
        var remaining = context.DrawListCount - drawStart;
        var drawCount = Math.Min(DrawsPerWorkItem, remaining);
        return RenderPassWorkItem.DrawRange(workItemIndex, drawStart, drawCount);
    }

    protected override void Record(RenderContext context, RenderCommandList commandList)
    {
        RecordDrawRange(context, commandList, 0, context.DrawListCount);
    }

    protected override void Record(RenderContext context, RenderCommandList commandList, RenderPassWorkItem workItem)
    {
        if (!workItem.HasDrawRange)
        {
            return;
        }

        RecordDrawRange(context, commandList, workItem.DrawStart, workItem.DrawCount);
    }

    private static void RecordDrawRange(RenderContext context, RenderCommandList commandList, int drawStart, int drawCount)
    {
        if (drawCount <= 0)
        {
            return;
        }

        var drawList = context.DrawList;
        var drawEnd = Math.Min(drawList.Length, drawStart + drawCount);
        if (drawStart < 0 || drawStart >= drawEnd)
        {
            return;
        }

        // Begin rendering with "Load" Op for color, preserving previous pass results.
        var colorImageView = context.SwapChain.GetImageView(context.FrameIndex);

        commandList.BeginRendering(
            colorImageView,
            EImageLayout.IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
            EAttachmentLoadOp.ATTACHMENT_LOAD_OP_LOAD,
            EAttachmentStoreOp.ATTACHMENT_STORE_OP_STORE,
            0, 0, 0, 0,
            0, 0, context.Width, context.Height);

        commandList.SetViewport(0, 0, context.Width, context.Height);
        commandList.SetScissor(0, 0, context.Width, context.Height);

        for (int i = drawStart; i < drawEnd; i++)
        {
            ref readonly var cmd = ref drawList[i];
            if (!cmd.VertexBuffer.IsValid) continue;

            commandList.BindVertexBuffers(cmd.VertexBuffer);
            if (cmd.IndexBuffer.IsValid)
            {
                commandList.BindIndexBuffer(cmd.IndexBuffer, 0, cmd.IndexType);
                commandList.DrawIndexed(cmd.IndexCount);
            }
        }

        commandList.EndRendering();
        // Output finalization is owned by FinalOutputPass so geometry remains a pure scene pass.
    }
}
