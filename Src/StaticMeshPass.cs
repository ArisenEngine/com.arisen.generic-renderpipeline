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
    private const uint InvalidBindlessIndex = 0xFFFFFFFFu;
    private const int DefaultObjectDataRingSize = 2;

    private readonly IAssetDatabase m_AssetDatabase;
    private readonly StaticMeshPassConfiguration m_Configuration;
    private RHIFactory m_Factory;
    private Matrix4x4 m_ViewProjection = Matrix4x4.Identity;
    private Matrix4x4 m_ShadowViewProjection = Matrix4x4.Identity;
    private Vector3 m_CameraPosition = Vector3.Zero;
    private Matrix4x4 m_FallbackLocalToWorld = Matrix4x4.Identity;
    private DirectionalLight m_DirectionalLight = DirectionalLight.Default;
    private SceneEnvironment m_SceneEnvironment = SceneEnvironment.Default;
    private StaticMeshLightingConstants m_LightingConstants = StaticMeshLightingConstants.Default;
    private StaticMeshShadowConstants m_ShadowConstants = StaticMeshShadowConstants.Disabled;
    private StaticMeshEnvironmentLightingConstants m_EnvironmentLightingConstants =
        StaticMeshEnvironmentLightingConstants.Disabled;
    private StaticMeshMaterialConstants m_FallbackMaterialConstants = StaticMeshMaterialConstants.Default;
    private StaticMeshMaterialSlot[] m_MaterialSlots = Array.Empty<StaticMeshMaterialSlot>();
    private StaticMeshPipelineBatch[] m_PipelineBatches = Array.Empty<StaticMeshPipelineBatch>();
    private StaticMeshDrawBatch[] m_DrawBatches = Array.Empty<StaticMeshDrawBatch>();
    private StaticMeshBatchWorkItem[] m_WorkItems = Array.Empty<StaticMeshBatchWorkItem>();
    private MeshDrawCommand[] m_PreparedDraws = Array.Empty<MeshDrawCommand>();
    private StaticMeshObjectData[] m_ObjectData = Array.Empty<StaticMeshObjectData>();
    private PointLight[] m_PointLights = Array.Empty<PointLight>();
    private SpotLight[] m_SpotLights = Array.Empty<SpotLight>();
    private int[] m_BatchedDrawIndices = Array.Empty<int>();
    private StaticMeshDrawSortKey[] m_BatchedDrawSortKeys = Array.Empty<StaticMeshDrawSortKey>();
    private StaticMeshDrawConstants m_FallbackDrawConstants = StaticMeshDrawConstants.Identity;
    private StaticMeshObjectBufferSlot[] m_ObjectDataBufferSlots = Array.Empty<StaticMeshObjectBufferSlot>();
    private RHIBufferHandle m_ObjectDataBuffer = RHIBufferHandle.Invalid;
    private uint m_ObjectDataBufferBindlessIndex = InvalidBindlessIndex;
    private int m_ObjectDataBufferCapacity;
    private int m_ObjectDataCount;
    private int m_SceneDataCount;
    private int m_PointLightDataStart;
    private int m_PointLightCount;
    private int m_SpotLightDataStart;
    private int m_SpotLightCount;
    private int m_ObjectDataRingSize;
    private int m_ObjectDataSlotIndex;
    private RHIBufferHandle m_VertexBuffer = RHIBufferHandle.Invalid;
    private RHIBufferHandle m_IndexBuffer = RHIBufferHandle.Invalid;
    private EIndexType m_IndexType = EIndexType.INDEX_TYPE_UINT32;
    private uint m_FirstIndex;
    private uint m_IndexCount;
    private int m_VertexOffset;
    private RHIImageViewHandle m_ColorTargetImageView = RHIImageViewHandle.Invalid;
    private EFormat m_ColorTargetFormat = EFormat.FORMAT_UNDEFINED;
    private bool m_EncodeOutputToSrgb;
    private EFormat m_ColorFormat = EFormat.FORMAT_UNDEFINED;
    private EFormat m_DepthFormat = EFormat.FORMAT_UNDEFINED;
    private RHIImageViewHandle m_DepthImageView = RHIImageViewHandle.Invalid;
    private EFormat m_DepthTargetFormat = EFormat.FORMAT_UNDEFINED;
    private uint m_DepthWidth;
    private uint m_DepthHeight;
    private int m_PipelineBatchCount;
    private int m_DrawBatchCount;
    private int m_WorkItemCount;
    private int m_PreparedDrawCount;
    private int m_OpaqueDrawCount;
    private int m_AlphaTestDrawCount;
    private int m_TransparentDrawCount;
    private int m_SkippedAlphaDrawCount;
    private int m_FallbackPipelineBatchIndex = -1;
    private uint m_MaterialSlotVersion;
    private uint m_PreparedMaterialSlotVersion;
    private bool m_Disposed;

    public int OpaqueDrawCount => m_OpaqueDrawCount;
    public int AlphaTestDrawCount => m_AlphaTestDrawCount;
    public int TransparentDrawCount => m_TransparentDrawCount;
    public int SkippedAlphaDrawCount => m_SkippedAlphaDrawCount;

    public StaticMeshPass(IAssetDatabase assetDatabase, string name = "StaticMeshPass")
        : this(assetDatabase, name, StaticMeshPassConfiguration.Opaque)
    {
    }

    internal StaticMeshPass(
        IAssetDatabase assetDatabase,
        string name,
        StaticMeshPassConfiguration configuration) : base(name)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        m_Configuration = configuration;
    }

    protected override int GetWorkItemCount(RenderContext context)
    {
        if (m_WorkItemCount > 0)
        {
            return m_WorkItemCount;
        }

        return CanRecordFallbackMesh() || RequiresDepthClearWorkItem() ? 1 : 0;
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
        if (m_WorkItemCount > 0)
        {
            RecordPreparedDrawBatches(context, commandList);
            return;
        }

        RecordFallbackOrDepthClear(context, commandList);
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

        RecordFallbackOrDepthClear(context, commandList);
    }

    private void RecordFallbackOrDepthClear(RenderContext context, RenderCommandList commandList)
    {
        if (CanRecordFallbackMesh())
        {
            RecordFallbackMesh(context, commandList);
            return;
        }

        if (!RequiresDepthClearWorkItem())
        {
            return;
        }

        BeginStaticMeshRendering(
            context,
            commandList,
            GetColorTargetImageView(context),
            clearDepth: true);
        commandList.EndRendering();
    }

    private bool CanRecordFallbackMesh()
    {
        if (m_PreparedDrawCount > 0 ||
            m_FallbackPipelineBatchIndex < 0 ||
            m_FallbackPipelineBatchIndex >= m_PipelineBatchCount)
        {
            return false;
        }

        return m_PipelineBatches[m_FallbackPipelineBatchIndex].IsValid;
    }

    private bool RequiresDepthClearWorkItem()
    {
        return m_Configuration.ClearDepthOnFirstWorkItem && m_DepthImageView.IsValid;
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

        var colorImageView = GetColorTargetImageView(context);

        if (context.FrameIndex % 60 == 0)
        {
            Logger.Log(
                $"[StaticMeshPass] RecordFallback | Surface: 0x{context.SurfaceId:X} | Size: {context.Width}x{context.Height} | Pipeline: {pipelineBatch.Pipeline.Index}:{pipelineBatch.Pipeline.Generation}");
        }

        BeginStaticMeshRendering(
            context,
            commandList,
            colorImageView,
            clearDepth: m_Configuration.ClearDepthOnFirstWorkItem);
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
        var colorImageView = GetColorTargetImageView(context);

        BeginStaticMeshRendering(
            context,
            commandList,
            colorImageView,
            clearDepth: firstWorkItem && m_Configuration.ClearDepthOnFirstWorkItem);
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
                materialConstants.BaseColorFactor,
                m_LightingConstants,
                m_CameraPosition,
                materialConstants.MetallicFactor,
                materialConstants.RoughnessFactor,
                materialConstants.BaseColorImageIndex,
                materialConstants.BaseColorSamplerIndex,
                materialConstants.NormalImageIndex,
                materialConstants.NormalSamplerIndex,
                m_ObjectDataBufferBindlessIndex,
                checked((uint)drawIndex),
                checked((uint)m_PointLightDataStart),
                PackLocalLightCounts(m_PointLightCount, m_SpotLightCount),
                m_EncodeOutputToSrgb);

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
            m_Configuration.DepthWriteEnabled
                ? EImageLayout.IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL
                : EImageLayout.IMAGE_LAYOUT_DEPTH_STENCIL_READ_ONLY_OPTIMAL,
            clearDepth ? EAttachmentLoadOp.ATTACHMENT_LOAD_OP_CLEAR : EAttachmentLoadOp.ATTACHMENT_LOAD_OP_LOAD,
            EAttachmentStoreOp.ATTACHMENT_STORE_OP_STORE,
            1.0f,
            0,
            0, 0, context.Width, context.Height);
    }

    public void Prepare(RenderContext context)
    {
        var factory = context.Device.GetFactory();
        var colorFormat = m_ColorTargetFormat != EFormat.FORMAT_UNDEFINED
            ? m_ColorTargetFormat
            : factory.GetImageViewFormat(context.SwapChain.GetImageView(context.FrameIndex));
        ValidateDepthTarget(context.Width, context.Height);
        var depthFormat = m_DepthTargetFormat;
        var encodeOutputToSrgb = RenderOutputEncoding.RequiresExplicitSrgbEncoding(colorFormat);
        if (m_EncodeOutputToSrgb != encodeOutputToSrgb)
        {
            m_EncodeOutputToSrgb = encodeOutputToSrgb;
            RefreshFallbackDrawConstants();
        }

        EnsurePipelineBatches(context, colorFormat, depthFormat);
        BuildDrawBatches(context);
        PrepareObjectDataBuffer(context, factory);
        PlotBatchDiagnostics();
    }

    public void SetColorTarget(RHIImageViewHandle imageView, EFormat format)
    {
        m_ColorTargetImageView = imageView;
        m_ColorTargetFormat = format;
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

    internal void SetDepthTarget(
        RHIImageViewHandle imageView,
        EFormat format,
        uint width,
        uint height)
    {
        if (!imageView.IsValid ||
            format == EFormat.FORMAT_UNDEFINED ||
            width == 0 ||
            height == 0)
        {
            throw new ArgumentException($"[{Name}] Graph-owned depth target is invalid.", nameof(imageView));
        }

        m_DepthImageView = imageView;
        m_DepthTargetFormat = format;
        m_DepthWidth = width;
        m_DepthHeight = height;
    }

    private void ValidateDepthTarget(uint width, uint height)
    {
        if (!m_DepthImageView.IsValid ||
            m_DepthTargetFormat == EFormat.FORMAT_UNDEFINED ||
            m_DepthWidth != width ||
            m_DepthHeight != height)
        {
            throw new InvalidOperationException(
                $"[{Name}] Graph-owned depth target does not match {width}x{height}.");
        }
    }

    private void EnsurePipelineBatches(RenderContext context, EFormat colorFormat, EFormat depthFormat)
    {
        if (m_ColorFormat == colorFormat &&
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
            if (!slot.IsValid ||
                slot.Shader == null ||
                !m_Configuration.Accepts(slot.RenderQueue.Class))
            {
                continue;
            }

            var key = new StaticMeshPipelineKey(
                slot.Shader.Guid,
                slot.ShaderDependencyStamp,
                slot.Shader.GetVariantIdentity(),
                slot.RenderState,
                m_Configuration.DepthWriteEnabled,
                colorFormat,
                depthFormat);
            int batchIndex = FindPipelineBatch(key);
            if (batchIndex < 0)
            {
                batchIndex = CreatePipelineBatch(pipelineCache, key, slot.Shader);
            }

            slot.PipelineBatchIndex = batchIndex;
            m_MaterialSlots[i] = slot;

            if (i == 0 && m_Configuration.EnableFallbackMesh)
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
                key.DepthFormat != EFormat.FORMAT_UNDEFINED && key.DepthWriteEnabled,
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
                $"[{Name}] Created pipeline batch | Batch: {batchIndex} | Shader: {key.ShaderGuid} | Format: {key.ColorFormat} | Cull: {key.RenderState.CullMode} | Blend: {key.RenderState.BlendEnabled} | DepthWrite: {key.DepthWriteEnabled} | Pipeline: {batch.Pipeline.Index}:{batch.Pipeline.Generation}");
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
        m_OpaqueDrawCount = 0;
        m_AlphaTestDrawCount = 0;
        m_TransparentDrawCount = 0;
        m_SkippedAlphaDrawCount = 0;

        if (m_PreparedDrawCount <= 0 || m_PipelineBatchCount <= 0)
        {
            return;
        }

        EnsureBatchScratchCapacity(m_PreparedDrawCount);
        EnsureDrawBatchCapacity(m_PreparedDrawCount);

        int preparedDrawCount = m_PreparedDrawCount;
        int sortedDrawCount = 0;
        for (int i = 0; i < preparedDrawCount; i++)
        {
            ref readonly var draw = ref m_PreparedDraws[i];
            var renderQueue = GetMaterialRenderQueue(draw.MaterialID);
            if (!m_Configuration.Accepts(renderQueue.Class))
            {
                continue;
            }

            IncrementRenderQueueCount(renderQueue.Class);
            if (!IsDrawable(draw))
            {
                IncrementSkippedAlphaDrawCount(renderQueue.Class);
                continue;
            }

            int batchIndex = GetMaterialPipelineBatchIndex(draw.MaterialID);
            if (batchIndex >= 0)
            {
                m_BatchedDrawSortKeys[sortedDrawCount] = StaticMeshDrawSortKey.From(
                    renderQueue,
                    batchIndex,
                    draw,
                    checked((uint)i));
                m_BatchedDrawIndices[sortedDrawCount] = i;
                sortedDrawCount++;
            }
            else
            {
                IncrementSkippedAlphaDrawCount(renderQueue.Class);
            }
        }

        if (sortedDrawCount <= 0)
        {
            return;
        }

        if (!m_Configuration.PreservePreparedDrawOrder)
        {
            Array.Sort(m_BatchedDrawSortKeys, m_BatchedDrawIndices, 0, sortedDrawCount);
        }

        int batchStart = 0;
        int currentPipelineBatchIndex = m_BatchedDrawSortKeys[0].PipelineBatchIndex;
        for (int sortedIndex = 1; sortedIndex < sortedDrawCount; sortedIndex++)
        {
            int pipelineBatchIndex = m_BatchedDrawSortKeys[sortedIndex].PipelineBatchIndex;
            if (pipelineBatchIndex == currentPipelineBatchIndex)
            {
                continue;
            }

            m_DrawBatches[m_DrawBatchCount++] = new StaticMeshDrawBatch(
                currentPipelineBatchIndex,
                batchStart,
                sortedIndex - batchStart);
            batchStart = sortedIndex;
            currentPipelineBatchIndex = pipelineBatchIndex;
        }

        m_DrawBatches[m_DrawBatchCount++] = new StaticMeshDrawBatch(
            currentPipelineBatchIndex,
            batchStart,
            sortedDrawCount - batchStart);

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

    private void EnsureBatchScratchCapacity(int drawCount)
    {
        if (m_BatchedDrawIndices.Length < drawCount)
        {
            Array.Resize(ref m_BatchedDrawIndices, drawCount);
        }

        if (m_BatchedDrawSortKeys.Length < drawCount)
        {
            Array.Resize(ref m_BatchedDrawSortKeys, drawCount);
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

        int opaqueDrawCount = m_OpaqueDrawCount;
        if (m_PreparedDrawCount == 0 && m_FallbackPipelineBatchIndex >= 0)
        {
            opaqueDrawCount = 1;
        }

        if (m_Configuration.QueuePolicy == StaticMeshPassQueuePolicy.Transparent)
        {
            Profiler.PlotValue("TransparentStaticMeshPass.DrawCount", effectiveDrawCount);
            Profiler.PlotValue("TransparentStaticMeshPass.MaterialBatchCount", activeBatchCount);
            Profiler.PlotValue("TransparentStaticMeshPass.TransparentDrawCount", m_TransparentDrawCount);
            Profiler.PlotValue("TransparentStaticMeshPass.SkippedAlphaDrawCount", m_SkippedAlphaDrawCount);
            Profiler.PlotValue("TransparentStaticMeshPass.PipelineBatchCount", m_PipelineBatchCount);
            Profiler.PlotValue("TransparentStaticMeshPass.WorkItemCount", m_WorkItemCount);
            Profiler.PlotValue("TransparentStaticMeshPass.ObjectDataCount", m_ObjectDataCount);
            Profiler.PlotValue("TransparentStaticMeshPass.ObjectDataCapacity", m_ObjectDataBufferCapacity);
            Profiler.PlotValue("TransparentStaticMeshPass.ObjectDataRingSize", m_ObjectDataRingSize);
            return;
        }

        Profiler.PlotValue("StaticMeshPass.DrawCount", effectiveDrawCount);
        Profiler.PlotValue("StaticMeshPass.MaterialBatchCount", activeBatchCount);
        Profiler.PlotValue("StaticMeshPass.OpaqueDrawCount", opaqueDrawCount);
        Profiler.PlotValue("StaticMeshPass.AlphaTestDrawCount", m_AlphaTestDrawCount);
        Profiler.PlotValue("StaticMeshPass.TransparentDrawCount", m_TransparentDrawCount);
        Profiler.PlotValue("StaticMeshPass.SkippedAlphaDrawCount", m_SkippedAlphaDrawCount);
        Profiler.PlotValue("StaticMeshPass.PipelineBatchCount", m_PipelineBatchCount);
        Profiler.PlotValue("StaticMeshPass.WorkItemCount", m_WorkItemCount);
        Profiler.PlotValue("StaticMeshPass.ObjectDataCount", m_ObjectDataCount);
        Profiler.PlotValue("StaticMeshPass.PointLightCount", m_PointLightCount);
        Profiler.PlotValue("StaticMeshPass.SpotLightCount", m_SpotLightCount);
        Profiler.PlotValue("StaticMeshPass.SceneDataCount", m_SceneDataCount);
        Profiler.PlotValue("StaticMeshPass.ObjectDataCapacity", m_ObjectDataBufferCapacity);
        Profiler.PlotValue("StaticMeshPass.ObjectDataRingSize", m_ObjectDataRingSize);
    }

    private RHIImageViewHandle GetColorTargetImageView(RenderContext context)
    {
        return m_ColorTargetImageView.IsValid
            ? m_ColorTargetImageView
            : context.SwapChain.GetImageView(context.FrameIndex);
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
            materialConstants.BaseColorFactor,
            m_LightingConstants,
            m_CameraPosition,
            materialConstants.MetallicFactor,
            materialConstants.RoughnessFactor,
            materialConstants.BaseColorImageIndex,
            materialConstants.BaseColorSamplerIndex,
            materialConstants.NormalImageIndex,
            materialConstants.NormalSamplerIndex,
            m_ObjectDataBufferBindlessIndex,
            0,
            checked((uint)m_PointLightDataStart),
            PackLocalLightCounts(m_PointLightCount, m_SpotLightCount),
            m_EncodeOutputToSrgb);
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
        var renderQueue = RenderQueuePolicy.Resolve(material.RenderState, material.Shader.VariantKeywords);
        var slot = new StaticMeshMaterialSlot(
            material.Shader,
            material.ShaderDependencyStamp,
            material.RenderState,
            renderQueue,
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

    public void SetCameraPosition(Vector3 cameraPosition)
    {
        m_CameraPosition = cameraPosition;
        RefreshFallbackDrawConstants();
    }

    public void SetDirectionalLight(DirectionalLight light)
    {
        m_DirectionalLight = light.IsValid ? light : DirectionalLight.Default;
        m_LightingConstants = StaticMeshLightingConstants.From(
            m_DirectionalLight,
            m_SceneEnvironment);
        RefreshFallbackDrawConstants();
    }

    public void SetPointLights(ReadOnlySpan<PointLight> pointLights)
    {
        int pointLightCount = Math.Min(pointLights.Length, PointLightSnapshotExtractor.MaxPointLightsPerFrame);
        if (m_PointLights.Length < pointLightCount)
        {
            Array.Resize(ref m_PointLights, pointLightCount);
        }

        pointLights.Slice(0, pointLightCount).CopyTo(m_PointLights);
        m_PointLightCount = pointLightCount;
        RefreshFallbackDrawConstants();
    }

    public void SetSpotLights(ReadOnlySpan<SpotLight> spotLights)
    {
        int spotLightCount = Math.Min(spotLights.Length, SpotLightSnapshotExtractor.MaxSpotLightsPerFrame);
        if (m_SpotLights.Length < spotLightCount)
        {
            Array.Resize(ref m_SpotLights, spotLightCount);
        }

        spotLights.Slice(0, spotLightCount).CopyTo(m_SpotLights);
        m_SpotLightCount = spotLightCount;
        RefreshFallbackDrawConstants();
    }

    public void SetSceneEnvironment(SceneEnvironment environment)
    {
        m_SceneEnvironment = environment.IsValid ? environment : SceneEnvironment.Default;
        m_LightingConstants = StaticMeshLightingConstants.From(
            m_DirectionalLight,
            m_SceneEnvironment);
        RefreshFallbackDrawConstants();
    }

    public void SetEnvironmentLighting(RHIEnvironmentLightingResource? environmentLighting)
    {
        m_EnvironmentLightingConstants =
            StaticMeshEnvironmentLightingConstants.From(environmentLighting);
    }

    public void SetDirectionalShadow(
        Matrix4x4 shadowViewProjection,
        uint shadowImageIndex,
        uint shadowSamplerIndex,
        float texelSize,
        float depthBias,
        float slopeBias,
        float strength,
        int pcfRadius,
        bool enabled)
    {
        m_ShadowViewProjection = shadowViewProjection;
        m_ShadowConstants = StaticMeshShadowConstants.From(
            shadowImageIndex,
            shadowSamplerIndex,
            texelSize,
            depthBias,
            slopeBias,
            strength,
            pcfRadius,
            enabled);
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
        m_DepthFormat = EFormat.FORMAT_UNDEFINED;
    }

    private void PrepareObjectDataBuffer(RenderContext context, RHIFactory factory)
    {
        int objectCount = m_PreparedDrawCount > 0
            ? m_PreparedDrawCount
            : m_FallbackPipelineBatchIndex >= 0 ? 1 : 0;
        int pointLightCount = objectCount > 0 ? m_PointLightCount : 0;
        int spotLightCount = objectCount > 0 ? m_SpotLightCount : 0;
        int sceneDataCount = checked(objectCount + pointLightCount + spotLightCount);

        m_ObjectDataCount = objectCount;
        m_PointLightDataStart = objectCount;
        m_SpotLightDataStart = objectCount + pointLightCount;
        m_SceneDataCount = sceneDataCount;
        if (sceneDataCount <= 0)
        {
            m_ObjectDataBuffer = RHIBufferHandle.Invalid;
            m_ObjectDataBufferBindlessIndex = InvalidBindlessIndex;
            m_ObjectDataBufferCapacity = 0;
            m_PointLightDataStart = 0;
            m_SpotLightDataStart = 0;
            return;
        }

        EnsureObjectDataRing(factory, GetObjectDataRingSize(context));
        m_ObjectDataSlotIndex = checked((int)(context.FrameIndex % (uint)m_ObjectDataRingSize));

        EnsureObjectDataCapacity(sceneDataCount);
        if (m_PreparedDrawCount > 0)
        {
            int preparedDrawCount = m_PreparedDrawCount;
            for (int i = 0; i < preparedDrawCount; i++)
            {
                var materialConstants = GetMaterialConstants(m_PreparedDraws[i].MaterialID);
                m_ObjectData[i] = StaticMeshObjectData.From(
                    m_PreparedDraws[i].LocalToWorld,
                    m_ViewProjection,
                    m_ShadowViewProjection,
                    m_ShadowConstants,
                    materialConstants.EmissiveFactor,
                    materialConstants.EmissiveTextureIndices,
                    materialConstants.MetallicRoughnessTextureIndices,
                    materialConstants.OcclusionTextureIndices,
                    materialConstants.PbrMaterialParameters,
                    m_EnvironmentLightingConstants);
            }
        }
        else
        {
            m_ObjectData[0] = StaticMeshObjectData.From(
                m_FallbackLocalToWorld,
                m_ViewProjection,
                m_ShadowViewProjection,
                m_ShadowConstants,
                m_FallbackMaterialConstants.EmissiveFactor,
                m_FallbackMaterialConstants.EmissiveTextureIndices,
                m_FallbackMaterialConstants.MetallicRoughnessTextureIndices,
                m_FallbackMaterialConstants.OcclusionTextureIndices,
                m_FallbackMaterialConstants.PbrMaterialParameters,
                m_EnvironmentLightingConstants);
        }

        for (int i = 0; i < pointLightCount; i++)
        {
            m_ObjectData[objectCount + i] = StaticMeshObjectData.From(m_PointLights[i]);
        }

        for (int i = 0; i < spotLightCount; i++)
        {
            m_ObjectData[m_SpotLightDataStart + i] = StaticMeshObjectData.From(m_SpotLights[i]);
        }

        EnsureObjectDataBuffer(factory, m_ObjectDataSlotIndex, sceneDataCount);
        UploadObjectData(sceneDataCount);

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
        if (!m_ObjectDataBuffer.IsValid || m_SceneDataCount <= 0)
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
            DstStageMask = EPipelineStageFlagBits.PIPELINE_STAGE_VERTEX_SHADER_BIT |
                           EPipelineStageFlagBits.PIPELINE_STAGE_FRAGMENT_SHADER_BIT
        };

        commandList.PipelineBarrier(
            EPipelineStageFlagBits.PIPELINE_STAGE_HOST_BIT,
            EPipelineStageFlagBits.PIPELINE_STAGE_VERTEX_SHADER_BIT |
            EPipelineStageFlagBits.PIPELINE_STAGE_FRAGMENT_SHADER_BIT,
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
        m_SceneDataCount = 0;
        m_PointLightDataStart = 0;
        m_SpotLightDataStart = 0;
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
            m_FallbackMaterialConstants.BaseColorFactor,
            m_LightingConstants,
            m_CameraPosition,
            m_FallbackMaterialConstants.MetallicFactor,
            m_FallbackMaterialConstants.RoughnessFactor,
            m_FallbackMaterialConstants.BaseColorImageIndex,
            m_FallbackMaterialConstants.BaseColorSamplerIndex,
            m_FallbackMaterialConstants.NormalImageIndex,
            m_FallbackMaterialConstants.NormalSamplerIndex,
            m_ObjectDataBufferBindlessIndex,
            0,
            checked((uint)m_PointLightDataStart),
            PackLocalLightCounts(m_PointLightCount, m_SpotLightCount),
            m_EncodeOutputToSrgb);
    }

    private static uint PackLocalLightCounts(int pointLightCount, int spotLightCount)
    {
        return ((uint)pointLightCount & 0xFFFFu) |
               (((uint)spotLightCount & 0xFFFFu) << 16);
    }

    private static StaticMeshMaterialConstants CreateMaterialConstants(RHIMaterialResource material)
    {
        var baseColorTexture = material.GetTexture2DConstants(MaterialTextureSlots.BaseColor);
        var normalTexture = material.TryGetTexture2DConstants(MaterialTextureSlots.Normal, out var normalConstants)
            ? normalConstants
            : baseColorTexture;
        var hasEmissiveTexture = material.TryGetTexture2DConstants(MaterialTextureSlots.Emissive, out var emissiveTexture);
        var hasMetallicRoughnessTexture = material.TryGetTexture2DConstants(
            MaterialTextureSlots.MetallicRoughness,
            out var metallicRoughnessTexture);
        var hasOcclusionTexture = material.TryGetTexture2DConstants(
            MaterialTextureSlots.Occlusion,
            out var occlusionTexture);
        var baseColorFactor = material.GetVector4PropertyOrDefault(
            MaterialPropertySlots.BaseColorFactor,
            Vector4.One);
        var metallicFactor = material.GetScalarPropertyOrDefault(MaterialPropertySlots.MetallicFactor, 0.0f);
        var roughnessFactor = material.GetScalarPropertyOrDefault(MaterialPropertySlots.RoughnessFactor, 1.0f);
        var occlusionStrength = material.GetScalarPropertyOrDefault(
            MaterialPropertySlots.OcclusionStrength,
            MaterialPbrDefaults.OcclusionStrength);
        var alphaCutoff = material.GetScalarPropertyOrDefault(
            MaterialPropertySlots.AlphaCutoff,
            MaterialPbrDefaults.AlphaCutoff);
        var emissiveFactor = material.GetVector4PropertyOrDefault(
            MaterialPropertySlots.EmissiveFactor,
            Vector4.Zero);

        return new StaticMeshMaterialConstants(
            baseColorFactor,
            emissiveFactor,
            metallicFactor,
            roughnessFactor,
            occlusionStrength,
            alphaCutoff,
            baseColorTexture.ImageIndex,
            baseColorTexture.SamplerIndex,
            normalTexture.ImageIndex,
            normalTexture.SamplerIndex,
            hasEmissiveTexture ? emissiveTexture.ImageIndex : 0,
            hasEmissiveTexture ? emissiveTexture.SamplerIndex : 0,
            hasEmissiveTexture ? 1u : 0u,
            hasMetallicRoughnessTexture ? metallicRoughnessTexture.ImageIndex : 0,
            hasMetallicRoughnessTexture ? metallicRoughnessTexture.SamplerIndex : 0,
            hasMetallicRoughnessTexture ? 1u : 0u,
            hasOcclusionTexture ? occlusionTexture.ImageIndex : 0,
            hasOcclusionTexture ? occlusionTexture.SamplerIndex : 0,
            hasOcclusionTexture ? 1u : 0u);
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

        return m_Configuration.EnableFallbackMesh
            ? m_FallbackPipelineBatchIndex
            : -1;
    }

    private RenderQueueInfo GetMaterialRenderQueue(uint materialId)
    {
        if (materialId < (uint)m_MaterialSlots.Length)
        {
            var slot = m_MaterialSlots[(int)materialId];
            if (slot.IsValid)
            {
                return slot.RenderQueue;
            }
        }

        return RenderQueueInfo.Opaque;
    }

    private void IncrementRenderQueueCount(RenderQueueClass queueClass)
    {
        switch (queueClass)
        {
            case RenderQueueClass.Opaque:
                m_OpaqueDrawCount++;
                break;
            case RenderQueueClass.AlphaTest:
                m_AlphaTestDrawCount++;
                break;
            case RenderQueueClass.Transparent:
                m_TransparentDrawCount++;
                break;
        }
    }

    private void IncrementSkippedAlphaDrawCount(RenderQueueClass queueClass)
    {
        if (queueClass is RenderQueueClass.AlphaTest or RenderQueueClass.Transparent)
        {
            m_SkippedAlphaDrawCount++;
        }
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
        ReleaseObjectDataBuffers();
        m_Disposed = true;
    }

}

internal enum StaticMeshPassQueuePolicy : byte
{
    OpaqueAndAlphaTest,
    Transparent
}

internal readonly record struct StaticMeshPassConfiguration(
    StaticMeshPassQueuePolicy QueuePolicy,
    bool DepthWriteEnabled,
    bool ClearDepthOnFirstWorkItem,
    bool PreservePreparedDrawOrder,
    bool EnableFallbackMesh)
{
    public static StaticMeshPassConfiguration Opaque { get; } = new(
        StaticMeshPassQueuePolicy.OpaqueAndAlphaTest,
        DepthWriteEnabled: true,
        ClearDepthOnFirstWorkItem: true,
        PreservePreparedDrawOrder: false,
        EnableFallbackMesh: true);

    public static StaticMeshPassConfiguration Transparent { get; } = new(
        StaticMeshPassQueuePolicy.Transparent,
        DepthWriteEnabled: false,
        ClearDepthOnFirstWorkItem: false,
        PreservePreparedDrawOrder: true,
        EnableFallbackMesh: false);

    public bool Accepts(RenderQueueClass queueClass)
    {
        return QueuePolicy == StaticMeshPassQueuePolicy.Transparent
            ? queueClass == RenderQueueClass.Transparent
            : queueClass is RenderQueueClass.Opaque or RenderQueueClass.AlphaTest;
    }
}

internal readonly record struct StaticMeshPipelineKey(
    Guid ShaderGuid,
    AssetDependencyStamp ShaderDependencyStamp,
    string ShaderVariantIdentity,
    MaterialRenderState RenderState,
    bool DepthWriteEnabled,
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

internal readonly struct StaticMeshDrawSortKey : IComparable<StaticMeshDrawSortKey>
{
    public readonly ushort RenderQueue;
    public readonly int PipelineBatchIndex;
    public readonly uint MaterialID;
    public readonly uint VertexBufferIndex;
    public readonly uint VertexBufferGeneration;
    public readonly uint IndexBufferIndex;
    public readonly uint IndexBufferGeneration;
    public readonly uint FirstIndex;
    public readonly int VertexOffset;
    public readonly uint SourceDrawIndex;

    private StaticMeshDrawSortKey(
        ushort renderQueue,
        int pipelineBatchIndex,
        uint materialId,
        uint vertexBufferIndex,
        uint vertexBufferGeneration,
        uint indexBufferIndex,
        uint indexBufferGeneration,
        uint firstIndex,
        int vertexOffset,
        uint sourceDrawIndex)
    {
        RenderQueue = renderQueue;
        PipelineBatchIndex = pipelineBatchIndex;
        MaterialID = materialId;
        VertexBufferIndex = vertexBufferIndex;
        VertexBufferGeneration = vertexBufferGeneration;
        IndexBufferIndex = indexBufferIndex;
        IndexBufferGeneration = indexBufferGeneration;
        FirstIndex = firstIndex;
        VertexOffset = vertexOffset;
        SourceDrawIndex = sourceDrawIndex;
    }

    public static StaticMeshDrawSortKey From(
        RenderQueueInfo renderQueue,
        int pipelineBatchIndex,
        in MeshDrawCommand draw,
        uint sourceDrawIndex)
    {
        return new StaticMeshDrawSortKey(
            renderQueue.Value,
            pipelineBatchIndex,
            draw.MaterialID,
            draw.VertexBuffer.Index,
            draw.VertexBuffer.Generation,
            draw.IndexBuffer.Index,
            draw.IndexBuffer.Generation,
            draw.FirstIndex,
            draw.VertexOffset,
            sourceDrawIndex);
    }

    public int CompareTo(StaticMeshDrawSortKey other)
    {
        int result = RenderQueue.CompareTo(other.RenderQueue);
        if (result != 0) return result;

        result = PipelineBatchIndex.CompareTo(other.PipelineBatchIndex);
        if (result != 0) return result;

        result = MaterialID.CompareTo(other.MaterialID);
        if (result != 0) return result;

        result = VertexBufferIndex.CompareTo(other.VertexBufferIndex);
        if (result != 0) return result;

        result = VertexBufferGeneration.CompareTo(other.VertexBufferGeneration);
        if (result != 0) return result;

        result = IndexBufferIndex.CompareTo(other.IndexBufferIndex);
        if (result != 0) return result;

        result = IndexBufferGeneration.CompareTo(other.IndexBufferGeneration);
        if (result != 0) return result;

        result = FirstIndex.CompareTo(other.FirstIndex);
        if (result != 0) return result;

        result = VertexOffset.CompareTo(other.VertexOffset);
        if (result != 0) return result;

        return SourceDrawIndex.CompareTo(other.SourceDrawIndex);
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
    public readonly RenderQueueInfo RenderQueue;
    public readonly StaticMeshMaterialConstants Constants;
    public int PipelineBatchIndex;
    public readonly bool IsValid;

    public StaticMeshMaterialSlot(
        ShaderAsset shader,
        AssetDependencyStamp shaderDependencyStamp,
        MaterialRenderState renderState,
        RenderQueueInfo renderQueue,
        StaticMeshMaterialConstants constants,
        int pipelineBatchIndex)
    {
        Shader = shader;
        ShaderDependencyStamp = shaderDependencyStamp;
        RenderState = renderState;
        RenderQueue = renderQueue;
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
               RenderQueue == other.RenderQueue &&
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
            RenderQueue,
            Constants,
            PipelineBatchIndex,
            IsValid);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct StaticMeshMaterialConstants : IEquatable<StaticMeshMaterialConstants>
{
    public static StaticMeshMaterialConstants Default => new(
        Vector4.One,
        Vector4.Zero,
        0.0f,
        1.0f,
        MaterialPbrDefaults.OcclusionStrength,
        MaterialPbrDefaults.AlphaCutoff,
        0, 0,
        0, 0,
        0, 0, 0,
        0, 0, 0,
        0, 0, 0);

    public readonly Vector4 BaseColorFactor;
    public readonly Vector4 EmissiveFactor;
    public readonly float MetallicFactor;
    public readonly float RoughnessFactor;
    public readonly float OcclusionStrength;
    public readonly float AlphaCutoff;
    public readonly uint BaseColorImageIndex;
    public readonly uint BaseColorSamplerIndex;
    public readonly uint NormalImageIndex;
    public readonly uint NormalSamplerIndex;
    public readonly uint EmissiveImageIndex;
    public readonly uint EmissiveSamplerIndex;
    public readonly uint HasEmissiveTexture;
    public readonly uint MetallicRoughnessImageIndex;
    public readonly uint MetallicRoughnessSamplerIndex;
    public readonly uint HasMetallicRoughnessTexture;
    public readonly uint OcclusionImageIndex;
    public readonly uint OcclusionSamplerIndex;
    public readonly uint HasOcclusionTexture;
    public Vector4 EmissiveTextureIndices => new(EmissiveImageIndex, EmissiveSamplerIndex, HasEmissiveTexture, 0.0f);
    public Vector4 MetallicRoughnessTextureIndices => new(
        MetallicRoughnessImageIndex,
        MetallicRoughnessSamplerIndex,
        HasMetallicRoughnessTexture,
        0.0f);
    public Vector4 OcclusionTextureIndices => new(
        OcclusionImageIndex,
        OcclusionSamplerIndex,
        HasOcclusionTexture,
        0.0f);
    public Vector4 PbrMaterialParameters => new(OcclusionStrength, AlphaCutoff, 0.0f, 0.0f);

    public StaticMeshMaterialConstants(
        Vector4 baseColorFactor,
        Vector4 emissiveFactor,
        float metallicFactor,
        float roughnessFactor,
        float occlusionStrength,
        float alphaCutoff,
        uint baseColorImageIndex,
        uint baseColorSamplerIndex,
        uint normalImageIndex,
        uint normalSamplerIndex,
        uint emissiveImageIndex,
        uint emissiveSamplerIndex,
        uint hasEmissiveTexture,
        uint metallicRoughnessImageIndex,
        uint metallicRoughnessSamplerIndex,
        uint hasMetallicRoughnessTexture,
        uint occlusionImageIndex,
        uint occlusionSamplerIndex,
        uint hasOcclusionTexture)
    {
        BaseColorFactor = baseColorFactor;
        EmissiveFactor = emissiveFactor;
        MetallicFactor = metallicFactor;
        RoughnessFactor = roughnessFactor;
        OcclusionStrength = occlusionStrength;
        AlphaCutoff = alphaCutoff;
        BaseColorImageIndex = baseColorImageIndex;
        BaseColorSamplerIndex = baseColorSamplerIndex;
        NormalImageIndex = normalImageIndex;
        NormalSamplerIndex = normalSamplerIndex;
        EmissiveImageIndex = emissiveImageIndex;
        EmissiveSamplerIndex = emissiveSamplerIndex;
        HasEmissiveTexture = hasEmissiveTexture;
        MetallicRoughnessImageIndex = metallicRoughnessImageIndex;
        MetallicRoughnessSamplerIndex = metallicRoughnessSamplerIndex;
        HasMetallicRoughnessTexture = hasMetallicRoughnessTexture;
        OcclusionImageIndex = occlusionImageIndex;
        OcclusionSamplerIndex = occlusionSamplerIndex;
        HasOcclusionTexture = hasOcclusionTexture;
    }

    public bool Equals(StaticMeshMaterialConstants other)
    {
        return BaseColorFactor.Equals(other.BaseColorFactor) &&
               EmissiveFactor.Equals(other.EmissiveFactor) &&
               MetallicFactor.Equals(other.MetallicFactor) &&
               RoughnessFactor.Equals(other.RoughnessFactor) &&
               OcclusionStrength.Equals(other.OcclusionStrength) &&
               AlphaCutoff.Equals(other.AlphaCutoff) &&
               BaseColorImageIndex == other.BaseColorImageIndex &&
               BaseColorSamplerIndex == other.BaseColorSamplerIndex &&
               NormalImageIndex == other.NormalImageIndex &&
               NormalSamplerIndex == other.NormalSamplerIndex &&
               EmissiveImageIndex == other.EmissiveImageIndex &&
               EmissiveSamplerIndex == other.EmissiveSamplerIndex &&
               HasEmissiveTexture == other.HasEmissiveTexture &&
               MetallicRoughnessImageIndex == other.MetallicRoughnessImageIndex &&
               MetallicRoughnessSamplerIndex == other.MetallicRoughnessSamplerIndex &&
               HasMetallicRoughnessTexture == other.HasMetallicRoughnessTexture &&
               OcclusionImageIndex == other.OcclusionImageIndex &&
               OcclusionSamplerIndex == other.OcclusionSamplerIndex &&
               HasOcclusionTexture == other.HasOcclusionTexture;
    }

    public override bool Equals(object? obj)
    {
        return obj is StaticMeshMaterialConstants other && Equals(other);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(BaseColorFactor);
        hash.Add(EmissiveFactor);
        hash.Add(MetallicFactor);
        hash.Add(RoughnessFactor);
        hash.Add(OcclusionStrength);
        hash.Add(AlphaCutoff);
        hash.Add(BaseColorImageIndex);
        hash.Add(BaseColorSamplerIndex);
        hash.Add(NormalImageIndex);
        hash.Add(NormalSamplerIndex);
        hash.Add(EmissiveImageIndex);
        hash.Add(EmissiveSamplerIndex);
        hash.Add(HasEmissiveTexture);
        hash.Add(MetallicRoughnessImageIndex);
        hash.Add(MetallicRoughnessSamplerIndex);
        hash.Add(HasMetallicRoughnessTexture);
        hash.Add(OcclusionImageIndex);
        hash.Add(OcclusionSamplerIndex);
        hash.Add(HasOcclusionTexture);
        return hash.ToHashCode();
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct StaticMeshLightingConstants
{
    public static StaticMeshLightingConstants Default => From(
        DirectionalLight.Default,
        SceneEnvironment.Default);

    public readonly Vector4 DirectionIntensity;
    public readonly Vector4 ColorAmbient;
    public readonly Vector4 EnvironmentAmbient;

    private StaticMeshLightingConstants(
        Vector4 directionIntensity,
        Vector4 colorAmbient,
        Vector4 environmentAmbient)
    {
        DirectionIntensity = directionIntensity;
        ColorAmbient = colorAmbient;
        EnvironmentAmbient = environmentAmbient;
    }

    public static StaticMeshLightingConstants From(
        DirectionalLight light,
        SceneEnvironment environment)
    {
        if (!light.IsValid)
        {
            light = DirectionalLight.Default;
        }

        if (!environment.IsValid)
        {
            environment = SceneEnvironment.Default;
        }

        return new StaticMeshLightingConstants(
            new Vector4(light.Direction, light.Intensity),
            new Vector4(light.Color, light.AmbientIntensity),
            new Vector4(environment.AmbientColor, environment.AmbientIntensity));
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct StaticMeshEnvironmentLightingConstants
{
    private const uint InvalidBindlessIndex = 0xFFFFFFFFu;

    public static StaticMeshEnvironmentLightingConstants Disabled => new(
        new Vector4(
            InvalidBindlessIndex,
            InvalidBindlessIndex,
            InvalidBindlessIndex,
            InvalidBindlessIndex),
        new Vector4(InvalidBindlessIndex, InvalidBindlessIndex, 0.0f, 0.0f),
        Vector4.Zero);

    public readonly Vector4 TextureIndices0;
    public readonly Vector4 TextureIndices1;
    public readonly Vector4 Parameters;

    private StaticMeshEnvironmentLightingConstants(
        Vector4 textureIndices0,
        Vector4 textureIndices1,
        Vector4 parameters)
    {
        TextureIndices0 = textureIndices0;
        TextureIndices1 = textureIndices1;
        Parameters = parameters;
    }

    public static StaticMeshEnvironmentLightingConstants From(
        RHIEnvironmentLightingResource? environmentLighting)
    {
        if (environmentLighting is not { IsValid: true } ||
            environmentLighting.IrradianceImageIndex == InvalidBindlessIndex ||
            environmentLighting.IrradianceSamplerIndex == InvalidBindlessIndex ||
            environmentLighting.PrefilteredSpecularImageIndex == InvalidBindlessIndex ||
            environmentLighting.PrefilteredSpecularSamplerIndex == InvalidBindlessIndex ||
            environmentLighting.BrdfIntegrationLutImageIndex == InvalidBindlessIndex ||
            environmentLighting.BrdfIntegrationLutSamplerIndex == InvalidBindlessIndex)
        {
            return Disabled;
        }

        return new StaticMeshEnvironmentLightingConstants(
            new Vector4(
                environmentLighting.IrradianceImageIndex,
                environmentLighting.IrradianceSamplerIndex,
                environmentLighting.PrefilteredSpecularImageIndex,
                environmentLighting.PrefilteredSpecularSamplerIndex),
            new Vector4(
                environmentLighting.BrdfIntegrationLutImageIndex,
                environmentLighting.BrdfIntegrationLutSamplerIndex,
                environmentLighting.PrefilteredSpecularMaxLod,
                1.0f),
            new Vector4(
                environmentLighting.RotationRadians,
                environmentLighting.Intensity,
                0.0f,
                0.0f));
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct StaticMeshShadowConstants
{
    private const uint InvalidBindlessIndex = 0xFFFFFFFFu;

    public static StaticMeshShadowConstants Disabled => new(
        new Vector4(InvalidBindlessIndex, InvalidBindlessIndex, 0.0f, 0.0f),
        new Vector4(0.0f, 0.0f, 1.0f / 2048.0f, 0.0f));

    public readonly Vector4 TextureIndices;
    public readonly Vector4 Parameters;

    private StaticMeshShadowConstants(Vector4 textureIndices, Vector4 parameters)
    {
        TextureIndices = textureIndices;
        Parameters = parameters;
    }

    public static StaticMeshShadowConstants From(
        uint shadowImageIndex,
        uint shadowSamplerIndex,
        float texelSize,
        float depthBias,
        float slopeBias,
        float strength,
        int pcfRadius,
        bool enabled)
    {
        bool isEnabled = enabled &&
                         shadowImageIndex != InvalidBindlessIndex &&
                         shadowSamplerIndex != InvalidBindlessIndex &&
                         texelSize > 0.0f;
        return new StaticMeshShadowConstants(
            new Vector4(shadowImageIndex, shadowSamplerIndex, pcfRadius, slopeBias),
            new Vector4(depthBias, strength, texelSize, isEnabled ? 1.0f : 0.0f));
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct StaticMeshDrawConstants
{
    private const uint InvalidBindlessIndex = 0xFFFFFFFFu;

    public static StaticMeshDrawConstants Identity => From(Vector4.One, StaticMeshLightingConstants.Default, Vector3.Zero, 0.0f, 1.0f, 0, 0, 0, 0, InvalidBindlessIndex, 0, 0, 0, false);

    public readonly Vector4 BaseColorFactor;
    public readonly Vector4 LightDirectionIntensity;
    public readonly Vector4 LightColorAmbient;
    public readonly Vector4 EnvironmentAmbient;
    public readonly Vector4 CameraWorldPosition;
    public readonly float MetallicFactor;
    public readonly float RoughnessFactor;
    public readonly uint BaseColorImageIndex;
    public readonly uint BaseColorSamplerIndex;
    public readonly uint NormalImageIndex;
    public readonly uint NormalSamplerIndex;
    public readonly uint ObjectBufferIndex;
    public readonly uint ObjectIndex;
    public readonly uint PointLightDataStart;
    public readonly uint PackedLocalLightCounts;
    public readonly uint EncodeOutputToSrgb;

    private StaticMeshDrawConstants(
        Vector4 baseColorFactor,
        StaticMeshLightingConstants lightingConstants,
        Vector3 cameraPosition,
        float metallicFactor,
        float roughnessFactor,
        uint baseColorImageIndex,
        uint baseColorSamplerIndex,
        uint normalImageIndex,
        uint normalSamplerIndex,
        uint objectBufferIndex,
        uint objectIndex,
        uint pointLightDataStart,
        uint packedLocalLightCounts,
        bool encodeOutputToSrgb)
    {
        BaseColorFactor = baseColorFactor;
        LightDirectionIntensity = lightingConstants.DirectionIntensity;
        LightColorAmbient = lightingConstants.ColorAmbient;
        EnvironmentAmbient = lightingConstants.EnvironmentAmbient;
        CameraWorldPosition = new Vector4(cameraPosition, 1.0f);
        MetallicFactor = metallicFactor;
        RoughnessFactor = roughnessFactor;
        BaseColorImageIndex = baseColorImageIndex;
        BaseColorSamplerIndex = baseColorSamplerIndex;
        NormalImageIndex = normalImageIndex;
        NormalSamplerIndex = normalSamplerIndex;
        ObjectBufferIndex = objectBufferIndex;
        ObjectIndex = objectIndex;
        PointLightDataStart = pointLightDataStart;
        PackedLocalLightCounts = packedLocalLightCounts;
        EncodeOutputToSrgb = encodeOutputToSrgb ? 1u : 0u;
    }

    public static StaticMeshDrawConstants From(
        Vector4 baseColorFactor,
        StaticMeshLightingConstants lightingConstants,
        Vector3 cameraPosition,
        float metallicFactor,
        float roughnessFactor,
        uint baseColorImageIndex,
        uint baseColorSamplerIndex,
        uint normalImageIndex,
        uint normalSamplerIndex,
        uint objectBufferIndex,
        uint objectIndex,
        uint pointLightDataStart,
        uint packedLocalLightCounts,
        bool encodeOutputToSrgb)
    {
        return new StaticMeshDrawConstants(
            baseColorFactor,
            lightingConstants,
            cameraPosition,
            metallicFactor,
            roughnessFactor,
            baseColorImageIndex,
            baseColorSamplerIndex,
            normalImageIndex,
            normalSamplerIndex,
            objectBufferIndex,
            objectIndex,
            pointLightDataStart,
            packedLocalLightCounts,
            encodeOutputToSrgb);
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
    public readonly Vector4 ShadowModelViewProjectionColumn0;
    public readonly Vector4 ShadowModelViewProjectionColumn1;
    public readonly Vector4 ShadowModelViewProjectionColumn2;
    public readonly Vector4 ShadowModelViewProjectionColumn3;
    public readonly Vector4 ShadowTextureIndices;
    public readonly Vector4 ShadowParameters;
    public readonly Vector4 EmissiveFactor;
    public readonly Vector4 EmissiveTextureIndices;
    public readonly Vector4 MetallicRoughnessTextureIndices;
    public readonly Vector4 OcclusionTextureIndices;
    public readonly Vector4 PbrMaterialParameters;
    public readonly Vector4 EnvironmentTextureIndices0;
    public readonly Vector4 EnvironmentTextureIndices1;
    public readonly Vector4 EnvironmentParameters;

    private StaticMeshObjectData(
        Vector4 modelViewProjectionColumn0,
        Vector4 modelViewProjectionColumn1,
        Vector4 modelViewProjectionColumn2,
        Vector4 modelViewProjectionColumn3,
        Vector4 localToWorldColumn0,
        Vector4 localToWorldColumn1,
        Vector4 localToWorldColumn2,
        Vector4 shadowModelViewProjectionColumn0,
        Vector4 shadowModelViewProjectionColumn1,
        Vector4 shadowModelViewProjectionColumn2,
        Vector4 shadowModelViewProjectionColumn3,
        Vector4 shadowTextureIndices,
        Vector4 shadowParameters,
        Vector4 emissiveFactor,
        Vector4 emissiveTextureIndices,
        Vector4 metallicRoughnessTextureIndices,
        Vector4 occlusionTextureIndices,
        Vector4 pbrMaterialParameters,
        StaticMeshEnvironmentLightingConstants environmentLightingConstants)
    {
        ModelViewProjectionColumn0 = modelViewProjectionColumn0;
        ModelViewProjectionColumn1 = modelViewProjectionColumn1;
        ModelViewProjectionColumn2 = modelViewProjectionColumn2;
        ModelViewProjectionColumn3 = modelViewProjectionColumn3;
        LocalToWorldColumn0 = localToWorldColumn0;
        LocalToWorldColumn1 = localToWorldColumn1;
        LocalToWorldColumn2 = localToWorldColumn2;
        ShadowModelViewProjectionColumn0 = shadowModelViewProjectionColumn0;
        ShadowModelViewProjectionColumn1 = shadowModelViewProjectionColumn1;
        ShadowModelViewProjectionColumn2 = shadowModelViewProjectionColumn2;
        ShadowModelViewProjectionColumn3 = shadowModelViewProjectionColumn3;
        ShadowTextureIndices = shadowTextureIndices;
        ShadowParameters = shadowParameters;
        EmissiveFactor = emissiveFactor;
        EmissiveTextureIndices = emissiveTextureIndices;
        MetallicRoughnessTextureIndices = metallicRoughnessTextureIndices;
        OcclusionTextureIndices = occlusionTextureIndices;
        PbrMaterialParameters = pbrMaterialParameters;
        EnvironmentTextureIndices0 = environmentLightingConstants.TextureIndices0;
        EnvironmentTextureIndices1 = environmentLightingConstants.TextureIndices1;
        EnvironmentParameters = environmentLightingConstants.Parameters;
    }

    public static StaticMeshObjectData From(
        Matrix4x4 localToWorld,
        Matrix4x4 viewProjection,
        Matrix4x4 shadowViewProjection,
        StaticMeshShadowConstants shadowConstants,
        Vector4 emissiveFactor,
        Vector4 emissiveTextureIndices,
        Vector4 metallicRoughnessTextureIndices,
        Vector4 occlusionTextureIndices,
        Vector4 pbrMaterialParameters,
        StaticMeshEnvironmentLightingConstants environmentLightingConstants)
    {
        var modelViewProjection = localToWorld * viewProjection;
        var shadowModelViewProjection = localToWorld * shadowViewProjection;
        return new StaticMeshObjectData(
            new Vector4(modelViewProjection.M11, modelViewProjection.M21, modelViewProjection.M31, modelViewProjection.M41),
            new Vector4(modelViewProjection.M12, modelViewProjection.M22, modelViewProjection.M32, modelViewProjection.M42),
            new Vector4(modelViewProjection.M13, modelViewProjection.M23, modelViewProjection.M33, modelViewProjection.M43),
            new Vector4(modelViewProjection.M14, modelViewProjection.M24, modelViewProjection.M34, modelViewProjection.M44),
            new Vector4(localToWorld.M11, localToWorld.M21, localToWorld.M31, localToWorld.M41),
            new Vector4(localToWorld.M12, localToWorld.M22, localToWorld.M32, localToWorld.M42),
            new Vector4(localToWorld.M13, localToWorld.M23, localToWorld.M33, localToWorld.M43),
            new Vector4(shadowModelViewProjection.M11, shadowModelViewProjection.M21, shadowModelViewProjection.M31, shadowModelViewProjection.M41),
            new Vector4(shadowModelViewProjection.M12, shadowModelViewProjection.M22, shadowModelViewProjection.M32, shadowModelViewProjection.M42),
            new Vector4(shadowModelViewProjection.M13, shadowModelViewProjection.M23, shadowModelViewProjection.M33, shadowModelViewProjection.M43),
            new Vector4(shadowModelViewProjection.M14, shadowModelViewProjection.M24, shadowModelViewProjection.M34, shadowModelViewProjection.M44),
            shadowConstants.TextureIndices,
            shadowConstants.Parameters,
            emissiveFactor,
            emissiveTextureIndices,
            metallicRoughnessTextureIndices,
            occlusionTextureIndices,
            pbrMaterialParameters,
            environmentLightingConstants);
    }

    public static StaticMeshObjectData From(PointLight light)
    {
        return new StaticMeshObjectData(
            new Vector4(light.Position, light.Range),
            new Vector4(light.Color, light.Intensity),
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            StaticMeshEnvironmentLightingConstants.Disabled);
    }

    public static StaticMeshObjectData From(SpotLight light)
    {
        return new StaticMeshObjectData(
            new Vector4(light.Position, light.Range),
            new Vector4(light.Color, light.Intensity),
            new Vector4(light.Direction, light.InnerConeCosine),
            new Vector4(light.OuterConeCosine, 0.0f, 0.0f, 0.0f),
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            StaticMeshEnvironmentLightingConstants.Disabled);
    }
}
