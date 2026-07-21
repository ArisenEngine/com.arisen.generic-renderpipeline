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
    private const uint InvalidBindlessIndex = 0xFFFFFFFFu;
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
    private RHIImageViewHandle m_TargetImageView = RHIImageViewHandle.Invalid;
    private EFormat m_TargetColorFormat = EFormat.FORMAT_UNDEFINED;
    private EFormat m_ColorFormat = EFormat.FORMAT_UNDEFINED;
    private SceneEnvironment m_Environment = SceneEnvironment.Default;
    private EnvironmentSkyConstants m_Constants = EnvironmentSkyConstants.Default;
    private uint m_EnvironmentImageIndex = InvalidBindlessIndex;
    private uint m_EnvironmentSamplerIndex = InvalidBindlessIndex;
    private float m_EnvironmentRotationRadians;
    private float m_EnvironmentTextureIntensity;
    private bool m_Disposed;

    public EnvironmentSkyPass(
        IAssetDatabase assetDatabase,
        string name = "EnvironmentSkyPass") : base(name)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        m_Shader = GenericRenderPipelineShaderAssets.CreateEnvironmentSky();
    }

    public void SetEnvironment(SceneEnvironment environment)
    {
        m_Environment = environment.IsValid ? environment : SceneEnvironment.Default;
    }

    public void SetEnvironmentTexture(RHIEnvironmentTextureResource? environmentTexture)
    {
        if (environmentTexture is { IsValid: true })
        {
            m_EnvironmentImageIndex = environmentTexture.BindlessImageIndex;
            m_EnvironmentSamplerIndex = environmentTexture.BindlessSamplerIndex;
            m_EnvironmentRotationRadians = environmentTexture.RotationRadians;
            m_EnvironmentTextureIntensity = environmentTexture.Intensity;
            return;
        }

        m_EnvironmentImageIndex = InvalidBindlessIndex;
        m_EnvironmentSamplerIndex = InvalidBindlessIndex;
        m_EnvironmentRotationRadians = 0.0f;
        m_EnvironmentTextureIntensity = 0.0f;
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
        m_Constants = EnvironmentSkyConstants.From(
            m_Environment,
            m_EnvironmentImageIndex,
            m_EnvironmentSamplerIndex,
            m_EnvironmentRotationRadians,
            m_EnvironmentTextureIntensity,
            context);
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
            m_PipelineState.SetRenderingFormats(new[] { colorFormat }, EFormat.FORMAT_UNDEFINED);
            m_PipelineState.BuildDescriptorSetLayout();

            m_Pipeline = context.Device.PipelineCache.GetGraphicsPipeline(m_PipelineState);
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
        if (!m_Pipeline.IsValid)
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
            m_Constants.GroundColor.X,
            m_Constants.GroundColor.Y,
            m_Constants.GroundColor.Z,
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
internal readonly struct EnvironmentSkyConstants
{
    private const uint InvalidBindlessIndex = 0xFFFFFFFFu;

    public static EnvironmentSkyConstants Default => new(
        new Vector4(SceneEnvironment.Default.SkyColor, SceneEnvironment.Default.SkyIntensity),
        new Vector4(SceneEnvironment.Default.HorizonColor, 0.0f),
        new Vector4(SceneEnvironment.Default.GroundColor, 0.0f),
        new Vector4(Vector3.UnitX, MathF.Tan(MathF.PI / 6.0f)),
        new Vector4(Vector3.UnitY, 16.0f / 9.0f),
        new Vector4(Vector3.UnitZ, 0.0f),
        InvalidBindlessIndex,
        InvalidBindlessIndex,
        0.0f,
        0.0f);

    public readonly Vector4 SkyColorIntensity;
    public readonly Vector4 HorizonColor;
    public readonly Vector4 GroundColor;
    public readonly Vector4 CameraRightTanHalfFov;
    public readonly Vector4 CameraUpAspect;
    public readonly Vector4 CameraForward;
    public readonly uint EnvironmentImageIndex;
    public readonly uint EnvironmentSamplerIndex;
    public readonly float EnvironmentRotationRadians;
    public readonly float EnvironmentTextureIntensity;

    private EnvironmentSkyConstants(
        Vector4 skyColorIntensity,
        Vector4 horizonColor,
        Vector4 groundColor,
        Vector4 cameraRightTanHalfFov,
        Vector4 cameraUpAspect,
        Vector4 cameraForward,
        uint environmentImageIndex,
        uint environmentSamplerIndex,
        float environmentRotationRadians,
        float environmentTextureIntensity)
    {
        SkyColorIntensity = skyColorIntensity;
        HorizonColor = horizonColor;
        GroundColor = groundColor;
        CameraRightTanHalfFov = cameraRightTanHalfFov;
        CameraUpAspect = cameraUpAspect;
        CameraForward = cameraForward;
        EnvironmentImageIndex = environmentImageIndex;
        EnvironmentSamplerIndex = environmentSamplerIndex;
        EnvironmentRotationRadians = environmentRotationRadians;
        EnvironmentTextureIntensity = environmentTextureIntensity;
    }

    public static EnvironmentSkyConstants From(
        SceneEnvironment environment,
        uint environmentImageIndex,
        uint environmentSamplerIndex,
        float environmentRotationRadians,
        float environmentTextureIntensity,
        RenderContext context)
    {
        var right = Vector3.UnitX;
        var up = Vector3.UnitY;
        var forward = Vector3.UnitZ;
        var verticalFovDegrees = 60.0f;
        var aspectRatio = context.Height > 0
            ? context.Width / (float)context.Height
            : 1.0f;

        if (context.CameraCount > 0)
        {
            ref readonly var camera = ref context.Cameras[0];
            var rotation = Matrix4x4.CreateFromYawPitchRoll(
                camera.Rotation.Y * (MathF.PI / 180.0f),
                camera.Rotation.X * (MathF.PI / 180.0f),
                camera.Rotation.Z * (MathF.PI / 180.0f));
            right = Vector3.Normalize(Vector3.Transform(Vector3.UnitX, rotation));
            up = Vector3.Normalize(Vector3.Transform(Vector3.UnitY, rotation));
            forward = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, rotation));
            verticalFovDegrees = camera.FieldOfView > 0.0f
                ? camera.FieldOfView
                : verticalFovDegrees;
            aspectRatio = camera.AspectRatio > 0.0f
                ? camera.AspectRatio
                : aspectRatio;
        }

        var tanHalfFov = MathF.Tan(
            Math.Clamp(verticalFovDegrees, 1.0f, 179.0f) *
            (MathF.PI / 360.0f));
        return new EnvironmentSkyConstants(
            new Vector4(environment.SkyColor, environment.SkyIntensity),
            new Vector4(environment.HorizonColor, 0.0f),
            new Vector4(environment.GroundColor, 0.0f),
            new Vector4(right, tanHalfFov),
            new Vector4(up, aspectRatio),
            new Vector4(forward, 0.0f),
            environmentImageIndex,
            environmentSamplerIndex,
            environmentRotationRadians,
            MathF.Max(0.0f, environmentTextureIntensity));
    }
}
