using Arisen.Native.RHI;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.Math;
using ArisenEngine.Core.RHI;
using ArisenEngine.Rendering.Resources;
using System.Runtime.InteropServices;

namespace ArisenEngine.Rendering;

/// <summary>
/// First static mesh pass that proves shader/material-backed indexed mesh submission.
/// </summary>
public sealed class StaticMeshPass : RenderPassNode, IDisposable
{
    private const ulong DynamicViewportScissorMask = 0x1UL | 0x2UL;
    private const string VertexStage = "Vertex";
    private const string FragmentStage = "Fragment";
    private const int DrawsPerWorkItem = 256;
    private const EFormat DepthTargetFormat = EFormat.FORMAT_D32_SFLOAT;
    private const uint InvalidBindlessIndex = 0xFFFFFFFFu;
    private const int DefaultObjectDataRingSize = 2;

    private readonly IAssetDatabase m_AssetDatabase;
    private RHIFactory m_Factory;
    private Matrix4x4 m_ViewProjection = Matrix4x4.Identity;
    private Matrix4x4 m_FallbackLocalToWorld = Matrix4x4.Identity;
    private StaticMeshMaterialConstants m_FallbackMaterialConstants = StaticMeshMaterialConstants.Default;
    private StaticMeshMaterialSlot[] m_MaterialSlots = Array.Empty<StaticMeshMaterialSlot>();
    private StaticMeshPipelineBatch[] m_PipelineBatches = Array.Empty<StaticMeshPipelineBatch>();
    private StaticMeshDrawBatch[] m_DrawBatches = Array.Empty<StaticMeshDrawBatch>();
    private StaticMeshBatchWorkItem[] m_WorkItems = Array.Empty<StaticMeshBatchWorkItem>();
    private MeshDrawCommand[] m_PreparedDraws = Array.Empty<MeshDrawCommand>();
    private StaticMeshObjectData[] m_ObjectData = Array.Empty<StaticMeshObjectData>();
    private int[] m_BatchDrawCounts = Array.Empty<int>();
    private int[] m_BatchWriteOffsets = Array.Empty<int>();
    private int[] m_BatchedDrawIndices = Array.Empty<int>();
    private StaticMeshDrawConstants m_FallbackDrawConstants = StaticMeshDrawConstants.Identity;
    private StaticMeshObjectBufferSlot[] m_ObjectDataBufferSlots = Array.Empty<StaticMeshObjectBufferSlot>();
    private RHIBufferHandle m_ObjectDataBuffer = RHIBufferHandle.Invalid;
    private uint m_ObjectDataBufferBindlessIndex = InvalidBindlessIndex;
    private int m_ObjectDataBufferCapacity;
    private int m_ObjectDataCount;
    private int m_ObjectDataRingSize;
    private int m_ObjectDataSlotIndex;
    private RHIBufferHandle m_VertexBuffer = RHIBufferHandle.Invalid;
    private RHIBufferHandle m_IndexBuffer = RHIBufferHandle.Invalid;
    private EIndexType m_IndexType = EIndexType.INDEX_TYPE_UINT32;
    private uint m_FirstIndex;
    private uint m_IndexCount;
    private int m_VertexOffset;
    private EFormat m_ColorFormat = EFormat.FORMAT_UNDEFINED;
    private EFormat m_DepthFormat = EFormat.FORMAT_UNDEFINED;
    private RHIImageHandle m_DepthImage = RHIImageHandle.Invalid;
    private RHIImageViewHandle m_DepthImageView = RHIImageViewHandle.Invalid;
    private uint m_DepthWidth;
    private uint m_DepthHeight;
    private bool m_DepthNeedsInitialTransition;
    private bool m_RecordDepthInitialTransition;
    private int m_PipelineBatchCount;
    private int m_DrawBatchCount;
    private int m_WorkItemCount;
    private int m_PreparedDrawCount;
    private int m_FallbackPipelineBatchIndex = -1;
    private uint m_MaterialSlotVersion;
    private uint m_PreparedMaterialSlotVersion;
    private bool m_Disposed;

    public StaticMeshPass(IAssetDatabase assetDatabase, string name = "StaticMeshPass") : base(name)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
    }

    protected override int GetWorkItemCount(RenderContext context)
    {
        if (m_PreparedDrawCount <= 0)
        {
            return m_FallbackPipelineBatchIndex >= 0 ? 1 : 0;
        }

        return m_WorkItemCount;
    }

    protected override RenderPassWorkItem GetWorkItem(RenderContext context, int workItemIndex)
    {
        if (m_PreparedDrawCount <= 0)
        {
            return RenderPassWorkItem.Pass(workItemIndex);
        }

        if ((uint)workItemIndex >= (uint)m_WorkItemCount)
        {
            return RenderPassWorkItem.Pass(workItemIndex);
        }

        var batchWorkItem = m_WorkItems[workItemIndex];
        return RenderPassWorkItem.DrawRange(workItemIndex, batchWorkItem.DrawIndexStart, batchWorkItem.DrawIndexCount);
    }

    protected override void Record(RenderContext context, RenderCommandList commandList)
    {
        if (m_PreparedDrawCount > 0)
        {
            RecordPreparedDrawBatches(context, commandList);
            return;
        }

        RecordFallbackMesh(context, commandList);
    }

    private void RecordPreparedDrawBatches(RenderContext context, RenderCommandList commandList)
    {
        for (int i = 0; i < m_WorkItemCount; i++)
        {
            RecordBatchWorkItem(context, commandList, m_WorkItems[i], i == 0);
        }
    }

    protected override void Record(RenderContext context, RenderCommandList commandList, RenderPassWorkItem workItem)
    {
        if (workItem.HasDrawRange)
        {
            RecordDrawRange(context, commandList, workItem);
            return;
        }

        RecordFallbackMesh(context, commandList);
    }

    private void RecordFallbackMesh(RenderContext context, RenderCommandList commandList)
    {
        if (m_FallbackPipelineBatchIndex < 0 ||
            m_FallbackPipelineBatchIndex >= m_PipelineBatchCount)
        {
            return;
        }

        ref readonly var pipelineBatch = ref m_PipelineBatches[m_FallbackPipelineBatchIndex];
        if (!pipelineBatch.IsValid)
        {
            return;
        }

        var colorImageView = context.SwapChain.GetImageView(context.FrameIndex);

        if (context.FrameIndex % 60 == 0)
        {
            Logger.Log(
                $"[StaticMeshPass] RecordFallback | Surface: 0x{context.SurfaceId:X} | Size: {context.Width}x{context.Height} | Pipeline: {pipelineBatch.Pipeline.Index}:{pipelineBatch.Pipeline.Generation}");
        }

        BeginStaticMeshRendering(context, commandList, colorImageView, clearDepth: true);
        RecordObjectDataBarrier(commandList);

        commandList.BindPipeline(pipelineBatch.Pipeline);
        commandList.PushConstants(
            m_FallbackDrawConstants,
            EShaderStage.SHADER_STAGE_VERTEX_BIT | EShaderStage.SHADER_STAGE_FRAGMENT_BIT);
        commandList.SetViewport(0, 0, context.Width, context.Height);
        commandList.SetScissor(0, 0, context.Width, context.Height);
        commandList.BindVertexBuffers(m_VertexBuffer);
        commandList.BindIndexBuffer(m_IndexBuffer, 0, m_IndexType);
        commandList.DrawIndexed(m_IndexCount, firstIndex: m_FirstIndex, vertexOffset: m_VertexOffset);

        commandList.EndRendering();
    }

    private void RecordDrawRange(RenderContext context, RenderCommandList commandList, RenderPassWorkItem workItem)
    {
        if (workItem.DrawCount <= 0)
        {
            return;
        }

        int workItemIndex = workItem.Index;
        if ((uint)workItemIndex >= (uint)m_WorkItemCount)
        {
            return;
        }

        var batchWorkItem = m_WorkItems[workItemIndex];
        if (batchWorkItem.DrawIndexStart != workItem.DrawStart ||
            batchWorkItem.DrawIndexCount != workItem.DrawCount)
        {
            return;
        }

        RecordBatchWorkItem(context, commandList, batchWorkItem, workItemIndex == 0);
    }

    private void RecordBatchWorkItem(
        RenderContext context,
        RenderCommandList commandList,
        StaticMeshBatchWorkItem workItem,
        bool firstWorkItem)
    {
        if ((uint)workItem.PipelineBatchIndex >= (uint)m_PipelineBatchCount ||
            workItem.DrawIndexCount <= 0)
        {
            return;
        }

        ref readonly var pipelineBatch = ref m_PipelineBatches[workItem.PipelineBatchIndex];
        if (!pipelineBatch.IsValid)
        {
            return;
        }

        if (context.FrameIndex % 60 == 0 && workItem.DrawIndexStart == 0)
        {
            Logger.Log(
                $"[StaticMeshPass] RecordDrawBatches | Surface: 0x{context.SurfaceId:X} | Draws: {m_PreparedDrawCount} | Batches: {m_DrawBatchCount} | WorkItems: {m_WorkItemCount}");
        }

        if (context.FrameIndex % 60 == 0)
        {
            LogBatchDrawCount(workItem);
        }

        int preparedDrawCount = m_PreparedDrawCount;
        var drawIndexEnd = Math.Min(m_BatchedDrawIndices.Length, workItem.DrawIndexStart + workItem.DrawIndexCount);
        var colorImageView = context.SwapChain.GetImageView(context.FrameIndex);

        BeginStaticMeshRendering(context, commandList, colorImageView, clearDepth: firstWorkItem);
        RecordObjectDataBarrier(commandList);

        commandList.BindPipeline(pipelineBatch.Pipeline);
        commandList.SetViewport(0, 0, context.Width, context.Height);
        commandList.SetScissor(0, 0, context.Width, context.Height);

        for (int i = workItem.DrawIndexStart; i < drawIndexEnd; i++)
        {
            int drawIndex = m_BatchedDrawIndices[i];
            if ((uint)drawIndex >= (uint)preparedDrawCount)
            {
                continue;
            }

            ref readonly var draw = ref m_PreparedDraws[drawIndex];
            if (!draw.VertexBuffer.IsValid ||
                !draw.IndexBuffer.IsValid ||
                draw.IndexCount == 0)
            {
                continue;
            }

            var materialConstants = GetMaterialConstants(draw.MaterialID);
            var constants = StaticMeshDrawConstants.From(
                materialConstants.ImageIndex,
                materialConstants.SamplerIndex,
                materialConstants.BaseColorFactor,
                m_ObjectDataBufferBindlessIndex,
                checked((uint)drawIndex));

            commandList.PushConstants(
                constants,
                EShaderStage.SHADER_STAGE_VERTEX_BIT | EShaderStage.SHADER_STAGE_FRAGMENT_BIT);
            commandList.BindVertexBuffers(draw.VertexBuffer);
            commandList.BindIndexBuffer(draw.IndexBuffer, 0, draw.IndexType);
            commandList.DrawIndexed(draw.IndexCount, firstIndex: draw.FirstIndex, vertexOffset: draw.VertexOffset);
        }

        commandList.EndRendering();
    }

    private void BeginStaticMeshRendering(
        RenderContext context,
        RenderCommandList commandList,
        RHIImageViewHandle colorImageView,
        bool clearDepth)
    {
        if (clearDepth && m_RecordDepthInitialTransition && m_DepthImage.IsValid)
        {
            commandList.TransitionImageLayout(
                m_DepthImage,
                EImageLayout.IMAGE_LAYOUT_UNDEFINED,
                EImageLayout.IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL);
        }

        if (!m_DepthImageView.IsValid)
        {
            commandList.BeginRendering(
                colorImageView,
                EImageLayout.IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
                EAttachmentLoadOp.ATTACHMENT_LOAD_OP_LOAD,
                EAttachmentStoreOp.ATTACHMENT_STORE_OP_STORE,
                0, 0, 0, 0,
                0, 0, context.Width, context.Height);
            return;
        }

        commandList.BeginRendering(
            colorImageView,
            EImageLayout.IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
            EAttachmentLoadOp.ATTACHMENT_LOAD_OP_LOAD,
            EAttachmentStoreOp.ATTACHMENT_STORE_OP_STORE,
            0, 0, 0, 0,
            m_DepthImageView,
            EImageLayout.IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL,
            clearDepth ? EAttachmentLoadOp.ATTACHMENT_LOAD_OP_CLEAR : EAttachmentLoadOp.ATTACHMENT_LOAD_OP_LOAD,
            EAttachmentStoreOp.ATTACHMENT_STORE_OP_STORE,
            1.0f,
            0,
            0, 0, context.Width, context.Height);
    }

    public void Prepare(RenderContext context)
    {
        var factory = context.Device.GetFactory();
        var colorFormat = factory.GetImageViewFormat(context.SwapChain.GetImageView(context.FrameIndex));
        var depthFormat = DepthTargetFormat;

        EnsureDepthTarget(factory, context.Width, context.Height, depthFormat);
        EnsurePipelineBatches(context, colorFormat, depthFormat);
        BuildDrawBatches(context);
        PrepareObjectDataBuffer(context, factory);
        PlotBatchDiagnostics();
    }

    public void SetPreparedDraws(ReadOnlySpan<MeshDrawCommand> drawCommands)
    {
        if (drawCommands.Length > m_PreparedDraws.Length)
        {
            Array.Resize(ref m_PreparedDraws, drawCommands.Length);
        }

        drawCommands.CopyTo(m_PreparedDraws);
        m_PreparedDrawCount = drawCommands.Length;
    }

    private void EnsureDepthTarget(RHIFactory factory, uint width, uint height, EFormat depthFormat)
    {
        m_Factory = factory;

        if (m_DepthImage.IsValid &&
            m_DepthImageView.IsValid &&
            m_DepthWidth == width &&
            m_DepthHeight == height &&
            m_DepthFormat == depthFormat)
        {
            m_RecordDepthInitialTransition = m_DepthNeedsInitialTransition;
            m_DepthNeedsInitialTransition = false;
            return;
        }

        ReleaseDepthTargetResources();

        m_DepthWidth = width;
        m_DepthHeight = height;
        m_DepthFormat = depthFormat;
        m_DepthImage = factory.CreateImage(
            width,
            height,
            1,
            1,
            1,
            depthFormat,
            (uint)EImageUsageFlagBits.IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT,
            ERHIMemoryUsage.GpuOnly,
            "GenericStaticMeshDepth");
        if (!m_DepthImage.IsValid)
        {
            throw new InvalidOperationException("[StaticMeshPass] Failed to create depth image.");
        }

        m_DepthImageView = factory.CreateImageView(
            m_DepthImage,
            EImageViewType.IMAGE_VIEW_TYPE_2D,
            depthFormat,
            (uint)EImageAspectFlagBits.IMAGE_ASPECT_DEPTH_BIT,
            0,
            1,
            0,
            1);
        if (!m_DepthImageView.IsValid)
        {
            ReleaseDepthTargetResources();
            throw new InvalidOperationException("[StaticMeshPass] Failed to create depth image view.");
        }

        m_DepthNeedsInitialTransition = true;
        m_RecordDepthInitialTransition = true;
        Logger.Log(
            $"[StaticMeshPass] Created depth target | Size: {width}x{height} | Format: {depthFormat} | Image: {m_DepthImage.Index}:{m_DepthImage.Generation}");
    }

    private void EnsurePipelineBatches(RenderContext context, EFormat colorFormat, EFormat depthFormat)
    {
        if (m_PipelineBatchCount > 0 &&
            m_ColorFormat == colorFormat &&
            m_DepthFormat == depthFormat &&
            m_PreparedMaterialSlotVersion == m_MaterialSlotVersion)
        {
            return;
        }

        ReleasePipelineResources();

        m_Factory = context.Device.GetFactory();
        m_ColorFormat = colorFormat;
        m_DepthFormat = depthFormat;
        m_FallbackPipelineBatchIndex = -1;
        m_PipelineBatchCount = 0;

        var pipelineCache = context.Device.PipelineCache;
        for (int i = 0; i < m_MaterialSlots.Length; i++)
        {
            var slot = m_MaterialSlots[i];
            if (!slot.IsValid || slot.Shader == null)
            {
                continue;
            }

            var key = new StaticMeshPipelineKey(
                slot.Shader.Guid,
                slot.ShaderDependencyStamp,
                slot.Shader.GetVariantIdentity(),
                slot.RenderState,
                colorFormat,
                depthFormat);
            int batchIndex = FindPipelineBatch(key);
            if (batchIndex < 0)
            {
                batchIndex = CreatePipelineBatch(pipelineCache, key, slot.Shader);
            }

            slot.PipelineBatchIndex = batchIndex;
            m_MaterialSlots[i] = slot;

            if (i == 0)
            {
                m_FallbackPipelineBatchIndex = batchIndex;
            }
        }

        m_PreparedMaterialSlotVersion = m_MaterialSlotVersion;

        Logger.Log(
            $"[StaticMeshPass] Prepared material pipeline batches | Count: {m_PipelineBatchCount} | ColorFormat: {colorFormat} | DepthFormat: {depthFormat}");
    }

    private int CreatePipelineBatch(
        RHIPipelineCache pipelineCache,
        StaticMeshPipelineKey key,
        ShaderAsset shader)
    {
        if (m_PipelineBatches.Length <= m_PipelineBatchCount)
        {
            Array.Resize(ref m_PipelineBatches, Math.Max(4, m_PipelineBatches.Length * 2));
        }

        var batch = new StaticMeshPipelineBatch(key, shader);

        try
        {
            batch.VertexProgram = CompileProgram(
                m_Factory,
                EShaderStage.SHADER_STAGE_VERTEX_BIT,
                shader,
                VertexStage,
                m_AssetDatabase,
                out batch.VertexShaderAsset);
            batch.FragmentProgram = CompileProgram(
                m_Factory,
                EShaderStage.SHADER_STAGE_FRAGMENT_BIT,
                shader,
                FragmentStage,
                m_AssetDatabase,
                out batch.FragmentShaderAsset);

            batch.PipelineState = pipelineCache.GetPipelineState();
            batch.PipelineState.AddProgram(batch.VertexProgram);
            batch.PipelineState.AddProgram(batch.FragmentProgram);
            batch.PipelineState.SetBindPoint(EPipelineBindPoint.PIPELINE_BIND_POINT_GRAPHICS);
            batch.PipelineState.SetInputAssemblyState(EPrimitiveTopology.PRIMITIVE_TOPOLOGY_TRIANGLE_LIST);
            batch.PipelineState.ClearVertexInputDescriptions();
            batch.PipelineState.AddVertexBindingDescription(
                0,
                MeshAssetCooker.StaticMeshVertexStride,
                EVertexInputRate.VERTEX_INPUT_RATE_VERTEX);
            batch.PipelineState.AddVertexInputAttributeDescription(0, 0, EFormat.FORMAT_R32G32B32_SFLOAT, 0);
            batch.PipelineState.AddVertexInputAttributeDescription(1, 0, EFormat.FORMAT_R32G32B32_SFLOAT, 12);
            batch.PipelineState.AddVertexInputAttributeDescription(2, 0, EFormat.FORMAT_R32G32B32A32_SFLOAT, 24);
            batch.PipelineState.AddVertexInputAttributeDescription(3, 0, EFormat.FORMAT_R32G32_SFLOAT, 40);
            batch.PipelineState.AddVertexInputAttributeDescription(4, 0, EFormat.FORMAT_R32G32B32_SFLOAT, 48);
            batch.PipelineState.SetRasterizationState(
                EPolygonMode.EPOLYGON_MODE_FILL,
                key.RenderState.CullMode,
                key.RenderState.FrontFace);
            batch.PipelineState.SetColorBlendState(
                key.RenderState.BlendEnabled,
                key.RenderState.SrcColorBlendFactor,
                key.RenderState.DstColorBlendFactor,
                key.RenderState.ColorBlendOp);
            batch.PipelineState.SetDepthStencilState(
                key.DepthFormat != EFormat.FORMAT_UNDEFINED,
                key.DepthFormat != EFormat.FORMAT_UNDEFINED,
                ECompareOp.COMPARE_OP_LESS_OR_EQUAL);
            batch.PipelineState.SetDynamicStateMask(DynamicViewportScissorMask);
            batch.PipelineState.SetRenderingFormats(new[] { key.ColorFormat }, key.DepthFormat);
            batch.PipelineState.BuildDescriptorSetLayout();

            batch.Pipeline = pipelineCache.GetGraphicsPipeline(batch.PipelineState);
            if (!batch.Pipeline.IsValid)
            {
                throw new InvalidOperationException("[StaticMeshPass] Failed to create graphics pipeline.");
            }

            int batchIndex = m_PipelineBatchCount++;
            m_PipelineBatches[batchIndex] = batch;
            Logger.Log(
                $"[StaticMeshPass] Created pipeline batch | Batch: {batchIndex} | Shader: {key.ShaderGuid} | Format: {key.ColorFormat} | Cull: {key.RenderState.CullMode} | Blend: {key.RenderState.BlendEnabled} | Pipeline: {batch.Pipeline.Index}:{batch.Pipeline.Generation}");
            return batchIndex;
        }
        catch
        {
            ReleasePipelineBatchResources(ref batch);
            throw;
        }
    }

    private int FindPipelineBatch(StaticMeshPipelineKey key)
    {
        for (int i = 0; i < m_PipelineBatchCount; i++)
        {
            if (m_PipelineBatches[i].Key == key)
            {
                return i;
            }
        }

        return -1;
    }

    private void BuildDrawBatches(RenderContext context)
    {
        m_DrawBatchCount = 0;
        m_WorkItemCount = 0;

        if (m_PreparedDrawCount <= 0 || m_PipelineBatchCount <= 0)
        {
            return;
        }

        EnsureBatchScratchCapacity(m_PreparedDrawCount, m_PipelineBatchCount);
        EnsureDrawBatchCapacity(m_PipelineBatchCount);
        Array.Clear(m_BatchDrawCounts, 0, m_PipelineBatchCount);

        int preparedDrawCount = m_PreparedDrawCount;
        for (int i = 0; i < preparedDrawCount; i++)
        {
            ref readonly var draw = ref m_PreparedDraws[i];
            if (!IsDrawable(draw))
            {
                continue;
            }

            int batchIndex = GetMaterialPipelineBatchIndex(draw.MaterialID);
            if (batchIndex >= 0)
            {
                m_BatchDrawCounts[batchIndex]++;
            }
        }

        int drawIndexOffset = 0;
        for (int batchIndex = 0; batchIndex < m_PipelineBatchCount; batchIndex++)
        {
            int batchDrawCount = m_BatchDrawCounts[batchIndex];
            m_BatchWriteOffsets[batchIndex] = drawIndexOffset;
            if (batchDrawCount <= 0)
            {
                continue;
            }

            m_DrawBatches[m_DrawBatchCount++] = new StaticMeshDrawBatch(batchIndex, drawIndexOffset, batchDrawCount);
            drawIndexOffset += batchDrawCount;
        }

        for (int i = 0; i < preparedDrawCount; i++)
        {
            ref readonly var draw = ref m_PreparedDraws[i];
            if (!IsDrawable(draw))
            {
                continue;
            }

            int batchIndex = GetMaterialPipelineBatchIndex(draw.MaterialID);
            if (batchIndex < 0)
            {
                continue;
            }

            int writeIndex = m_BatchWriteOffsets[batchIndex]++;
            m_BatchedDrawIndices[writeIndex] = i;
        }

        BuildBatchWorkItems();
    }

    private void BuildBatchWorkItems()
    {
        m_WorkItemCount = 0;
        int workItemCapacity = 0;
        for (int i = 0; i < m_DrawBatchCount; i++)
        {
            int drawCount = m_DrawBatches[i].DrawCount;
            if (drawCount > 0)
            {
                workItemCapacity += (drawCount + DrawsPerWorkItem - 1) / DrawsPerWorkItem;
            }
        }

        EnsureWorkItemCapacity(workItemCapacity);
        for (int i = 0; i < m_DrawBatchCount; i++)
        {
            var batch = m_DrawBatches[i];
            int remaining = batch.DrawCount;
            int drawIndexStart = batch.DrawIndexStart;
            while (remaining > 0)
            {
                int workDrawCount = Math.Min(DrawsPerWorkItem, remaining);
                m_WorkItems[m_WorkItemCount++] = new StaticMeshBatchWorkItem(
                    batch.PipelineBatchIndex,
                    drawIndexStart,
                    workDrawCount);
                drawIndexStart += workDrawCount;
                remaining -= workDrawCount;
            }
        }
    }

    private void EnsureDrawBatchCapacity(int batchCount)
    {
        if (m_DrawBatches.Length < batchCount)
        {
            Array.Resize(ref m_DrawBatches, Math.Max(4, batchCount));
        }
    }

    private void EnsureWorkItemCapacity(int workItemCount)
    {
        if (m_WorkItems.Length < workItemCount)
        {
            Array.Resize(ref m_WorkItems, Math.Max(4, workItemCount));
        }
    }

    private void EnsureBatchScratchCapacity(int drawCount, int pipelineBatchCount)
    {
        if (m_BatchedDrawIndices.Length < drawCount)
        {
            Array.Resize(ref m_BatchedDrawIndices, drawCount);
        }

        if (m_BatchDrawCounts.Length < pipelineBatchCount)
        {
            Array.Resize(ref m_BatchDrawCounts, pipelineBatchCount);
        }

        if (m_BatchWriteOffsets.Length < pipelineBatchCount)
        {
            Array.Resize(ref m_BatchWriteOffsets, pipelineBatchCount);
        }
    }

    private void PlotBatchDiagnostics()
    {
        int effectiveDrawCount = m_PreparedDrawCount;
        if (effectiveDrawCount == 0 && m_FallbackPipelineBatchIndex >= 0)
        {
            effectiveDrawCount = 1;
        }

        int activeBatchCount = m_DrawBatchCount;
        if (activeBatchCount == 0 && m_FallbackPipelineBatchIndex >= 0)
        {
            activeBatchCount = 1;
        }

        Profiler.PlotValue("StaticMeshPass.DrawCount", effectiveDrawCount);
        Profiler.PlotValue("StaticMeshPass.MaterialBatchCount", activeBatchCount);
        Profiler.PlotValue("StaticMeshPass.PipelineBatchCount", m_PipelineBatchCount);
        Profiler.PlotValue("StaticMeshPass.WorkItemCount", m_WorkItemCount);
        Profiler.PlotValue("StaticMeshPass.ObjectDataCount", m_ObjectDataCount);
        Profiler.PlotValue("StaticMeshPass.ObjectDataCapacity", m_ObjectDataBufferCapacity);
        Profiler.PlotValue("StaticMeshPass.ObjectDataRingSize", m_ObjectDataRingSize);
    }

    private void LogBatchDrawCount(StaticMeshBatchWorkItem workItem)
    {
        for (int i = 0; i < m_DrawBatchCount; i++)
        {
            var batch = m_DrawBatches[i];
            if (batch.PipelineBatchIndex == workItem.PipelineBatchIndex &&
                batch.DrawIndexStart == workItem.DrawIndexStart)
            {
                Logger.Log(
                    $"[StaticMeshPass] Material batch | PipelineBatch: {batch.PipelineBatchIndex} | Draws: {batch.DrawCount}");
                return;
            }
        }
    }

    public void SetStaticMeshResources(RHIMaterialResource material, RHIStaticMeshResource mesh)
    {
        if (!material.IsValid)
        {
            throw new InvalidOperationException("[StaticMeshPass] Material is not ready for rendering.");
        }

        if (!mesh.IsValid)
        {
            throw new InvalidOperationException("[StaticMeshPass] Mesh is not ready for indexed drawing.");
        }

        var materialConstants = CreateMaterialConstants(material);
        m_FallbackMaterialConstants = materialConstants;
        SetMaterialSlot(0, material);
        m_FallbackDrawConstants = StaticMeshDrawConstants.From(
            materialConstants.ImageIndex,
            materialConstants.SamplerIndex,
            materialConstants.BaseColorFactor,
            m_ObjectDataBufferBindlessIndex,
            0);
        m_VertexBuffer = mesh.VertexBuffer;
        m_IndexBuffer = mesh.IndexBuffer;
        m_IndexType = mesh.IndexType;
        var submesh = mesh.GetSubmeshOrDefault(0);
        m_FirstIndex = submesh.FirstIndex;
        m_IndexCount = submesh.IndexCount;
        m_VertexOffset = submesh.VertexOffset;
    }

    public void SetMaterialSlot(uint materialId, RHIMaterialResource material)
    {
        if (!material.IsValid)
        {
            throw new InvalidOperationException("[StaticMeshPass] Material is not ready for rendering.");
        }

        var slotIndex = checked((int)materialId);
        if (m_MaterialSlots.Length <= slotIndex)
        {
            Array.Resize(ref m_MaterialSlots, slotIndex + 1);
        }

        int previousPipelineBatchIndex = m_MaterialSlots[slotIndex].IsValid
            ? m_MaterialSlots[slotIndex].PipelineBatchIndex
            : -1;
        var slot = new StaticMeshMaterialSlot(
            material.Shader,
            material.ShaderDependencyStamp,
            material.RenderState,
            CreateMaterialConstants(material),
            previousPipelineBatchIndex);

        if (m_MaterialSlots[slotIndex].Equals(slot))
        {
            return;
        }

        m_MaterialSlots[slotIndex] = slot;
        m_MaterialSlotVersion++;
    }

    public void SetViewProjection(Matrix4x4 viewProjection)
    {
        m_ViewProjection = viewProjection;
        RefreshFallbackDrawConstants();
    }

    public void SetFallbackLocalToWorld(Matrix4x4 localToWorld)
    {
        m_FallbackLocalToWorld = localToWorld;
        RefreshFallbackDrawConstants();
    }

    private static RHIShaderProgramHandle CompileProgram(
        RHIFactory factory,
        EShaderStage rhiStage,
        ShaderAsset shader,
        string stageName,
        IAssetDatabase assetDatabase,
        out CookedAssetHandle shaderAssetHandle)
    {
        shaderAssetHandle = CookedAssetHandle.Invalid;
        RHIShaderProgramHandle program = RHIShaderProgramHandle.Invalid;

        try
        {
            var cookedStage = ShaderAssetCooker.LoadOrCookStage(assetDatabase, shader, stageName);
            shaderAssetHandle = cookedStage.Handle;
            var shaderCode = assetDatabase.GetCookedAssetBytes(shaderAssetHandle);

            program = factory.CreateGPUProgram();
            if (!program.IsValid)
            {
                throw new InvalidOperationException($"[StaticMeshPass] Failed to allocate shader program for {cookedStage.Stage.EntryPoint}.");
            }

            if (!factory.AttachProgramByteCode(program, rhiStage, shaderCode, cookedStage.Stage.EntryPoint))
            {
                throw new InvalidOperationException($"[StaticMeshPass] Failed to attach shader bytecode for {cookedStage.Stage.EntryPoint}.");
            }

            return program;
        }
        catch
        {
            if (program.IsValid)
            {
                factory.ReleaseGPUProgram(program);
            }

            if (shaderAssetHandle.IsValid)
            {
                assetDatabase.Release(shaderAssetHandle);
                shaderAssetHandle = CookedAssetHandle.Invalid;
            }

            throw;
        }
    }

    private void ReleasePipelineResources()
    {
        for (int i = 0; i < m_PipelineBatchCount; i++)
        {
            ReleasePipelineBatchResources(ref m_PipelineBatches[i]);
            m_PipelineBatches[i] = default;
        }

        m_PipelineBatchCount = 0;
        m_DrawBatchCount = 0;
        m_WorkItemCount = 0;
        m_FallbackPipelineBatchIndex = -1;
        m_ColorFormat = EFormat.FORMAT_UNDEFINED;
    }

    private void ReleaseDepthTargetResources()
    {
        if (m_Factory.IsValid)
        {
            if (m_DepthImageView.IsValid)
            {
                m_Factory.ReleaseImageView(m_DepthImageView);
            }

            if (m_DepthImage.IsValid)
            {
                m_Factory.ReleaseImage(m_DepthImage);
            }
        }

        m_DepthImage = RHIImageHandle.Invalid;
        m_DepthImageView = RHIImageViewHandle.Invalid;
        m_DepthWidth = 0;
        m_DepthHeight = 0;
        m_DepthFormat = EFormat.FORMAT_UNDEFINED;
        m_DepthNeedsInitialTransition = false;
        m_RecordDepthInitialTransition = false;
    }

    private void PrepareObjectDataBuffer(RenderContext context, RHIFactory factory)
    {
        int objectCount = m_PreparedDrawCount > 0
            ? m_PreparedDrawCount
            : m_FallbackPipelineBatchIndex >= 0 ? 1 : 0;

        m_ObjectDataCount = objectCount;
        if (objectCount <= 0)
        {
            m_ObjectDataBuffer = RHIBufferHandle.Invalid;
            m_ObjectDataBufferBindlessIndex = InvalidBindlessIndex;
            m_ObjectDataBufferCapacity = 0;
            return;
        }

        EnsureObjectDataRing(factory, GetObjectDataRingSize(context));
        m_ObjectDataSlotIndex = checked((int)(context.FrameIndex % (uint)m_ObjectDataRingSize));

        EnsureObjectDataCapacity(objectCount);
        if (m_PreparedDrawCount > 0)
        {
            int preparedDrawCount = m_PreparedDrawCount;
            for (int i = 0; i < preparedDrawCount; i++)
            {
                m_ObjectData[i] = StaticMeshObjectData.From(m_PreparedDraws[i].LocalToWorld, m_ViewProjection);
            }
        }
        else
        {
            m_ObjectData[0] = StaticMeshObjectData.From(m_FallbackLocalToWorld, m_ViewProjection);
        }

        EnsureObjectDataBuffer(factory, m_ObjectDataSlotIndex, objectCount);
        UploadObjectData(objectCount);

        if (m_PreparedDrawCount <= 0)
        {
            RefreshFallbackDrawConstants();
        }
    }

    private static int GetObjectDataRingSize(RenderContext context)
    {
        var instance = context.Device.GetInstance();
        var maxFramesInFlight = instance.MaxFramesInFlight;
        return maxFramesInFlight == 0
            ? DefaultObjectDataRingSize
            : checked((int)Math.Max(1u, maxFramesInFlight));
    }

    private void EnsureObjectDataRing(RHIFactory factory, int ringSize)
    {
        if (ringSize <= 0)
        {
            ringSize = DefaultObjectDataRingSize;
        }

        if (m_ObjectDataBufferSlots.Length == ringSize && m_ObjectDataRingSize == ringSize)
        {
            return;
        }

        ReleaseObjectDataBuffers();
        m_ObjectDataBufferSlots = new StaticMeshObjectBufferSlot[ringSize];
        m_ObjectDataRingSize = ringSize;
        Logger.Log($"[StaticMeshPass] Initialized object data buffer ring | Slots: {ringSize}");
    }

    private void EnsureObjectDataCapacity(int objectCount)
    {
        if (m_ObjectData.Length < objectCount)
        {
            Array.Resize(ref m_ObjectData, objectCount);
        }
    }

    private unsafe void EnsureObjectDataBuffer(RHIFactory factory, int slotIndex, int objectCount)
    {
        ref var slot = ref m_ObjectDataBufferSlots[slotIndex];
        if (slot.Buffer.IsValid && slot.Capacity >= objectCount)
        {
            SetActiveObjectDataBuffer(slotIndex, slot);
            return;
        }

        ReleaseObjectDataBufferSlot(slotIndex);

        ulong byteSize = checked((ulong)objectCount * (ulong)sizeof(StaticMeshObjectData));
        var buffer = factory.CreateBuffer(
            byteSize,
            (uint)EBufferUsageFlagBits.BUFFER_USAGE_STORAGE_BUFFER_BIT,
            ESharingMode.SHARING_MODE_EXCLUSIVE,
            ERHIMemoryUsage.Upload,
            $"StaticMeshPass.ObjectData[{slotIndex}]");
        if (!buffer.IsValid)
        {
            throw new InvalidOperationException("[StaticMeshPass] Failed to create object data buffer.");
        }

        var bindlessIndex = factory.RegisterBindlessResourceBuffer(buffer);
        if (bindlessIndex == InvalidBindlessIndex)
        {
            factory.ReleaseBuffer(buffer);
            throw new InvalidOperationException("[StaticMeshPass] Failed to register object data buffer in the bindless table.");
        }

        slot = new StaticMeshObjectBufferSlot(buffer, bindlessIndex, objectCount);
        SetActiveObjectDataBuffer(slotIndex, slot);
        Logger.Log(
            $"[StaticMeshPass] Created object data buffer slot | Slot: {slotIndex}/{m_ObjectDataRingSize} | Capacity: {objectCount} | Bytes: {byteSize} | Buffer: {buffer.Index}:{buffer.Generation} | Bindless: {bindlessIndex}");
    }

    private void SetActiveObjectDataBuffer(int slotIndex, StaticMeshObjectBufferSlot slot)
    {
        if (!slot.IsValid)
        {
            throw new InvalidOperationException($"[StaticMeshPass] Object data buffer slot {slotIndex} is invalid.");
        }

        m_ObjectDataSlotIndex = slotIndex;
        m_ObjectDataBuffer = slot.Buffer;
        m_ObjectDataBufferBindlessIndex = slot.BindlessIndex;
        m_ObjectDataBufferCapacity = slot.Capacity;
    }

    private unsafe void UploadObjectData(int objectCount)
    {
        if (!m_ObjectDataBuffer.IsValid || objectCount <= 0)
        {
            return;
        }

        var mapped = m_Factory.MapBuffer(m_ObjectDataBuffer);
        if (mapped == IntPtr.Zero)
        {
            throw new InvalidOperationException("[StaticMeshPass] Failed to map object data buffer.");
        }

        try
        {
            var source = new ReadOnlySpan<StaticMeshObjectData>(m_ObjectData, 0, objectCount);
            source.CopyTo(new Span<StaticMeshObjectData>(mapped.ToPointer(), objectCount));
        }
        finally
        {
            m_Factory.UnmapBuffer(m_ObjectDataBuffer);
        }
    }

    private void RecordObjectDataBarrier(RenderCommandList commandList)
    {
        if (!m_ObjectDataBuffer.IsValid || m_ObjectDataCount <= 0)
        {
            return;
        }

        Span<RHIBufferMemoryBarrier> barriers = stackalloc RHIBufferMemoryBarrier[1];
        barriers[0] = new RHIBufferMemoryBarrier
        {
            SrcAccessMask = EAccessFlag.ACCESS_HOST_WRITE_BIT,
            DstAccessMask = EAccessFlag.ACCESS_SHADER_READ_BIT,
            SrcQueueFamilyIndex = RHIQueueFamily.Ignored,
            DstQueueFamilyIndex = RHIQueueFamily.Ignored,
            Buffer = m_ObjectDataBuffer,
            SrcStageMask = EPipelineStageFlagBits.PIPELINE_STAGE_HOST_BIT,
            DstStageMask = EPipelineStageFlagBits.PIPELINE_STAGE_VERTEX_SHADER_BIT
        };

        commandList.PipelineBarrier(
            EPipelineStageFlagBits.PIPELINE_STAGE_HOST_BIT,
            EPipelineStageFlagBits.PIPELINE_STAGE_VERTEX_SHADER_BIT,
            barriers);
    }

    private void ReleaseObjectDataBuffers()
    {
        for (int i = 0; i < m_ObjectDataBufferSlots.Length; i++)
        {
            ReleaseObjectDataBufferSlot(i);
        }

        m_ObjectDataBufferSlots = Array.Empty<StaticMeshObjectBufferSlot>();
        m_ObjectDataRingSize = 0;
        m_ObjectDataSlotIndex = 0;
        m_ObjectDataBuffer = RHIBufferHandle.Invalid;
        m_ObjectDataBufferBindlessIndex = InvalidBindlessIndex;
        m_ObjectDataBufferCapacity = 0;
        m_ObjectDataCount = 0;
    }

    private void ReleaseObjectDataBufferSlot(int slotIndex)
    {
        if ((uint)slotIndex >= (uint)m_ObjectDataBufferSlots.Length)
        {
            return;
        }

        ref var slot = ref m_ObjectDataBufferSlots[slotIndex];
        if (m_Factory.IsValid && slot.Buffer.IsValid)
        {
            if (slot.BindlessIndex != InvalidBindlessIndex)
            {
                m_Factory.UnregisterBindlessResourceBuffer(slot.BindlessIndex);
            }

            m_Factory.ReleaseBuffer(slot.Buffer);
        }

        slot = default;
    }

    private void ReleasePipelineBatchResources(ref StaticMeshPipelineBatch batch)
    {
        if (batch.PipelineState.IsValid)
        {
            batch.PipelineState.Release();
            batch.PipelineState = default;
        }

        if (m_Factory.IsValid)
        {
            if (batch.VertexProgram.IsValid)
            {
                m_Factory.ReleaseGPUProgram(batch.VertexProgram);
            }

            if (batch.FragmentProgram.IsValid)
            {
                m_Factory.ReleaseGPUProgram(batch.FragmentProgram);
            }
        }

        if (batch.VertexShaderAsset.IsValid)
        {
            m_AssetDatabase.Release(batch.VertexShaderAsset);
        }

        if (batch.FragmentShaderAsset.IsValid)
        {
            m_AssetDatabase.Release(batch.FragmentShaderAsset);
        }

        batch.VertexProgram = RHIShaderProgramHandle.Invalid;
        batch.FragmentProgram = RHIShaderProgramHandle.Invalid;
        batch.VertexShaderAsset = CookedAssetHandle.Invalid;
        batch.FragmentShaderAsset = CookedAssetHandle.Invalid;
        batch.Pipeline = RHIPipelineHandle.Invalid;
        batch.Key = default;
        batch.Shader = null;
    }

    private void RefreshFallbackDrawConstants()
    {
        m_FallbackDrawConstants = StaticMeshDrawConstants.From(
            m_FallbackMaterialConstants.ImageIndex,
            m_FallbackMaterialConstants.SamplerIndex,
            m_FallbackMaterialConstants.BaseColorFactor,
            m_ObjectDataBufferBindlessIndex,
            0);
    }

    private static StaticMeshMaterialConstants CreateMaterialConstants(RHIMaterialResource material)
    {
        var textureConstants = material.GetTexture2DConstants(GenericRenderPipelineAssetRefs.SmokeMaterial.Texture2DSlots.BaseColor);
        var baseColorFactor = material.GetVector4PropertyOrDefault(
            GenericRenderPipelineAssetRefs.SmokeMaterial.Vector4Properties.BaseColorFactor,
            Vector4.One);

        return new StaticMeshMaterialConstants(
            textureConstants.ImageIndex,
            textureConstants.SamplerIndex,
            baseColorFactor);
    }

    private StaticMeshMaterialConstants GetMaterialConstants(uint materialId)
    {
        if (materialId < (uint)m_MaterialSlots.Length)
        {
            var slot = m_MaterialSlots[(int)materialId];
            if (slot.IsValid)
            {
                return slot.Constants;
            }
        }

        return m_FallbackMaterialConstants;
    }

    private int GetMaterialPipelineBatchIndex(uint materialId)
    {
        if (materialId < (uint)m_MaterialSlots.Length)
        {
            var slot = m_MaterialSlots[(int)materialId];
            if (slot.IsValid && slot.PipelineBatchIndex >= 0)
            {
                return slot.PipelineBatchIndex;
            }
        }

        return m_FallbackPipelineBatchIndex;
    }

    private static bool IsDrawable(in MeshDrawCommand draw)
    {
        return draw.VertexBuffer.IsValid &&
               draw.IndexBuffer.IsValid &&
               draw.IndexCount != 0;
    }

    public void Dispose()
    {
        if (m_Disposed)
        {
            return;
        }

        ReleasePipelineResources();
        ReleaseDepthTargetResources();
        ReleaseObjectDataBuffers();
        m_Disposed = true;
    }

}

internal readonly record struct StaticMeshPipelineKey(
    Guid ShaderGuid,
    AssetDependencyStamp ShaderDependencyStamp,
    string ShaderVariantIdentity,
    MaterialRenderState RenderState,
    EFormat ColorFormat,
    EFormat DepthFormat);

internal struct StaticMeshPipelineBatch
{
    public StaticMeshPipelineKey Key;
    public ShaderAsset? Shader;
    public RHIPipelineState PipelineState;
    public RHIPipelineHandle Pipeline;
    public RHIShaderProgramHandle VertexProgram;
    public RHIShaderProgramHandle FragmentProgram;
    public CookedAssetHandle VertexShaderAsset;
    public CookedAssetHandle FragmentShaderAsset;
    public bool IsValid => Pipeline.IsValid;

    public StaticMeshPipelineBatch(StaticMeshPipelineKey key, ShaderAsset shader)
    {
        Key = key;
        Shader = shader;
        PipelineState = default;
        Pipeline = RHIPipelineHandle.Invalid;
        VertexProgram = RHIShaderProgramHandle.Invalid;
        FragmentProgram = RHIShaderProgramHandle.Invalid;
        VertexShaderAsset = CookedAssetHandle.Invalid;
        FragmentShaderAsset = CookedAssetHandle.Invalid;
    }
}

internal readonly struct StaticMeshDrawBatch
{
    public readonly int PipelineBatchIndex;
    public readonly int DrawIndexStart;
    public readonly int DrawCount;

    public StaticMeshDrawBatch(int pipelineBatchIndex, int drawIndexStart, int drawCount)
    {
        PipelineBatchIndex = pipelineBatchIndex;
        DrawIndexStart = drawIndexStart;
        DrawCount = drawCount;
    }
}

internal readonly struct StaticMeshBatchWorkItem
{
    public readonly int PipelineBatchIndex;
    public readonly int DrawIndexStart;
    public readonly int DrawIndexCount;

    public StaticMeshBatchWorkItem(int pipelineBatchIndex, int drawIndexStart, int drawIndexCount)
    {
        PipelineBatchIndex = pipelineBatchIndex;
        DrawIndexStart = drawIndexStart;
        DrawIndexCount = drawIndexCount;
    }
}

internal readonly struct StaticMeshObjectBufferSlot
{
    public readonly RHIBufferHandle Buffer;
    public readonly uint BindlessIndex;
    public readonly int Capacity;

    public bool IsValid => Buffer.IsValid && BindlessIndex != 0xFFFFFFFFu && Capacity > 0;

    public StaticMeshObjectBufferSlot(RHIBufferHandle buffer, uint bindlessIndex, int capacity)
    {
        Buffer = buffer;
        BindlessIndex = bindlessIndex;
        Capacity = capacity;
    }
}

internal struct StaticMeshMaterialSlot : IEquatable<StaticMeshMaterialSlot>
{
    public readonly ShaderAsset? Shader;
    public readonly AssetDependencyStamp ShaderDependencyStamp;
    public readonly MaterialRenderState RenderState;
    public readonly StaticMeshMaterialConstants Constants;
    public int PipelineBatchIndex;
    public readonly bool IsValid;

    public StaticMeshMaterialSlot(
        ShaderAsset shader,
        AssetDependencyStamp shaderDependencyStamp,
        MaterialRenderState renderState,
        StaticMeshMaterialConstants constants,
        int pipelineBatchIndex)
    {
        Shader = shader;
        ShaderDependencyStamp = shaderDependencyStamp;
        RenderState = renderState;
        Constants = constants;
        PipelineBatchIndex = pipelineBatchIndex;
        IsValid = true;
    }

    public bool Equals(StaticMeshMaterialSlot other)
    {
        return IsValid == other.IsValid &&
               ReferenceEquals(Shader, other.Shader) &&
               ShaderDependencyStamp == other.ShaderDependencyStamp &&
               RenderState == other.RenderState &&
               Constants.Equals(other.Constants) &&
               PipelineBatchIndex == other.PipelineBatchIndex;
    }

    public override bool Equals(object? obj)
    {
        return obj is StaticMeshMaterialSlot other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            Shader,
            ShaderDependencyStamp,
            RenderState,
            Constants,
            PipelineBatchIndex,
            IsValid);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct StaticMeshMaterialConstants : IEquatable<StaticMeshMaterialConstants>
{
    public static StaticMeshMaterialConstants Default => new(0, 0, Vector4.One);

    public readonly uint ImageIndex;
    public readonly uint SamplerIndex;
    public readonly Vector4 BaseColorFactor;

    public StaticMeshMaterialConstants(uint imageIndex, uint samplerIndex, Vector4 baseColorFactor)
    {
        ImageIndex = imageIndex;
        SamplerIndex = samplerIndex;
        BaseColorFactor = baseColorFactor;
    }

    public bool Equals(StaticMeshMaterialConstants other)
    {
        return ImageIndex == other.ImageIndex &&
               SamplerIndex == other.SamplerIndex &&
               BaseColorFactor.Equals(other.BaseColorFactor);
    }

    public override bool Equals(object? obj)
    {
        return obj is StaticMeshMaterialConstants other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(ImageIndex, SamplerIndex, BaseColorFactor);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct StaticMeshDrawConstants
{
    private const uint InvalidBindlessIndex = 0xFFFFFFFFu;

    public static StaticMeshDrawConstants Identity => From(0, 0, Vector4.One, InvalidBindlessIndex, 0);

    public readonly Vector4 BaseColorFactor;
    public readonly uint ImageIndex;
    public readonly uint SamplerIndex;
    public readonly uint ObjectBufferIndex;
    public readonly uint ObjectIndex;

    private StaticMeshDrawConstants(
        uint imageIndex,
        uint samplerIndex,
        Vector4 baseColorFactor,
        uint objectBufferIndex,
        uint objectIndex)
    {
        BaseColorFactor = baseColorFactor;
        ImageIndex = imageIndex;
        SamplerIndex = samplerIndex;
        ObjectBufferIndex = objectBufferIndex;
        ObjectIndex = objectIndex;
    }

    public static StaticMeshDrawConstants From(
        uint imageIndex,
        uint samplerIndex,
        Vector4 baseColorFactor,
        uint objectBufferIndex,
        uint objectIndex)
    {
        return new StaticMeshDrawConstants(
            imageIndex,
            samplerIndex,
            baseColorFactor,
            objectBufferIndex,
            objectIndex);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct StaticMeshObjectData
{
    public readonly Vector4 ModelViewProjectionColumn0;
    public readonly Vector4 ModelViewProjectionColumn1;
    public readonly Vector4 ModelViewProjectionColumn2;
    public readonly Vector4 ModelViewProjectionColumn3;
    public readonly Vector4 LocalToWorldColumn0;
    public readonly Vector4 LocalToWorldColumn1;
    public readonly Vector4 LocalToWorldColumn2;

    private StaticMeshObjectData(
        Vector4 modelViewProjectionColumn0,
        Vector4 modelViewProjectionColumn1,
        Vector4 modelViewProjectionColumn2,
        Vector4 modelViewProjectionColumn3,
        Vector4 localToWorldColumn0,
        Vector4 localToWorldColumn1,
        Vector4 localToWorldColumn2)
    {
        ModelViewProjectionColumn0 = modelViewProjectionColumn0;
        ModelViewProjectionColumn1 = modelViewProjectionColumn1;
        ModelViewProjectionColumn2 = modelViewProjectionColumn2;
        ModelViewProjectionColumn3 = modelViewProjectionColumn3;
        LocalToWorldColumn0 = localToWorldColumn0;
        LocalToWorldColumn1 = localToWorldColumn1;
        LocalToWorldColumn2 = localToWorldColumn2;
    }

    public static StaticMeshObjectData From(Matrix4x4 localToWorld, Matrix4x4 viewProjection)
    {
        var modelViewProjection = localToWorld * viewProjection;
        return new StaticMeshObjectData(
            new Vector4(modelViewProjection.M11, modelViewProjection.M21, modelViewProjection.M31, modelViewProjection.M41),
            new Vector4(modelViewProjection.M12, modelViewProjection.M22, modelViewProjection.M32, modelViewProjection.M42),
            new Vector4(modelViewProjection.M13, modelViewProjection.M23, modelViewProjection.M33, modelViewProjection.M43),
            new Vector4(modelViewProjection.M14, modelViewProjection.M24, modelViewProjection.M34, modelViewProjection.M44),
            new Vector4(localToWorld.M11, localToWorld.M21, localToWorld.M31, 0.0f),
            new Vector4(localToWorld.M12, localToWorld.M22, localToWorld.M32, 0.0f),
            new Vector4(localToWorld.M13, localToWorld.M23, localToWorld.M33, 0.0f));
    }
}
