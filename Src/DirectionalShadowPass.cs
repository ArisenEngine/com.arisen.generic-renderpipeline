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
    private const uint InvalidBindlessIndex = 0xFFFFFFFFu;
    private const float ShowcaseShadowDiameter = 10.0f;
    private const float ShowcaseShadowCenterY = 0.75f;
    private const float ShowcaseShadowEyeDistance = 11.0f;
    private const float ShowcaseShadowDepth = 24.0f;

    private readonly IAssetDatabase m_AssetDatabase;
    private readonly ShaderAsset m_Shader;
    private RHIFactory m_Factory;
    private RHIPipelineState m_PipelineState;
    private RHIPipelineHandle m_Pipeline = RHIPipelineHandle.Invalid;
    private RHIShaderProgramHandle m_VertexProgram = RHIShaderProgramHandle.Invalid;
    private CookedAssetHandle m_VertexShaderAsset = CookedAssetHandle.Invalid;
    private AssetDependencyStamp m_ShaderStamp = AssetDependencyStamp.Empty;
    private DirectionalShadowTarget? m_Target;
    private EFormat m_DepthFormat = EFormat.FORMAT_UNDEFINED;
    private DirectionalLight m_DirectionalLight = DirectionalLight.Default;
    private Matrix4x4 m_ViewProjection = Matrix4x4.Identity;
    private MeshDrawCommand[] m_PreparedDraws = Array.Empty<MeshDrawCommand>();
    private ShadowDepthDrawConstants[] m_DrawConstants = Array.Empty<ShadowDepthDrawConstants>();
    private int m_PreparedDrawCount;
    private bool m_Disposed;

    public Matrix4x4 ViewProjection => m_ViewProjection;
    public bool HasRenderableShadowMap => m_Target is { IsValid: true } && m_PreparedDrawCount > 0;

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

    public void SetTarget(DirectionalShadowTarget target)
    {
        m_Target = target ?? throw new ArgumentNullException(nameof(target));
    }

    public void SetDirectionalLight(DirectionalLight light)
    {
        m_DirectionalLight = light.IsValid ? light : DirectionalLight.Default;
        m_ViewProjection = CreateShowcaseShadowViewProjection(m_DirectionalLight);
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

        var target = m_Target;
        if (target is not { IsValid: true })
        {
            return;
        }

        var factory = context.Device.GetFactory();
        var depthFormat = target.Format;
        EnsureDrawConstants();
        var shaderStamp = AssetDependencyTracker.GetShaderStamp(m_AssetDatabase, m_Shader);
        if (m_Pipeline.IsValid &&
            m_DepthFormat == depthFormat &&
            m_ShaderStamp == shaderStamp)
        {
            PlotDiagnostics(target);
            return;
        }

        ReleasePipelineResources();
        m_Factory = factory;
        m_DepthFormat = depthFormat;

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
            PlotDiagnostics(target);
        }
        catch
        {
            ReleasePipelineResources();
            throw;
        }
    }

    protected override void Record(RenderContext context, RenderCommandList commandList)
    {
        var target = m_Target;
        if (!m_Pipeline.IsValid ||
            target is not { IsValid: true } ||
            m_PreparedDrawCount <= 0)
        {
            return;
        }

        commandList.TransitionImageLayout(
            target.Image,
            target.ExpectedLayout,
            EImageLayout.IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL);

        commandList.BeginRenderingDepthOnly(
            target.ImageView,
            EImageLayout.IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL,
            EAttachmentLoadOp.ATTACHMENT_LOAD_OP_CLEAR,
            EAttachmentStoreOp.ATTACHMENT_STORE_OP_STORE,
            1.0f,
            0,
            0,
            0,
            target.Size,
            target.Size);
        commandList.BindPipeline(m_Pipeline);
        commandList.SetViewport(0, 0, target.Size, target.Size);
        commandList.SetScissor(0, 0, target.Size, target.Size);

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
        commandList.TransitionImageLayout(
            target.Image,
            EImageLayout.IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL,
            EImageLayout.IMAGE_LAYOUT_DEPTH_STENCIL_READ_ONLY_OPTIMAL);
        target.SetExpectedLayout(EImageLayout.IMAGE_LAYOUT_DEPTH_STENCIL_READ_ONLY_OPTIMAL);
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
        m_DepthFormat = EFormat.FORMAT_UNDEFINED;
    }

    private void PlotDiagnostics(DirectionalShadowTarget target)
    {
        Profiler.PlotValue("DirectionalShadowPass.DrawCount", m_PreparedDrawCount);
        Profiler.PlotValue("DirectionalShadowPass.ShadowMapSize", target.Size);
        Profiler.PlotValue("DirectionalShadowPass.DepthFormat", (double)(uint)target.Format);
        Profiler.PlotValue("DirectionalShadowPass.Enabled", HasRenderableShadowMap ? 1 : 0);
    }

    private static Matrix4x4 CreateShowcaseShadowViewProjection(DirectionalLight light)
    {
        var lightDirection = light.IsValid
            ? Vector3.Normalize(light.Direction)
            : Vector3.Normalize(DirectionalLight.Default.Direction);
        var center = new Vector3(0.0f, ShowcaseShadowCenterY, 0.0f);
        var eye = center + lightDirection * ShowcaseShadowEyeDistance;
        var up = MathF.Abs(Vector3.Dot(lightDirection, Vector3.UnitY)) > 0.92f
            ? Vector3.UnitZ
            : Vector3.UnitY;
        var view = Matrix4x4.CreateLookAt(eye, center, up);
        var projection = Matrix4x4.CreateOrthographic(
            ShowcaseShadowDiameter,
            ShowcaseShadowDiameter,
            0.1f,
            ShowcaseShadowDepth);
        return view * projection;
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
