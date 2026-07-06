using Arisen.Native.RHI;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.RHI;
using ArisenEngine.Rendering.Resources;
using System.Runtime.InteropServices;

namespace ArisenEngine.Rendering;

/// <summary>
/// Temporary smoke/sample pass that proves shader-backed RenderGraph draw submission.
/// Production scene rendering should replace this with real scene passes.
/// </summary>
public sealed class SmokeTrianglePass : RenderPassNode, IDisposable
{
    private const ulong DynamicViewportScissorMask = 0x1UL | 0x2UL;
    private const uint InvalidBindlessIndex = 0xFFFFFFFFu;
    private const string VertexStage = "Vertex";
    private const string FragmentStage = "Fragment";
    private static readonly Guid SmokeTriangleShaderGuid = Guid.Parse("98433827-04d5-4d19-8712-8d21596ed9ad");
    private static readonly ShaderAsset SmokeTriangleShader = new(
        SmokeTriangleShaderGuid,
        "GenericRP/SmokeTriangle",
        new[]
        {
            new ShaderStageAsset(VertexStage, EProgramStage.Vertex, "VSMain"),
            new ShaderStageAsset(FragmentStage, EProgramStage.Fragment, "PSMain")
        },
        ShaderVariantKey.VulkanDebug);

    private readonly IAssetDatabase m_AssetDatabase;
    private RHIFactory m_Factory;
    private RHIShaderProgramHandle m_VertexProgram = RHIShaderProgramHandle.Invalid;
    private RHIShaderProgramHandle m_FragmentProgram = RHIShaderProgramHandle.Invalid;
    private CookedAssetHandle m_VertexShaderAsset = CookedAssetHandle.Invalid;
    private CookedAssetHandle m_FragmentShaderAsset = CookedAssetHandle.Invalid;
    private SmokeTextureConstants m_TextureConstants;
    private RHIBufferHandle m_VertexBuffer = RHIBufferHandle.Invalid;
    private RHIBufferHandle m_IndexBuffer = RHIBufferHandle.Invalid;
    private EIndexType m_IndexType = EIndexType.INDEX_TYPE_UINT32;
    private uint m_IndexCount;
    private RHIPipelineState m_PipelineState;
    private RHIPipelineHandle m_Pipeline = RHIPipelineHandle.Invalid;
    private EFormat m_ColorFormat = EFormat.FORMAT_UNDEFINED;
    private bool m_Disposed;

    public SmokeTrianglePass(IAssetDatabase assetDatabase, string name = "SmokeTrianglePass") : base(name)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
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
        commandList.PushConstants(
            m_TextureConstants,
            EShaderStage.SHADER_STAGE_FRAGMENT_BIT);
        commandList.SetViewport(0, 0, context.Width, context.Height);
        commandList.SetScissor(0, 0, context.Width, context.Height);
        commandList.BindVertexBuffers(m_VertexBuffer);
        commandList.BindIndexBuffer(m_IndexBuffer, 0, m_IndexType);
        commandList.DrawIndexed(m_IndexCount);

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

        try
        {
            m_VertexProgram = CompileProgram(
                factory,
                EShaderStage.SHADER_STAGE_VERTEX_BIT,
                SmokeTriangleShader,
                VertexStage,
                m_AssetDatabase,
                out m_VertexShaderAsset);
            m_FragmentProgram = CompileProgram(
                factory,
                EShaderStage.SHADER_STAGE_FRAGMENT_BIT,
                SmokeTriangleShader,
                FragmentStage,
                m_AssetDatabase,
                out m_FragmentShaderAsset);

            var pipelineCache = context.Device.PipelineCache;
            m_PipelineState = pipelineCache.GetPipelineState();
            m_PipelineState.AddProgram(m_VertexProgram);
            m_PipelineState.AddProgram(m_FragmentProgram);
            m_PipelineState.SetBindPoint(EPipelineBindPoint.PIPELINE_BIND_POINT_GRAPHICS);
            m_PipelineState.SetInputAssemblyState(EPrimitiveTopology.PRIMITIVE_TOPOLOGY_TRIANGLE_LIST);
            m_PipelineState.ClearVertexInputDescriptions();
            m_PipelineState.AddVertexBindingDescription(
                0,
                MeshAssetCooker.StaticMeshVertexStride,
                EVertexInputRate.VERTEX_INPUT_RATE_VERTEX);
            m_PipelineState.AddVertexInputAttributeDescription(0, 0, EFormat.FORMAT_R32G32B32_SFLOAT, 0);
            m_PipelineState.AddVertexInputAttributeDescription(1, 0, EFormat.FORMAT_R32G32_SFLOAT, 12);
            m_PipelineState.AddVertexInputAttributeDescription(2, 0, EFormat.FORMAT_R32G32B32_SFLOAT, 20);
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
        catch
        {
            ReleasePipelineResources();
            throw;
        }
    }

    public void SetSmokeResources(RHITexture2DResource texture, RHIStaticMeshResource mesh)
    {
        if (!texture.IsValid ||
            texture.BindlessImageIndex == InvalidBindlessIndex ||
            texture.BindlessSamplerIndex == InvalidBindlessIndex)
        {
            throw new InvalidOperationException("[SmokeTrianglePass] Smoke texture is not ready for bindless sampling.");
        }

        if (!mesh.IsValid)
        {
            throw new InvalidOperationException("[SmokeTrianglePass] Smoke mesh is not ready for indexed drawing.");
        }

        m_TextureConstants = new SmokeTextureConstants
        {
            ImageIndex = texture.BindlessImageIndex,
            SamplerIndex = texture.BindlessSamplerIndex
        };
        m_VertexBuffer = mesh.VertexBuffer;
        m_IndexBuffer = mesh.IndexBuffer;
        m_IndexType = mesh.IndexType;
        m_IndexCount = mesh.IndexCount;
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
                throw new InvalidOperationException($"[SmokeTrianglePass] Failed to allocate shader program for {cookedStage.Stage.EntryPoint}.");
            }

            if (!factory.AttachProgramByteCode(program, rhiStage, shaderCode, cookedStage.Stage.EntryPoint))
            {
                throw new InvalidOperationException($"[SmokeTrianglePass] Failed to attach shader bytecode for {cookedStage.Stage.EntryPoint}.");
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

        if (m_VertexShaderAsset.IsValid)
        {
            m_AssetDatabase.Release(m_VertexShaderAsset);
        }

        if (m_FragmentShaderAsset.IsValid)
        {
            m_AssetDatabase.Release(m_FragmentShaderAsset);
        }

        m_VertexShaderAsset = CookedAssetHandle.Invalid;
        m_FragmentShaderAsset = CookedAssetHandle.Invalid;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct SmokeTextureConstants
    {
        public uint ImageIndex;
        public uint SamplerIndex;
    }
}
