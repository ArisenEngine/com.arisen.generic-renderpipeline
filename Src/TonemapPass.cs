using System.Numerics;
using System.Runtime.InteropServices;
using Arisen.Native.RHI;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.RHI;
using ArisenEngine.Rendering.Resources;

namespace ArisenEngine.Rendering;

public sealed class TonemapPass : RenderPassNode, IDisposable
{
    private const ulong DynamicViewportScissorMask = 0x1UL | 0x2UL;
    private const string VertexStage = "Vertex";
    private const string FragmentStage = "Fragment";

    private readonly IAssetDatabase m_AssetDatabase;
    private readonly ShaderAsset m_Shader;
    private RHIFactory m_Factory;
    private RHIPipelineState m_PipelineState;
    private RHIPipelineHandle m_Pipeline = RHIPipelineHandle.Invalid;
    private RHIShaderProgramHandle m_VertexProgram = RHIShaderProgramHandle.Invalid;
    private RHIShaderProgramHandle m_FragmentProgram = RHIShaderProgramHandle.Invalid;
    private CookedAssetHandle m_VertexShaderAsset = CookedAssetHandle.Invalid;
    private CookedAssetHandle m_FragmentShaderAsset = CookedAssetHandle.Invalid;
    private AssetDependencyStamp m_ShaderStamp = AssetDependencyStamp.Empty;
    private EFormat m_OutputFormat = EFormat.FORMAT_UNDEFINED;
    private RHIImageHandle m_SceneColorImage = RHIImageHandle.Invalid;
    private uint m_SceneColorImageIndex = 0xFFFFFFFFu;
    private uint m_SceneColorSamplerIndex = 0xFFFFFFFFu;
    private float m_Exposure = 1.0f;
    private TonemapConstants m_Constants = TonemapConstants.Default;
    private bool m_Disposed;

    public TonemapPass(
        IAssetDatabase assetDatabase,
        string name = "TonemapPass") : base(name)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        m_Shader = GenericRenderPipelineShaderAssets.CreateTonemap();
    }

    public void SetSceneColor(
        RHIImageHandle image,
        uint bindlessImageIndex,
        uint bindlessSamplerIndex)
    {
        m_SceneColorImage = image;
        m_SceneColorImageIndex = bindlessImageIndex;
        m_SceneColorSamplerIndex = bindlessSamplerIndex;
    }

    public void SetExposure(float exposure)
    {
        m_Exposure = SceneEnvironment.NormalizeExposure(exposure);
    }

    public void Prepare(RenderContext context)
    {
        ThrowIfDisposed();

        var factory = context.Device.GetFactory();
        var outputFormat = factory.GetImageViewFormat(context.SwapChain.GetImageView(context.FrameIndex));
        m_Constants = TonemapConstants.From(
            m_SceneColorImageIndex,
            m_SceneColorSamplerIndex,
            m_Exposure,
            RenderOutputEncoding.RequiresExplicitSrgbEncoding(outputFormat));

        var shaderStamp = AssetDependencyTracker.GetShaderStamp(m_AssetDatabase, m_Shader);
        if (m_Pipeline.IsValid &&
            m_OutputFormat == outputFormat &&
            m_ShaderStamp == shaderStamp)
        {
            PlotDiagnostics(outputFormat);
            return;
        }

        ReleasePipelineResources();
        m_Factory = factory;
        m_OutputFormat = outputFormat;

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

            m_PipelineState = context.Device.PipelineCache.GetPipelineState();
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
            m_PipelineState.SetRenderingFormats(new[] { outputFormat }, EFormat.FORMAT_UNDEFINED);
            m_PipelineState.BuildDescriptorSetLayout();

            m_Pipeline = context.Device.PipelineCache.GetGraphicsPipeline(m_PipelineState);
            if (!m_Pipeline.IsValid)
            {
                throw new InvalidOperationException("[TonemapPass] Failed to create graphics pipeline.");
            }

            m_ShaderStamp = shaderStamp;
            Logger.Log(
                $"[TonemapPass] Prepared pipeline | OutputFormat: {outputFormat} | Pipeline: {m_Pipeline.Index}:{m_Pipeline.Generation}");
            PlotDiagnostics(outputFormat);
        }
        catch
        {
            ReleasePipelineResources();
            throw;
        }
    }

    protected override void Record(RenderContext context, RenderCommandList commandList)
    {
        if (!m_Pipeline.IsValid || !m_SceneColorImage.IsValid)
        {
            return;
        }

        var outputImageView = context.SwapChain.GetImageView(context.FrameIndex);
        commandList.BeginRendering(
            outputImageView,
            EImageLayout.IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
            EAttachmentLoadOp.ATTACHMENT_LOAD_OP_CLEAR,
            EAttachmentStoreOp.ATTACHMENT_STORE_OP_STORE,
            0.0f,
            0.0f,
            0.0f,
            1.0f,
            0,
            0,
            context.Width,
            context.Height);
        commandList.BindPipeline(m_Pipeline);
        commandList.PushConstants(
            m_Constants,
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
                    $"[TonemapPass] Failed to allocate shader program for {cookedStage.Stage.EntryPoint}.");
            }

            if (!m_Factory.AttachProgramByteCode(
                    program,
                    rhiStage,
                    shaderCode,
                    cookedStage.Stage.EntryPoint))
            {
                throw new InvalidOperationException(
                    $"[TonemapPass] Failed to attach shader bytecode for {cookedStage.Stage.EntryPoint}.");
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
        m_Pipeline = RHIPipelineHandle.Invalid;
        m_VertexProgram = RHIShaderProgramHandle.Invalid;
        m_FragmentProgram = RHIShaderProgramHandle.Invalid;
        m_VertexShaderAsset = CookedAssetHandle.Invalid;
        m_FragmentShaderAsset = CookedAssetHandle.Invalid;
        m_ShaderStamp = AssetDependencyStamp.Empty;
        m_OutputFormat = EFormat.FORMAT_UNDEFINED;
    }

    private void PlotDiagnostics(EFormat outputFormat)
    {
        Profiler.PlotValue("TonemapPass.Exposure", m_Exposure);
        Profiler.PlotValue("TonemapPass.OutputFormat", (double)(uint)outputFormat);
        Profiler.PlotValue(
            "TonemapPass.ExplicitSrgbEncode",
            RenderOutputEncoding.RequiresExplicitSrgbEncoding(outputFormat) ? 1 : 0);
    }

    private void ThrowIfDisposed()
    {
        if (m_Disposed)
        {
            throw new ObjectDisposedException(nameof(TonemapPass));
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct TonemapConstants
{
    public static TonemapConstants Default => new(
        Vector4.One,
        0xFFFFFFFFu,
        0xFFFFFFFFu);

    public readonly Vector4 ToneMapParams;
    public readonly uint SceneColorImageIndex;
    public readonly uint SceneColorSamplerIndex;
    public readonly uint Padding0;
    public readonly uint Padding1;

    private TonemapConstants(
        Vector4 toneMapParams,
        uint sceneColorImageIndex,
        uint sceneColorSamplerIndex)
    {
        ToneMapParams = toneMapParams;
        SceneColorImageIndex = sceneColorImageIndex;
        SceneColorSamplerIndex = sceneColorSamplerIndex;
        Padding0 = 0;
        Padding1 = 0;
    }

    public static TonemapConstants From(
        uint sceneColorImageIndex,
        uint sceneColorSamplerIndex,
        float exposure,
        bool encodeOutputToSrgb)
    {
        return new TonemapConstants(
            new Vector4(exposure, encodeOutputToSrgb ? 1.0f : 0.0f, 0.0f, 0.0f),
            sceneColorImageIndex,
            sceneColorSamplerIndex);
    }
}
