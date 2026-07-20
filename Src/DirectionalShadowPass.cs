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
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly ShaderAsset m_Shader;
    private RHIFactory m_Factory;
    private RHIPipelineState m_PipelineState;
    private RHIPipelineHandle m_Pipeline = RHIPipelineHandle.Invalid;
    private RHIShaderProgramHandle m_VertexProgram = RHIShaderProgramHandle.Invalid;
    private CookedAssetHandle m_VertexShaderAsset = CookedAssetHandle.Invalid;
    private AssetDependencyStamp m_ShaderStamp = AssetDependencyStamp.Empty;
    private RHIImageViewHandle m_DepthImageView = RHIImageViewHandle.Invalid;
    private EFormat m_TargetDepthFormat = EFormat.FORMAT_UNDEFINED;
    private uint m_TargetWidth;
    private uint m_TargetHeight;
    private EFormat m_PipelineDepthFormat = EFormat.FORMAT_UNDEFINED;
    private DirectionalShadowProjection m_Projection;
    private Matrix4x4 m_ViewProjection = Matrix4x4.Identity;
    private MeshDrawCommand[] m_PreparedDraws = Array.Empty<MeshDrawCommand>();
    private ShadowDepthDrawConstants[] m_DrawConstants = Array.Empty<ShadowDepthDrawConstants>();
    private int m_PreparedDrawCount;
    private bool m_Disposed;

    public Matrix4x4 ViewProjection => m_ViewProjection;
    public bool HasRenderableShadowMap =>
        HasValidDepthTarget &&
        m_Pipeline.IsValid &&
        m_PreparedDrawCount > 0;

    private bool HasValidDepthTarget =>
        m_DepthImageView.IsValid &&
        m_TargetDepthFormat != EFormat.FORMAT_UNDEFINED &&
        m_TargetWidth > 0 &&
        m_TargetHeight > 0;

    public DirectionalShadowPass(
        IAssetDatabase assetDatabase,
        string name = "DirectionalShadowPass") : base(name)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        m_Shader = new ShaderAsset(
            GenericRenderPipelineAssetRefs.DirectionalShadowShader.Ref.Guid,
            "GenericRP/DirectionalShadow",
            new ShaderStageAsset[]
            {
                new(VertexStage, EProgramStage.Vertex, "VSMain")
            },
            ShaderVariantKey.VulkanDebug);
    }

    public void SetDepthTarget(
        RHIImageViewHandle imageView,
        EFormat depthFormat,
        uint width,
        uint height)
    {
        if (!imageView.IsValid)
        {
            throw new ArgumentException("[DirectionalShadowPass] Depth image view is invalid.", nameof(imageView));
        }

        if (depthFormat == EFormat.FORMAT_UNDEFINED)
        {
            throw new ArgumentOutOfRangeException(nameof(depthFormat));
        }

        if (width == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "[DirectionalShadowPass] Depth target width must be non-zero.");
        }

        if (height == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "[DirectionalShadowPass] Depth target height must be non-zero.");
        }

        m_DepthImageView = imageView;
        m_TargetDepthFormat = depthFormat;
        m_TargetWidth = width;
        m_TargetHeight = height;
    }

    public void SetProjection(in DirectionalShadowProjection projection)
    {
        m_Projection = projection;
        m_ViewProjection = projection.ViewProjection;
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
        EnsureDrawConstants();
        var shaderStamp = AssetDependencyTracker.GetShaderStamp(m_AssetDatabase, m_Shader);
        if (m_Pipeline.IsValid &&
            m_PipelineDepthFormat == depthFormat &&
            m_ShaderStamp == shaderStamp)
        {
            PlotDiagnostics();
            return;
        }

        ReleasePipelineResources();
        m_Factory = factory;
        m_PipelineDepthFormat = depthFormat;

        try
        {
            m_VertexProgram = CompileProgram(
                EShaderStage.SHADER_STAGE_VERTEX_BIT,
                VertexStage,
                out m_VertexShaderAsset);

            m_PipelineState = context.Device.PipelineCache.GetPipelineState();
            m_PipelineState.AddProgram(m_VertexProgram);
            m_PipelineState.SetBindPoint(EPipelineBindPoint.PIPELINE_BIND_POINT_GRAPHICS);
            m_PipelineState.SetInputAssemblyState(EPrimitiveTopology.PRIMITIVE_TOPOLOGY_TRIANGLE_LIST);
            m_PipelineState.ClearVertexInputDescriptions();
            m_PipelineState.AddVertexBindingDescription(
                0,
                MeshAssetCooker.StaticMeshVertexStride,
                EVertexInputRate.VERTEX_INPUT_RATE_VERTEX);
            m_PipelineState.AddVertexInputAttributeDescription(0, 0, EFormat.FORMAT_R32G32B32_SFLOAT, 0);
            m_PipelineState.SetRasterizationState(
                EPolygonMode.EPOLYGON_MODE_FILL,
                ECullModeFlagBits.CULL_MODE_BACK_BIT,
                EFrontFace.FRONT_FACE_COUNTER_CLOCKWISE);
            m_PipelineState.SetColorBlendState(false);
            m_PipelineState.SetDepthStencilState(true, true, ECompareOp.COMPARE_OP_LESS_OR_EQUAL);
            m_PipelineState.SetDynamicStateMask(DynamicViewportScissorMask);
            m_PipelineState.SetRenderingFormats(Array.Empty<EFormat>(), depthFormat);
            m_PipelineState.BuildDescriptorSetLayout();

            m_Pipeline = context.Device.PipelineCache.GetGraphicsPipeline(m_PipelineState);
            if (!m_Pipeline.IsValid)
            {
                throw new InvalidOperationException("[DirectionalShadowPass] Failed to create graphics pipeline.");
            }

            m_ShaderStamp = shaderStamp;
            Logger.Log(
                $"[DirectionalShadowPass] Prepared pipeline | DepthFormat: {depthFormat} | Pipeline: {m_Pipeline.Index}:{m_Pipeline.Generation}");
            PlotDiagnostics();
        }
        catch
        {
            ReleasePipelineResources();
            throw;
        }
    }

    protected override void Record(RenderContext context, RenderCommandList commandList)
    {
        if (!m_Pipeline.IsValid ||
            !HasValidDepthTarget)
        {
            return;
        }

        commandList.BeginRenderingDepthOnly(
            m_DepthImageView,
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
        commandList.SetViewport(0, 0, m_TargetWidth, m_TargetHeight);
        commandList.SetScissor(0, 0, m_TargetWidth, m_TargetHeight);

        int drawCount = m_PreparedDrawCount;
        for (int i = 0; i < drawCount; i++)
        {
            ref readonly var draw = ref m_PreparedDraws[i];
            if (!IsDrawable(draw))
            {
                continue;
            }

            commandList.PushConstants(
                m_DrawConstants[i],
                EShaderStage.SHADER_STAGE_VERTEX_BIT);
            commandList.BindVertexBuffers(draw.VertexBuffer);
            commandList.BindIndexBuffer(draw.IndexBuffer, 0, draw.IndexType);
            commandList.DrawIndexed(draw.IndexCount, firstIndex: draw.FirstIndex, vertexOffset: draw.VertexOffset);
        }

        commandList.EndRendering();
    }

    public void Dispose()
    {
        if (m_Disposed)
        {
            return;
        }

        ReleasePipelineResources();
        m_DepthImageView = RHIImageViewHandle.Invalid;
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

        int drawCount = m_PreparedDrawCount;
        for (int i = 0; i < drawCount; i++)
        {
            m_DrawConstants[i] = ShadowDepthDrawConstants.From(
                m_PreparedDraws[i].LocalToWorld,
                m_ViewProjection);
        }
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
        if (m_PipelineState.IsValid)
        {
            m_PipelineState.Release();
        }

        if (m_Factory.IsValid && m_VertexProgram.IsValid)
        {
            m_Factory.ReleaseGPUProgram(m_VertexProgram);
        }

        if (m_VertexShaderAsset.IsValid)
        {
            m_AssetDatabase.Release(m_VertexShaderAsset);
        }

        m_PipelineState = default;
        m_Pipeline = RHIPipelineHandle.Invalid;
        m_VertexProgram = RHIShaderProgramHandle.Invalid;
        m_VertexShaderAsset = CookedAssetHandle.Invalid;
        m_ShaderStamp = AssetDependencyStamp.Empty;
        m_PipelineDepthFormat = EFormat.FORMAT_UNDEFINED;
    }

    private void PlotDiagnostics()
    {
        Profiler.PlotValue("DirectionalShadowPass.DrawCount", m_PreparedDrawCount);
        Profiler.PlotValue("DirectionalShadowPass.ShadowMapSize", m_TargetWidth);
        Profiler.PlotValue("DirectionalShadowPass.DepthFormat", (double)(uint)m_TargetDepthFormat);
        Profiler.PlotValue("DirectionalShadowPass.Enabled", HasRenderableShadowMap ? 1 : 0);
        Profiler.PlotValue("DirectionalShadowPass.SceneFitted", m_Projection.IsSceneFitted ? 1 : 0);
        Profiler.PlotValue("DirectionalShadowPass.Diameter", m_Projection.Diameter);
        Profiler.PlotValue("DirectionalShadowPass.Depth", m_Projection.Depth);
        Profiler.PlotValue("DirectionalShadowPass.WorldUnitsPerTexel", m_Projection.WorldUnitsPerTexel);
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

    private ShadowDepthDrawConstants(
        Vector4 modelViewProjectionColumn0,
        Vector4 modelViewProjectionColumn1,
        Vector4 modelViewProjectionColumn2,
        Vector4 modelViewProjectionColumn3)
    {
        ModelViewProjectionColumn0 = modelViewProjectionColumn0;
        ModelViewProjectionColumn1 = modelViewProjectionColumn1;
        ModelViewProjectionColumn2 = modelViewProjectionColumn2;
        ModelViewProjectionColumn3 = modelViewProjectionColumn3;
    }

    public static ShadowDepthDrawConstants From(Matrix4x4 localToWorld, Matrix4x4 viewProjection)
    {
        var modelViewProjection = localToWorld * viewProjection;
        return new ShadowDepthDrawConstants(
            new Vector4(modelViewProjection.M11, modelViewProjection.M21, modelViewProjection.M31, modelViewProjection.M41),
            new Vector4(modelViewProjection.M12, modelViewProjection.M22, modelViewProjection.M32, modelViewProjection.M42),
            new Vector4(modelViewProjection.M13, modelViewProjection.M23, modelViewProjection.M33, modelViewProjection.M43),
            new Vector4(modelViewProjection.M14, modelViewProjection.M24, modelViewProjection.M34, modelViewProjection.M44));
    }
}
