using ArisenEngine.Rendering.Resources;
using System.Numerics;

namespace ArisenEngine.Rendering;

public enum GenericRenderPipelineFeatureGraphStage
{
    DirectionalShadow = 0,
    Opaque = 1
}

public readonly struct GenericRenderPipelineFeatureFrameContext
{
    internal GenericRenderPipelineFeatureFrameContext(
        RenderContext renderContext,
        Matrix4x4 viewProjection,
        Vector3 cameraPosition,
        DirectionalLight directionalLight,
        SceneEnvironment sceneEnvironment,
        in DirectionalShadowFrameData directionalShadow,
        bool visualValidationEnabled)
    {
        RenderContext = renderContext;
        ViewProjection = viewProjection;
        CameraPosition = cameraPosition;
        DirectionalLight = directionalLight;
        SceneEnvironment = sceneEnvironment;
        DirectionalShadow = directionalShadow;
        VisualValidationEnabled = visualValidationEnabled;
    }

    public RenderContext RenderContext { get; }
    public RenderFrameSnapshot Snapshot => RenderContext.Snapshot;
    public Matrix4x4 ViewProjection { get; }
    public Vector3 CameraPosition { get; }
    public DirectionalLight DirectionalLight { get; }
    public SceneEnvironment SceneEnvironment { get; }
    public DirectionalShadowFrameData DirectionalShadow { get; }
    public bool VisualValidationEnabled { get; }
}

public readonly struct GenericRenderPipelineFeatureGraphContext
{
    internal GenericRenderPipelineFeatureGraphContext(
        RenderGraph graph,
        GenericRenderPipelineFeatureFrameContext frame,
        RenderGraphTexture sceneColor,
        RenderGraphTexture frameDepth,
        RenderGraphTexture directionalShadow,
        in DirectionalShadowFrameData directionalShadowFrame,
        GenericShadowSettings shadowSettings,
        RHIEnvironmentLightingResource? environmentLighting)
    {
        Graph = graph;
        Frame = frame;
        SceneColor = sceneColor;
        FrameDepth = frameDepth;
        DirectionalShadow = directionalShadow;
        DirectionalShadowFrame = directionalShadowFrame;
        ShadowSettings = shadowSettings;
        EnvironmentLighting = environmentLighting;
    }

    public RenderGraph Graph { get; }
    public GenericRenderPipelineFeatureFrameContext Frame { get; }
    public RenderGraphTexture SceneColor { get; }
    public RenderGraphTexture FrameDepth { get; }
    public RenderGraphTexture DirectionalShadow { get; }
    public DirectionalShadowFrameData DirectionalShadowFrame { get; }
    public GenericShadowSettings ShadowSettings { get; }
    public RHIEnvironmentLightingResource? EnvironmentLighting { get; }
}

public readonly struct GenericRenderPipelineFeatureSubmissionContext
{
    internal GenericRenderPipelineFeatureSubmissionContext(
        GenericRenderPipelineFeatureFrameContext frame,
        ulong submittedTicket)
    {
        Frame = frame;
        SubmittedTicket = submittedTicket;
    }

    public GenericRenderPipelineFeatureFrameContext Frame { get; }
    public ulong SubmittedTicket { get; }
}

/// <summary>
/// Coarse-grained lifecycle for an optional Generic RP render feature.
/// Implementations are frozen at pipeline activation and must not perform service lookup,
/// managed allocation, or locking inside graph contribution or command recording paths.
/// </summary>
public interface IGenericRenderPipelineFeature
{
    string FeatureId { get; }
    int Order { get; }

    void ConsumeExtractedFrame(in GenericRenderPipelineFeatureFrameContext context);

    void PrepareResources(in GenericRenderPipelineFeatureFrameContext context);

    void AddRenderGraphPasses(
        GenericRenderPipelineFeatureGraphStage stage,
        in GenericRenderPipelineFeatureGraphContext context);

    void OnFrameSubmitted(in GenericRenderPipelineFeatureSubmissionContext context);

    /// <summary>
    /// Releases all feature-owned device resources. Implementations must be idempotent.
    /// </summary>
    void ReleaseDeviceResources();
}
