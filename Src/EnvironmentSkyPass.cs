using System.Numerics;
using System.Runtime.InteropServices;
using Arisen.Native.RHI;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.RHI;
using ArisenEngine.Rendering.Resources;

namespace ArisenEngine.Rendering;

public sealed class EnvironmentSkyPass : RenderPassNode, IDisposable
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
    private Vector3 m_ClearColor = SceneEnvironment.Default.GroundColor;
    private bool m_Disposed;

    public EnvironmentSkyPass(
        IAssetDatabase assetDatabase,
        string name = "EnvironmentSkyPass") : base(name)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        m_Shader = GenericRenderPipelineShaderAssets.CreateEnvironmentSky();
    }

    internal void SetFrameData(
        in EnvironmentFrameData frameData,
        in SceneEnvironment environment)
    {
        m_FrameData = frameData;
        m_ClearColor = environment.IsValid
            ? environment.GroundColor
            : SceneEnvironment.Default.GroundColor;
    }

    public void SetColorTarget(
        RHIImageViewHandle imageView,
        EFormat format)
    {
        m_TargetImageView = imageView;
        m_TargetColorFormat = format;
    }

    public void Prepare(RenderContext context)
    {
        ThrowIfDisposed();

        var factory = context.Device.GetFactory();
        var colorFormat = m_TargetColorFormat != EFormat.FORMAT_UNDEFINED
            ? m_TargetColorFormat
            : factory.GetImageViewFormat(context.SwapChain.GetImageView(context.FrameIndex));
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
            m_PipelineState.SetColorBlendState(false);
            m_PipelineState.SetDepthStencilState(false, false, ECompareOp.COMPARE_OP_ALWAYS);
            m_PipelineState.SetDynamicStateMask(DynamicViewportScissorMask);
            m_PipelineState.SetRenderingFormats(new[] { colorFormat }, EFormat.FORMAT_UNDEFINED);
            m_PipelineState.BuildDescriptorSetLayout();

            m_Pipeline = pipelineCache.GetGraphicsPipeline(m_PipelineState);
            if (!m_Pipeline.IsValid)
            {
                throw new InvalidOperationException("[EnvironmentSkyPass] Failed to create graphics pipeline.");
            }

            m_ShaderStamp = shaderStamp;
            Logger.Log(
                $"[EnvironmentSkyPass] Prepared pipeline | Format: {colorFormat} | Pipeline: {m_Pipeline.Index}:{m_Pipeline.Generation}");
        }
        catch
        {
            ReleasePipelineResources();
            throw;
        }
    }

    protected override void Record(RenderContext context, RenderCommandList commandList)
    {
        if (!m_Pipeline.IsValid || !m_FrameData.IsValid)
        {
            return;
        }

        var colorImageView = m_TargetImageView.IsValid
            ? m_TargetImageView
            : context.SwapChain.GetImageView(context.FrameIndex);

        commandList.BeginRendering(
            colorImageView,
            EImageLayout.IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
            EAttachmentLoadOp.ATTACHMENT_LOAD_OP_CLEAR,
            EAttachmentStoreOp.ATTACHMENT_STORE_OP_STORE,
            m_ClearColor.X,
            m_ClearColor.Y,
            m_ClearColor.Z,
            1.0f,
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
                    $"[EnvironmentSkyPass] Failed to allocate shader program for {cookedStage.Stage.EntryPoint}.");
            }

            if (!m_Factory.AttachProgramByteCode(
                    program,
                    rhiStage,
                    shaderCode,
                    cookedStage.Stage.EntryPoint))
            {
                throw new InvalidOperationException(
                    $"[EnvironmentSkyPass] Failed to attach shader bytecode for {cookedStage.Stage.EntryPoint}.");
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
            throw new ObjectDisposedException(nameof(EnvironmentSkyPass));
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct EnvironmentFramePushConstants
{
    private readonly uint m_EnvironmentFrameBufferIndex;
    private readonly uint m_Padding0;
    private readonly uint m_Padding1;
    private readonly uint m_Padding2;

    private EnvironmentFramePushConstants(uint environmentFrameBufferIndex)
    {
        m_EnvironmentFrameBufferIndex = environmentFrameBufferIndex;
        m_Padding0 = 0;
        m_Padding1 = 0;
        m_Padding2 = 0;
    }

    public static EnvironmentFramePushConstants From(in EnvironmentFrameData frameData) =>
        new(frameData.ConstantsBufferBindlessIndex);
}
