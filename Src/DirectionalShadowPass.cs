using Arisen.Native.RHI;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.RHI;
using ArisenEngine.Rendering.Resources;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ArisenEngine.Rendering;

internal sealed class DirectionalShadowPass : RenderPassNode, IDisposable
{
    private const ulong DynamicViewportScissorMask = 0x1UL | 0x2UL;
    private const string VertexStage = "Vertex";
    private const string FragmentStage = "Fragment";
    private const float RasterDepthBiasConstantFactor = 1.25f;
    private const float RasterDepthBiasSlopeFactor = 1.75f;
    private const uint InvalidBindlessIndex = 0xFFFFFFFFu;
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly GenericRenderMaterialLibrary m_MaterialLibrary;
    private readonly ShaderAsset m_Shader;
    private readonly ShaderAsset m_SkinnedShader;
    private readonly RHIImageViewHandle[] m_DepthImageViews = new RHIImageViewHandle[4];
    private RHIFactory m_Factory;
    private RHIPipelineCache? m_PipelineCache;
    private RHIPipelineState m_PipelineState;
    private RHIPipelineHandle m_Pipeline = RHIPipelineHandle.Invalid;
    private RHIShaderProgramHandle m_VertexProgram = RHIShaderProgramHandle.Invalid;
    private RHIShaderProgramHandle m_FragmentProgram = RHIShaderProgramHandle.Invalid;
    private CookedAssetHandle m_VertexShaderAsset = CookedAssetHandle.Invalid;
    private CookedAssetHandle m_FragmentShaderAsset = CookedAssetHandle.Invalid;
    private RHIPipelineState m_SkinnedPipelineState;
    private RHIPipelineHandle m_SkinnedPipeline = RHIPipelineHandle.Invalid;
    private RHIShaderProgramHandle m_SkinnedVertexProgram = RHIShaderProgramHandle.Invalid;
    private RHIShaderProgramHandle m_SkinnedFragmentProgram = RHIShaderProgramHandle.Invalid;
    private CookedAssetHandle m_SkinnedVertexShaderAsset = CookedAssetHandle.Invalid;
    private CookedAssetHandle m_SkinnedFragmentShaderAsset = CookedAssetHandle.Invalid;
    private AssetDependencyStamp m_ShaderStamp = AssetDependencyStamp.Empty;
    private EFormat m_TargetDepthFormat = EFormat.FORMAT_UNDEFINED;
    private uint m_TargetWidth;
    private uint m_TargetHeight;
    private EFormat m_PipelineDepthFormat = EFormat.FORMAT_UNDEFINED;
    private RHIBufferHandle m_SkinPaletteBuffer = RHIBufferHandle.Invalid;
    private uint m_SkinPaletteBufferBindlessIndex = InvalidBindlessIndex;
    private bool m_HasSkinPaletteData;
    private bool m_HasSkinnedDraws;
    private DirectionalShadowCascadeSet m_Cascades;
    private DirectionalShadowCascadeDrawRangeSet m_DrawRanges;
    private DirectionalShadowFrameData m_FrameData;
    private MeshDrawCommand[] m_PreparedDraws = Array.Empty<MeshDrawCommand>();
    private ShadowDepthDrawConstants[] m_DrawConstants = Array.Empty<ShadowDepthDrawConstants>();
    private int m_PreparedDrawCount;
    private bool m_Disposed;

    public Matrix4x4 ViewProjection => m_Cascades.IsValid
        ? m_Cascades.GetCascade(0).ViewProjection
        : Matrix4x4.Identity;
    public bool HasRenderableShadowMap =>
        HasValidDepthTarget &&
        m_Pipeline.IsValid &&
        (!m_HasSkinnedDraws || m_SkinnedPipeline.IsValid) &&
        m_Cascades.IsValid &&
        m_FrameData.Enabled;

    private bool HasValidDepthTarget =>
        m_TargetDepthFormat != EFormat.FORMAT_UNDEFINED &&
        m_TargetWidth > 0 &&
        m_TargetHeight > 0 &&
        HasValidLayerViews();

    public DirectionalShadowPass(
        IAssetDatabase assetDatabase,
        GenericRenderMaterialLibrary materialLibrary,
        string name = "DirectionalShadowPass") : base(name)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        m_MaterialLibrary = materialLibrary ?? throw new ArgumentNullException(nameof(materialLibrary));
        m_Shader = GenericRenderPipelineShaderAssets.CreateDirectionalShadow();
        m_SkinnedShader = MeshSkinningShaderVariant.Create(m_Shader);
    }

    public void SetDepthTargets(RenderGraphTexture texture, int cascadeCount)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (cascadeCount < 1 || cascadeCount > m_DepthImageViews.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(cascadeCount));
        }

        if (!texture.IsValid ||
            texture.ArrayLayers < (uint)cascadeCount ||
            texture.Format == EFormat.FORMAT_UNDEFINED ||
            texture.Width == 0 ||
            texture.Height == 0)
        {
            throw new ArgumentException(
                "[DirectionalShadowPass] Graph-owned cascade depth texture is invalid.",
                nameof(texture));
        }

        for (int index = 0; index < m_DepthImageViews.Length; index++)
        {
            m_DepthImageViews[index] = index < cascadeCount
                ? texture.GetLayerImageView(checked((uint)index))
                : RHIImageViewHandle.Invalid;
        }

        m_TargetDepthFormat = texture.Format;
        m_TargetWidth = texture.Width;
        m_TargetHeight = texture.Height;
    }

    public void SetCascades(
        in DirectionalShadowCascadeSet cascades,
        in DirectionalShadowCascadeDrawRangeSet drawRanges,
        in DirectionalShadowFrameData frameData)
    {
        if (!cascades.IsValid ||
            drawRanges.Count != cascades.Count ||
            drawRanges.TotalDrawCount < 0)
        {
            throw new ArgumentException(
                "[DirectionalShadowPass] Cascade draw ranges do not match the cascade set.",
                nameof(drawRanges));
        }

        m_Cascades = cascades;
        m_DrawRanges = drawRanges;
        m_FrameData = frameData;
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

    /// <summary>
    /// Hands the pass the frame's skin palette storage. A skinned caster reads the palette through
    /// the bindless table, exactly as its lit draw does, so a shadow follows the pose rather than the
    /// bind pose.
    /// </summary>
    public void SetPreparedSkinning(RHIBufferHandle paletteBuffer, uint paletteBindlessIndex, bool hasData)
    {
        m_SkinPaletteBuffer = hasData ? paletteBuffer : RHIBufferHandle.Invalid;
        m_SkinPaletteBufferBindlessIndex = hasData ? paletteBindlessIndex : InvalidBindlessIndex;
        m_HasSkinPaletteData = hasData;
    }

    public void Prepare(RenderContext context)
    {
        using var _ = Profiler.Zone("DirectionalShadowPass.Prepare");

        ThrowIfDisposed();

        if (!HasValidDepthTarget)
        {
            return;
        }

        var factory = context.Device.GetFactory();
        var depthFormat = m_TargetDepthFormat;
        m_HasSkinnedDraws = HasSkinnedDraws();
        if (m_HasSkinnedDraws && (!m_HasSkinPaletteData || !m_SkinPaletteBuffer.IsValid))
        {
            throw new InvalidOperationException(
                "[DirectionalShadowPass] Prepared shadow casters reference a skin palette, but the frame " +
                "uploaded no palette data. Skinned casters must be prepared with their palette block.");
        }

        EnsureDrawConstants();
        var shaderStamp = AssetDependencyTracker.GetShaderStamp(m_AssetDatabase, m_Shader);
        bool skinnedPipelineReady = !m_HasSkinnedDraws || m_SkinnedPipeline.IsValid;
        if (m_Pipeline.IsValid &&
            skinnedPipelineReady &&
            m_PipelineDepthFormat == depthFormat &&
            m_ShaderStamp == shaderStamp)
        {
            PlotDiagnostics();
            return;
        }

        ReleasePipelineResources();
        m_Factory = factory;
        m_PipelineDepthFormat = depthFormat;
        RHIPipelineCache pipelineCache = context.Device.PipelineCache;
        m_PipelineCache = pipelineCache;

        try
        {
            EnsurePipeline(pipelineCache, depthFormat, skinned: false);
            if (m_HasSkinnedDraws)
            {
                EnsurePipeline(pipelineCache, depthFormat, skinned: true);
            }

            m_ShaderStamp = shaderStamp;
            Logger.Log(
                $"[DirectionalShadowPass] Prepared pipeline | DepthFormat: {depthFormat} | " +
                $"RasterBias: {RasterDepthBiasConstantFactor}/{RasterDepthBiasSlopeFactor} | " +
                $"Pipeline: {m_Pipeline.Index}:{m_Pipeline.Generation} | " +
                $"Skinned: {m_HasSkinnedDraws} | " +
                $"SkinnedPipeline: {m_SkinnedPipeline.Index}:{m_SkinnedPipeline.Generation}");
            PlotDiagnostics();
        }
        catch
        {
            ReleasePipelineResources();
            throw;
        }
    }

    /// <summary>
    /// Builds one of the pass's two pipelines. The static one reads the stride-60 static vertex the
    /// depth-only draw has always used; the skinned one reads the same attributes with the appended
    /// joint indices and weights on the shader's SKINNED variant, so a skinned caster deforms exactly
    /// as its lit draw does.
    /// </summary>
    private void EnsurePipeline(
        RHIPipelineCache pipelineCache,
        EFormat depthFormat,
        bool skinned)
    {
        ShaderAsset shader = skinned ? m_SkinnedShader : m_Shader;
        RHIShaderProgramHandle vertexProgram = CompileProgram(
            shader,
            EShaderStage.SHADER_STAGE_VERTEX_BIT,
            VertexStage,
            out CookedAssetHandle vertexShaderAsset);
        RHIShaderProgramHandle fragmentProgram = CompileProgram(
            shader,
            EShaderStage.SHADER_STAGE_FRAGMENT_BIT,
            FragmentStage,
            out CookedAssetHandle fragmentShaderAsset);

        RHIPipelineState pipelineState = default;
        RHIPipelineHandle pipeline = RHIPipelineHandle.Invalid;
        try
        {
            pipelineState = pipelineCache.GetPipelineState();
            pipelineState.AddProgram(vertexProgram);
            pipelineState.AddProgram(fragmentProgram);
            pipelineState.SetBindPoint(EPipelineBindPoint.PIPELINE_BIND_POINT_GRAPHICS);
            pipelineState.SetInputAssemblyState(EPrimitiveTopology.PRIMITIVE_TOPOLOGY_TRIANGLE_LIST);
            pipelineState.ClearVertexInputDescriptions();
            pipelineState.AddVertexBindingDescription(
                0,
                skinned
                    ? MeshAssetCooker.SkinnedMeshVertexStride
                    : MeshAssetCooker.StaticMeshVertexStride,
                EVertexInputRate.VERTEX_INPUT_RATE_VERTEX);
            pipelineState.AddVertexInputAttributeDescription(0, 0, EFormat.FORMAT_R32G32B32_SFLOAT, 0);
            pipelineState.AddVertexInputAttributeDescription(1, 0, EFormat.FORMAT_R32G32B32_SFLOAT, 12);
            pipelineState.AddVertexInputAttributeDescription(2, 0, EFormat.FORMAT_R32G32B32A32_SFLOAT, 24);
            pipelineState.AddVertexInputAttributeDescription(3, 0, EFormat.FORMAT_R32G32_SFLOAT, 40);
            pipelineState.AddVertexInputAttributeDescription(4, 0, EFormat.FORMAT_R32G32B32_SFLOAT, 48);
            if (skinned)
            {
                pipelineState.AddVertexInputAttributeDescription(5, 0, EFormat.FORMAT_R8G8B8A8_UINT, 60);
                pipelineState.AddVertexInputAttributeDescription(6, 0, EFormat.FORMAT_R32G32B32A32_SFLOAT, 64);
            }

            pipelineState.SetRasterizationStateWithDepthBias(
                EPolygonMode.EPOLYGON_MODE_FILL,
                ECullModeFlagBits.CULL_MODE_BACK_BIT,
                EFrontFace.FRONT_FACE_COUNTER_CLOCKWISE,
                RasterDepthBiasConstantFactor,
                depthBiasClamp: 0.0f,
                depthBiasSlopeFactor: RasterDepthBiasSlopeFactor);
            pipelineState.SetColorBlendState(false);
            pipelineState.SetDepthStencilState(true, true, ECompareOp.COMPARE_OP_LESS_OR_EQUAL);
            pipelineState.SetDynamicStateMask(DynamicViewportScissorMask);
            pipelineState.SetRenderingFormats(Array.Empty<EFormat>(), depthFormat);
            pipelineState.BuildDescriptorSetLayout();

            pipeline = pipelineCache.GetGraphicsPipeline(pipelineState);
            if (!pipeline.IsValid)
            {
                throw new InvalidOperationException(
                    $"[DirectionalShadowPass] Failed to create the {(skinned ? "skinned" : "static")} graphics pipeline.");
            }
        }
        catch
        {
            if (pipeline.IsValid)
            {
                pipelineCache.ReleasePipeline(pipeline);
            }

            if (pipelineState.IsValid)
            {
                pipelineState.Release();
            }

            if (m_Factory.IsValid && vertexProgram.IsValid)
            {
                m_Factory.ReleaseGPUProgram(vertexProgram);
            }
            if (m_Factory.IsValid && fragmentProgram.IsValid)
            {
                m_Factory.ReleaseGPUProgram(fragmentProgram);
            }
            if (vertexShaderAsset.IsValid)
            {
                m_AssetDatabase.Release(vertexShaderAsset);
            }
            if (fragmentShaderAsset.IsValid)
            {
                m_AssetDatabase.Release(fragmentShaderAsset);
            }

            throw;
        }

        if (skinned)
        {
            m_SkinnedVertexProgram = vertexProgram;
            m_SkinnedFragmentProgram = fragmentProgram;
            m_SkinnedVertexShaderAsset = vertexShaderAsset;
            m_SkinnedFragmentShaderAsset = fragmentShaderAsset;
            m_SkinnedPipelineState = pipelineState;
            m_SkinnedPipeline = pipeline;
            return;
        }

        m_VertexProgram = vertexProgram;
        m_FragmentProgram = fragmentProgram;
        m_VertexShaderAsset = vertexShaderAsset;
        m_FragmentShaderAsset = fragmentShaderAsset;
        m_PipelineState = pipelineState;
        m_Pipeline = pipeline;
    }

    protected override void Record(RenderContext context, RenderCommandList commandList)
    {
        if (!m_Pipeline.IsValid ||
            !HasValidDepthTarget)
        {
            return;
        }

        if (m_HasSkinnedDraws && !m_SkinnedPipeline.IsValid)
        {
            throw new InvalidOperationException(
                "[DirectionalShadowPass] The frame declares skinned shadow casters, but the skinned " +
                "pipeline was never prepared for them.");
        }

        RecordFrameDataBarrier(commandList);
        int cascadeCount = m_Cascades.Count;
        for (int cascadeIndex = 0; cascadeIndex < cascadeCount; cascadeIndex++)
        {
            DirectionalShadowCascadeDrawRange range = m_DrawRanges.GetRange(cascadeIndex);
            commandList.BeginRenderingDepthOnly(
                m_DepthImageViews[cascadeIndex],
                EImageLayout.IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL,
                EAttachmentLoadOp.ATTACHMENT_LOAD_OP_CLEAR,
                EAttachmentStoreOp.ATTACHMENT_STORE_OP_STORE,
                1.0f,
                0,
                0,
                0,
                m_TargetWidth,
                m_TargetHeight);
            commandList.BindPipeline(m_Pipeline);
            bool skinnedPipelineBound = false;
            commandList.SetViewport(0, 0, m_TargetWidth, m_TargetHeight);
            commandList.SetScissor(0, 0, m_TargetWidth, m_TargetHeight);

            for (int drawIndex = range.Start; drawIndex < range.End; drawIndex++)
            {
                ref readonly MeshDrawCommand draw = ref m_PreparedDraws[drawIndex];
                if (!IsDrawable(draw))
                {
                    continue;
                }

                // Casters are ordered by material and distance, so a skinned caster can follow a
                // static one; the pipeline only changes when the draw's vertex layout does.
                if (draw.IsSkinned != skinnedPipelineBound)
                {
                    commandList.BindPipeline(draw.IsSkinned ? m_SkinnedPipeline : m_Pipeline);
                    skinnedPipelineBound = draw.IsSkinned;
                }

                commandList.PushConstants(
                    m_DrawConstants[drawIndex],
                    EShaderStage.SHADER_STAGE_VERTEX_BIT |
                    EShaderStage.SHADER_STAGE_FRAGMENT_BIT);
                commandList.BindVertexBuffers(draw.VertexBuffer);
                commandList.BindIndexBuffer(draw.IndexBuffer, 0, draw.IndexType);
                commandList.DrawIndexed(
                    draw.IndexCount,
                    firstIndex: draw.FirstIndex,
                    vertexOffset: draw.VertexOffset);
            }

            commandList.EndRendering();
        }
    }

    public void Dispose()
    {
        if (m_Disposed)
        {
            return;
        }

        ReleasePipelineResources();
        Array.Fill(m_DepthImageViews, RHIImageViewHandle.Invalid);
        m_TargetDepthFormat = EFormat.FORMAT_UNDEFINED;
        m_TargetWidth = 0;
        m_TargetHeight = 0;
        m_Disposed = true;
    }

    private void EnsureDrawConstants()
    {
        if (m_DrawConstants.Length < m_PreparedDrawCount)
        {
            Array.Resize(ref m_DrawConstants, m_PreparedDrawCount);
        }

        int cascadeCount = m_Cascades.Count;
        for (int cascadeIndex = 0; cascadeIndex < cascadeCount; cascadeIndex++)
        {
            DirectionalShadowCascade cascade = m_Cascades.GetCascade(cascadeIndex);
            DirectionalShadowCascadeDrawRange range = m_DrawRanges.GetRange(cascadeIndex);
            for (int drawIndex = range.Start; drawIndex < range.End; drawIndex++)
            {
                ref readonly MeshDrawCommand draw = ref m_PreparedDraws[drawIndex];
                StaticMeshMaterialConstants material = GetMaterialConstants(draw.MaterialID);
                Matrix4x4 cameraRelativeLocalToWorld =
                    DirectionalShadowCoordinateSpace.ToCameraRelative(
                        draw.LocalToWorld,
                        m_Cascades.CameraPosition);
                m_DrawConstants[drawIndex] = ShadowDepthDrawConstants.From(
                    cameraRelativeLocalToWorld,
                    cascade.ViewProjection,
                    material,
                    m_MaterialLibrary.GetRenderQueue(draw.MaterialID).Class ==
                        RenderQueueClass.AlphaTest,
                    draw.IsSkinned ? m_SkinPaletteBufferBindlessIndex : InvalidBindlessIndex,
                    draw.IsSkinned ? draw.SkinPaletteOffset : 0u);
            }
        }
    }

    private bool HasSkinnedDraws()
    {
        for (int drawIndex = 0; drawIndex < m_PreparedDrawCount; drawIndex++)
        {
            if (m_PreparedDraws[drawIndex].IsSkinned)
            {
                return true;
            }
        }

        return false;
    }

    private RHIShaderProgramHandle CompileProgram(
        ShaderAsset shader,
        EShaderStage rhiStage,
        string stageName,
        out CookedAssetHandle shaderAssetHandle)
    {
        shaderAssetHandle = CookedAssetHandle.Invalid;
        RHIShaderProgramHandle program = RHIShaderProgramHandle.Invalid;

        try
        {
            var cookedStage = ShaderAssetCooker.LoadOrCookStage(
                m_AssetDatabase,
                shader,
                stageName);
            shaderAssetHandle = cookedStage.Handle;
            var shaderCode = m_AssetDatabase.GetCookedAssetBytes(shaderAssetHandle);

            program = m_Factory.CreateGPUProgram();
            if (!program.IsValid)
            {
                throw new InvalidOperationException(
                    $"[DirectionalShadowPass] Failed to allocate shader program for {cookedStage.Stage.EntryPoint}.");
            }

            if (!m_Factory.AttachProgramByteCode(
                    program,
                    rhiStage,
                    shaderCode,
                    cookedStage.Stage.EntryPoint))
            {
                throw new InvalidOperationException(
                    $"[DirectionalShadowPass] Failed to attach shader bytecode for {cookedStage.Stage.EntryPoint}.");
            }

            return program;
        }
        catch
        {
            if (program.IsValid)
            {
                m_Factory.ReleaseGPUProgram(program);
            }

            if (shaderAssetHandle.IsValid)
            {
                m_AssetDatabase.Release(shaderAssetHandle);
                shaderAssetHandle = CookedAssetHandle.Invalid;
            }

            throw;
        }
    }

    private void ReleasePipelineResources()
    {
        if (m_Pipeline.IsValid && m_PipelineCache is not null)
        {
            m_PipelineCache.ReleasePipeline(m_Pipeline);
        }
        m_Pipeline = RHIPipelineHandle.Invalid;

        if (m_SkinnedPipeline.IsValid && m_PipelineCache is not null)
        {
            m_PipelineCache.ReleasePipeline(m_SkinnedPipeline);
        }
        m_SkinnedPipeline = RHIPipelineHandle.Invalid;

        if (m_PipelineState.IsValid)
        {
            m_PipelineState.Release();
        }

        if (m_SkinnedPipelineState.IsValid)
        {
            m_SkinnedPipelineState.Release();
        }

        if (m_Factory.IsValid && m_VertexProgram.IsValid)
        {
            m_Factory.ReleaseGPUProgram(m_VertexProgram);
        }
        if (m_Factory.IsValid && m_FragmentProgram.IsValid)
        {
            m_Factory.ReleaseGPUProgram(m_FragmentProgram);
        }
        if (m_Factory.IsValid && m_SkinnedVertexProgram.IsValid)
        {
            m_Factory.ReleaseGPUProgram(m_SkinnedVertexProgram);
        }
        if (m_Factory.IsValid && m_SkinnedFragmentProgram.IsValid)
        {
            m_Factory.ReleaseGPUProgram(m_SkinnedFragmentProgram);
        }

        if (m_VertexShaderAsset.IsValid)
        {
            m_AssetDatabase.Release(m_VertexShaderAsset);
        }
        if (m_FragmentShaderAsset.IsValid)
        {
            m_AssetDatabase.Release(m_FragmentShaderAsset);
        }
        if (m_SkinnedVertexShaderAsset.IsValid)
        {
            m_AssetDatabase.Release(m_SkinnedVertexShaderAsset);
        }
        if (m_SkinnedFragmentShaderAsset.IsValid)
        {
            m_AssetDatabase.Release(m_SkinnedFragmentShaderAsset);
        }

        m_PipelineState = default;
        m_SkinnedPipelineState = default;
        m_PipelineCache = null;
        m_VertexProgram = RHIShaderProgramHandle.Invalid;
        m_FragmentProgram = RHIShaderProgramHandle.Invalid;
        m_VertexShaderAsset = CookedAssetHandle.Invalid;
        m_FragmentShaderAsset = CookedAssetHandle.Invalid;
        m_SkinnedVertexProgram = RHIShaderProgramHandle.Invalid;
        m_SkinnedFragmentProgram = RHIShaderProgramHandle.Invalid;
        m_SkinnedVertexShaderAsset = CookedAssetHandle.Invalid;
        m_SkinnedFragmentShaderAsset = CookedAssetHandle.Invalid;
        m_ShaderStamp = AssetDependencyStamp.Empty;
        m_PipelineDepthFormat = EFormat.FORMAT_UNDEFINED;
    }

    private void PlotDiagnostics()
    {
        Profiler.PlotValue("DirectionalShadowPass.DrawCount", m_PreparedDrawCount);
        Profiler.PlotValue("DirectionalShadowPass.ShadowMapSize", m_TargetWidth);
        Profiler.PlotValue("DirectionalShadowPass.DepthFormat", (double)(uint)m_TargetDepthFormat);
        Profiler.PlotValue("DirectionalShadowPass.Enabled", HasRenderableShadowMap ? 1 : 0);
        Profiler.PlotValue("DirectionalShadowPass.CascadeCount", m_Cascades.Count);
        Profiler.PlotValue("DirectionalShadowPass.DroppedDrawCount", m_DrawRanges.DroppedDrawCount);
        if (m_Cascades.IsValid)
        {
            DirectionalShadowCascade firstCascade = m_Cascades.GetCascade(0);
            Profiler.PlotValue("DirectionalShadowPass.SceneFitted", 1);
            Profiler.PlotValue("DirectionalShadowPass.Diameter", firstCascade.Diameter);
            Profiler.PlotValue("DirectionalShadowPass.Depth", firstCascade.Depth);
            Profiler.PlotValue(
                "DirectionalShadowPass.WorldUnitsPerTexel",
                firstCascade.WorldUnitsPerTexel);
        }
    }

    private StaticMeshMaterialConstants GetMaterialConstants(uint materialId)
    {
        uint resolvedMaterialId = materialId == 0
            ? m_MaterialLibrary.DefaultMaterialID
            : materialId;
        return StaticMeshPass.CreateMaterialConstants(
            m_MaterialLibrary.GetPreparedMaterial(resolvedMaterialId));
    }

    private void RecordFrameDataBarrier(RenderCommandList commandList)
    {
        if (!m_FrameData.ConstantsBuffer.IsValid)
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
            Buffer = m_FrameData.ConstantsBuffer,
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

    private bool HasValidLayerViews()
    {
        int cascadeCount = m_Cascades.Count;
        if (cascadeCount < 1 || cascadeCount > m_DepthImageViews.Length)
        {
            return false;
        }

        for (int index = 0; index < cascadeCount; index++)
        {
            if (!m_DepthImageViews[index].IsValid)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDrawable(in MeshDrawCommand draw)
    {
        return draw.VertexBuffer.IsValid &&
               draw.IndexBuffer.IsValid &&
               draw.IndexCount != 0;
    }

    private void ThrowIfDisposed()
    {
        if (m_Disposed)
        {
            throw new ObjectDisposedException(nameof(DirectionalShadowPass));
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct ShadowDepthDrawConstants
{
    public readonly Vector4 ModelViewProjectionColumn0;
    public readonly Vector4 ModelViewProjectionColumn1;
    public readonly Vector4 ModelViewProjectionColumn2;
    public readonly Vector4 ModelViewProjectionColumn3;
    public readonly Vector4 BaseColorFactor;
    public readonly uint BaseColorImageIndex;
    public readonly uint BaseColorSamplerIndex;
    public readonly float AlphaCutoff;
    public readonly uint AlphaTest;
    public readonly uint SkinPaletteBufferIndex;
    public readonly uint SkinPaletteOffset;

    private ShadowDepthDrawConstants(
        Vector4 modelViewProjectionColumn0,
        Vector4 modelViewProjectionColumn1,
        Vector4 modelViewProjectionColumn2,
        Vector4 modelViewProjectionColumn3,
        Vector4 baseColorFactor,
        uint baseColorImageIndex,
        uint baseColorSamplerIndex,
        float alphaCutoff,
        bool alphaTest,
        uint skinPaletteBufferIndex,
        uint skinPaletteOffset)
    {
        ModelViewProjectionColumn0 = modelViewProjectionColumn0;
        ModelViewProjectionColumn1 = modelViewProjectionColumn1;
        ModelViewProjectionColumn2 = modelViewProjectionColumn2;
        ModelViewProjectionColumn3 = modelViewProjectionColumn3;
        BaseColorFactor = baseColorFactor;
        BaseColorImageIndex = baseColorImageIndex;
        BaseColorSamplerIndex = baseColorSamplerIndex;
        AlphaCutoff = alphaCutoff;
        AlphaTest = alphaTest ? 1u : 0u;
        SkinPaletteBufferIndex = skinPaletteBufferIndex;
        SkinPaletteOffset = skinPaletteOffset;
    }

    public static ShadowDepthDrawConstants From(
        Matrix4x4 localToWorld,
        Matrix4x4 viewProjection,
        in StaticMeshMaterialConstants material,
        bool alphaTest,
        uint skinPaletteBufferIndex,
        uint skinPaletteOffset)
    {
        var modelViewProjection = localToWorld * viewProjection;
        return new ShadowDepthDrawConstants(
            new Vector4(modelViewProjection.M11, modelViewProjection.M21, modelViewProjection.M31, modelViewProjection.M41),
            new Vector4(modelViewProjection.M12, modelViewProjection.M22, modelViewProjection.M32, modelViewProjection.M42),
            new Vector4(modelViewProjection.M13, modelViewProjection.M23, modelViewProjection.M33, modelViewProjection.M43),
            new Vector4(modelViewProjection.M14, modelViewProjection.M24, modelViewProjection.M34, modelViewProjection.M44),
            material.BaseColorFactor,
            material.BaseColorImageIndex,
            material.BaseColorSamplerIndex,
            material.AlphaCutoff,
            alphaTest,
            skinPaletteBufferIndex,
            skinPaletteOffset);
    }
}
