using Arisen.Native.RHI;
using ArisenEngine.Core.RHI;
using ArisenEngine.ShaderLab;

namespace ArisenEngine.Rendering;

/// <summary>
/// Temporary smoke/sample pass that proves shader-backed RenderGraph draw submission.
/// Production scene rendering should replace this with real scene passes.
/// </summary>
public sealed class SmokeTrianglePass : RenderPassNode, IDisposable
{
    private const ulong DynamicViewportScissorMask = 0x1UL | 0x2UL;
    private const string VertexEntry = "VSMain";
    private const string FragmentEntry = "PSMain";
    private const string ShaderSource = """
struct VSOutput
{
    float4 Position : SV_Position;
    float3 Color : COLOR0;
};

VSOutput VSMain(uint vertexId : SV_VertexID)
{
    float2 positions[3] =
    {
        float2(0.0, -0.55),
        float2(0.55, 0.55),
        float2(-0.55, 0.55)
    };

    float3 colors[3] =
    {
        float3(1.0, 0.28, 0.18),
        float3(0.20, 0.85, 0.38),
        float3(0.22, 0.46, 1.0)
    };

    VSOutput output;
    output.Position = float4(positions[vertexId], 0.0, 1.0);
    output.Color = colors[vertexId];
    return output;
}

float4 PSMain(VSOutput input) : SV_Target0
{
    return float4(input.Color, 1.0);
}
""";

    private RHIFactory m_Factory;
    private RHIShaderProgramHandle m_VertexProgram = RHIShaderProgramHandle.Invalid;
    private RHIShaderProgramHandle m_FragmentProgram = RHIShaderProgramHandle.Invalid;
    private RHIPipelineState m_PipelineState;
    private RHIPipelineHandle m_Pipeline = RHIPipelineHandle.Invalid;
    private EFormat m_ColorFormat = EFormat.FORMAT_UNDEFINED;
    private bool m_Disposed;

    public SmokeTrianglePass(string name = "SmokeTrianglePass") : base(name)
    {
    }

    protected override void Record(RenderContext context, RenderCommandList commandList)
    {
        var colorImageView = context.SwapChain.GetImageView(context.FrameIndex);

        if (context.FrameIndex % 60 == 0)
        {
            ArisenEngine.Core.Diagnostics.Logger.Log(
                $"[SmokeTrianglePass] Record | Surface: 0x{context.SurfaceId:X} | Size: {context.Width}x{context.Height} | Pipeline: {m_Pipeline.Index}:{m_Pipeline.Generation}");
        }

        commandList.BeginRendering(
            colorImageView,
            EImageLayout.IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
            EAttachmentLoadOp.ATTACHMENT_LOAD_OP_LOAD,
            EAttachmentStoreOp.ATTACHMENT_STORE_OP_STORE,
            0, 0, 0, 0,
            0, 0, context.Width, context.Height);

        commandList.BindPipeline(m_Pipeline);
        commandList.SetViewport(0, 0, context.Width, context.Height);
        commandList.SetScissor(0, 0, context.Width, context.Height);
        commandList.Draw(3);

        commandList.EndRendering();
    }

    public void Prepare(RenderContext context)
    {
        var factory = context.Device.GetFactory();
        var colorFormat = factory.GetImageViewFormat(context.SwapChain.GetImageView(context.FrameIndex));

        if (m_Pipeline.IsValid && m_ColorFormat == colorFormat)
        {
            return;
        }

        ReleasePipelineResources();

        m_Factory = factory;
        m_ColorFormat = colorFormat;

        m_VertexProgram = CompileProgram(factory, EProgramStage.Vertex, EShaderStage.SHADER_STAGE_VERTEX_BIT, VertexEntry);
        m_FragmentProgram = CompileProgram(factory, EProgramStage.Fragment, EShaderStage.SHADER_STAGE_FRAGMENT_BIT, FragmentEntry);

        var pipelineCache = context.Device.PipelineCache;
        m_PipelineState = pipelineCache.GetPipelineState();
        m_PipelineState.AddProgram(m_VertexProgram);
        m_PipelineState.AddProgram(m_FragmentProgram);
        m_PipelineState.SetBindPoint(EPipelineBindPoint.PIPELINE_BIND_POINT_GRAPHICS);
        m_PipelineState.SetInputAssemblyState(EPrimitiveTopology.PRIMITIVE_TOPOLOGY_TRIANGLE_LIST);
        m_PipelineState.SetRasterizationState(
            EPolygonMode.EPOLYGON_MODE_FILL,
            ECullModeFlagBits.CULL_MODE_NONE,
            EFrontFace.FRONT_FACE_COUNTER_CLOCKWISE);
        m_PipelineState.SetColorBlendState(false);
        m_PipelineState.SetDynamicStateMask(DynamicViewportScissorMask);
        m_PipelineState.SetRenderingFormats(new[] { colorFormat }, EFormat.FORMAT_UNDEFINED);
        m_PipelineState.BuildDescriptorSetLayout();

        m_Pipeline = pipelineCache.GetGraphicsPipeline(m_PipelineState);
        if (!m_Pipeline.IsValid)
        {
            throw new InvalidOperationException("[SmokeTrianglePass] Failed to create graphics pipeline.");
        }

        ArisenEngine.Core.Diagnostics.Logger.Log($"[SmokeTrianglePass] Created pipeline | Format: {colorFormat} | Pipeline: {m_Pipeline.Index}:{m_Pipeline.Generation}");
    }

    private static RHIShaderProgramHandle CompileProgram(
        RHIFactory factory,
        EProgramStage compileStage,
        EShaderStage rhiStage,
        string entryPoint)
    {
        var shaderPath = WriteTempShaderSource();
        var result = ShaderCompiler.Compile(
            shaderPath,
            compileStage,
            new ShaderCompiler.CompileOptions
            {
                Entry = entryPoint,
                ShaderModel = "6_4",
                Target = "-spirv",
                TargetEnv = "vulkan1.3",
                OptimizeLevel = "0"
            });

        try
        {
            if (!result.Success || result.Code.Length == 0)
            {
                throw new InvalidOperationException($"[SmokeTrianglePass] Failed to compile {entryPoint}. {result.Message}");
            }

            var program = factory.CreateGPUProgram();
            if (!program.IsValid)
            {
                throw new InvalidOperationException($"[SmokeTrianglePass] Failed to allocate shader program for {entryPoint}.");
            }

            if (!factory.AttachProgramByteCode(program, rhiStage, result.Code, entryPoint))
            {
                factory.ReleaseGPUProgram(program);
                throw new InvalidOperationException($"[SmokeTrianglePass] Failed to attach shader bytecode for {entryPoint}.");
            }

            return program;
        }
        finally
        {
            TryDelete(shaderPath);
            TryDelete(result.OutputPath);
        }
    }

    private static string WriteTempShaderSource()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ArisenSmokeTrianglePass_{Guid.NewGuid():N}.hlsl");
        File.WriteAllText(path, ShaderSource);
        return path;
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private void ReleasePipelineResources()
    {
        if (m_PipelineState.IsValid)
        {
            m_PipelineState.Release();
            m_PipelineState = default;
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

        m_VertexProgram = RHIShaderProgramHandle.Invalid;
        m_FragmentProgram = RHIShaderProgramHandle.Invalid;
        m_Pipeline = RHIPipelineHandle.Invalid;
        m_ColorFormat = EFormat.FORMAT_UNDEFINED;
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
}
