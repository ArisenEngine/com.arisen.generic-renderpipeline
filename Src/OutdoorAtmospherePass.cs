using Arisen.Native.RHI;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.RHI;
using ArisenEngine.Rendering.Resources;

namespace ArisenEngine.Rendering;

internal sealed class OutdoorAtmospherePass : RenderPassNode, IDisposable
{
    private const ulong DynamicViewportScissorMask = 0x1UL | 0x2UL;
    private const string VertexStage = "Vertex";
    private const string FragmentStage = "Fragment";

    private readonly IAssetDatabase m_AssetDatabase;
    private readonly ShaderAsset m_Shader;
    private RHIFactory m_Factory;
    private RHIPipelineCache? m_PipelineCache;
    private RHIPipelineState m_PipelineState;
    private RHIPipelineHandle m_Pipeline = RHIPipelineHandle.Invalid;
    private RHIShaderProgramHandle m_VertexProgram = RHIShaderProgramHandle.Invalid;
    private RHIShaderProgramHandle m_FragmentProgram = RHIShaderProgramHandle.Invalid;
    private CookedAssetHandle m_VertexShaderAsset = CookedAssetHandle.Invalid;
    private CookedAssetHandle m_FragmentShaderAsset = CookedAssetHandle.Invalid;
    private AssetDependencyStamp m_ShaderStamp = AssetDependencyStamp.Empty;
    private RHIImageViewHandle m_TargetImageView = RHIImageViewHandle.Invalid;
    private EFormat m_TargetColorFormat = EFormat.FORMAT_UNDEFINED;
    private EFormat m_ColorFormat = EFormat.FORMAT_UNDEFINED;
    private EnvironmentFrameData m_FrameData;
    private bool m_Disposed;

    public OutdoorAtmospherePass(
        IAssetDatabase assetDatabase,
        string name = "OutdoorAtmospherePass") : base(name)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        m_Shader = GenericRenderPipelineShaderAssets.CreateOutdoorAtmosphere();
    }

    public void SetFrameData(in EnvironmentFrameData frameData)
    {
        m_FrameData = frameData;
    }

    public void SetColorTarget(RHIImageViewHandle imageView, EFormat format)
    {
        m_TargetImageView = imageView;
        m_TargetColorFormat = format;
    }

    public void Prepare(RenderContext context)
    {
        using var _ = Profiler.Zone("OutdoorAtmospherePass.Prepare");
        ThrowIfDisposed();
        if (!m_FrameData.AtmosphereEnabled)
        {
            return;
        }

        var factory = context.Device.GetFactory();
        var colorFormat = m_TargetColorFormat;
        if (colorFormat == EFormat.FORMAT_UNDEFINED)
        {
            throw new InvalidOperationException(
                "[OutdoorAtmospherePass] A graph-owned HDR color target is required.");
        }

        var shaderStamp = AssetDependencyTracker.GetShaderStamp(m_AssetDatabase, m_Shader);
        if (m_Pipeline.IsValid &&
            m_ColorFormat == colorFormat &&
            m_ShaderStamp == shaderStamp)
        {
            return;
        }

        ReleasePipelineResources();
        m_Factory = factory;
        m_ColorFormat = colorFormat;
        RHIPipelineCache pipelineCache = context.Device.PipelineCache;
        m_PipelineCache = pipelineCache;
        try
        {
            m_VertexProgram = CompileProgram(
                EShaderStage.SHADER_STAGE_VERTEX_BIT,
                VertexStage,
                out m_VertexShaderAsset);
            m_FragmentProgram = CompileProgram(
                EShaderStage.SHADER_STAGE_FRAGMENT_BIT,
                FragmentStage,
                out m_FragmentShaderAsset);

            m_PipelineState = pipelineCache.GetPipelineState();
            m_PipelineState.AddProgram(m_VertexProgram);
            m_PipelineState.AddProgram(m_FragmentProgram);
            m_PipelineState.SetBindPoint(EPipelineBindPoint.PIPELINE_BIND_POINT_GRAPHICS);
            m_PipelineState.SetInputAssemblyState(EPrimitiveTopology.PRIMITIVE_TOPOLOGY_TRIANGLE_LIST);
            m_PipelineState.ClearVertexInputDescriptions();
            m_PipelineState.SetRasterizationState(
                EPolygonMode.EPOLYGON_MODE_FILL,
                ECullModeFlagBits.CULL_MODE_NONE,
                EFrontFace.FRONT_FACE_COUNTER_CLOCKWISE);
            m_PipelineState.SetColorBlendState(
                true,
                EBlendFactor.BLEND_FACTOR_SRC_ALPHA,
                EBlendFactor.BLEND_FACTOR_ONE_MINUS_SRC_ALPHA,
                EBlendOp.BLEND_OP_ADD);
            m_PipelineState.SetDepthStencilState(false, false, ECompareOp.COMPARE_OP_ALWAYS);
            m_PipelineState.SetDynamicStateMask(DynamicViewportScissorMask);
            m_PipelineState.SetRenderingFormats(new[] { colorFormat }, EFormat.FORMAT_UNDEFINED);
            m_PipelineState.BuildDescriptorSetLayout();

            m_Pipeline = pipelineCache.GetGraphicsPipeline(m_PipelineState);
            if (!m_Pipeline.IsValid)
            {
                throw new InvalidOperationException(
                    "[OutdoorAtmospherePass] Failed to create graphics pipeline.");
            }

            m_ShaderStamp = shaderStamp;
            Logger.Log(
                $"[OutdoorAtmospherePass] Prepared pipeline | Format: {colorFormat} | " +
                $"Pipeline: {m_Pipeline.Index}:{m_Pipeline.Generation}");
        }
        catch
        {
            ReleasePipelineResources();
            throw;
        }
    }

    protected override void Record(RenderContext context, RenderCommandList commandList)
    {
        using var _ = Profiler.Zone("OutdoorAtmospherePass.Record");
        if (!m_Pipeline.IsValid ||
            !m_FrameData.IsValid ||
            !m_FrameData.AtmosphereEnabled ||
            !m_TargetImageView.IsValid)
        {
            return;
        }

        commandList.BeginRendering(
            m_TargetImageView,
            EImageLayout.IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
            EAttachmentLoadOp.ATTACHMENT_LOAD_OP_LOAD,
            EAttachmentStoreOp.ATTACHMENT_STORE_OP_STORE,
            0.0f,
            0.0f,
            0.0f,
            0.0f,
            0,
            0,
            context.Width,
            context.Height);
        commandList.BindPipeline(m_Pipeline);
        commandList.PushConstants(
            EnvironmentFramePushConstants.From(m_FrameData),
            EShaderStage.SHADER_STAGE_VERTEX_BIT | EShaderStage.SHADER_STAGE_FRAGMENT_BIT);
        commandList.SetViewport(0, 0, context.Width, context.Height);
        commandList.SetScissor(0, 0, context.Width, context.Height);
        commandList.Draw(3);
        commandList.EndRendering();
    }

    public void Dispose()
    {
        if (m_Disposed)
        {
            return;
        }

        ReleasePipelineResources();
        m_Disposed = true;
    }

    private RHIShaderProgramHandle CompileProgram(
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
                m_Shader,
                stageName);
            shaderAssetHandle = cookedStage.Handle;
            var shaderCode = m_AssetDatabase.GetCookedAssetBytes(shaderAssetHandle);

            program = m_Factory.CreateGPUProgram();
            if (!program.IsValid)
            {
                throw new InvalidOperationException(
                    $"[OutdoorAtmospherePass] Failed to allocate shader program for {cookedStage.Stage.EntryPoint}.");
            }

            if (!m_Factory.AttachProgramByteCode(
                    program,
                    rhiStage,
                    shaderCode,
                    cookedStage.Stage.EntryPoint))
            {
                throw new InvalidOperationException(
                    $"[OutdoorAtmospherePass] Failed to attach shader bytecode for {cookedStage.Stage.EntryPoint}.");
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

        if (m_PipelineState.IsValid)
        {
            m_PipelineState.Release();
        }

        if (m_Factory.IsValid)
        {
            if (m_VertexProgram.IsValid)
            {
                m_Factory.ReleaseGPUProgram(m_VertexProgram);
            }

            if (m_FragmentProgram.IsValid)
            {
                m_Factory.ReleaseGPUProgram(m_FragmentProgram);
            }
        }

        if (m_VertexShaderAsset.IsValid)
        {
            m_AssetDatabase.Release(m_VertexShaderAsset);
        }

        if (m_FragmentShaderAsset.IsValid)
        {
            m_AssetDatabase.Release(m_FragmentShaderAsset);
        }

        m_PipelineState = default;
        m_PipelineCache = null;
        m_VertexProgram = RHIShaderProgramHandle.Invalid;
        m_FragmentProgram = RHIShaderProgramHandle.Invalid;
        m_VertexShaderAsset = CookedAssetHandle.Invalid;
        m_FragmentShaderAsset = CookedAssetHandle.Invalid;
        m_ShaderStamp = AssetDependencyStamp.Empty;
        m_ColorFormat = EFormat.FORMAT_UNDEFINED;
    }

    private void ThrowIfDisposed()
    {
        if (m_Disposed)
        {
            throw new ObjectDisposedException(nameof(OutdoorAtmospherePass));
        }
    }
}
