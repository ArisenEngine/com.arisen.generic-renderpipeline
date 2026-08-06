using ArisenKernel.Diagnostics;

namespace ArisenEngine.Rendering;

internal static class GenericRenderPipelineFeatureDispatcher
{
    public static void ConsumeExtractedFrame(
        IGenericRenderPipelineFeature[] features,
        in GenericRenderPipelineFeatureFrameContext context)
    {
        for (int i = 0; i < features.Length; i++)
        {
            try
            {
                features[i].ConsumeExtractedFrame(context);
            }
            catch (Exception ex)
            {
                throw HookFailure(features[i], nameof(IGenericRenderPipelineFeature.ConsumeExtractedFrame), ex);
            }
        }
    }

    public static void PrepareResources(
        IGenericRenderPipelineFeature[] features,
        in GenericRenderPipelineFeatureFrameContext context)
    {
        for (int i = 0; i < features.Length; i++)
        {
            try
            {
                features[i].PrepareResources(context);
            }
            catch (Exception ex)
            {
                throw HookFailure(features[i], nameof(IGenericRenderPipelineFeature.PrepareResources), ex);
            }
        }
    }

    public static void AddRenderGraphPasses(
        IGenericRenderPipelineFeature[] features,
        GenericRenderPipelineFeatureGraphStage stage,
        in GenericRenderPipelineFeatureGraphContext context)
    {
        for (int i = 0; i < features.Length; i++)
        {
            try
            {
                features[i].AddRenderGraphPasses(stage, context);
            }
            catch (Exception ex)
            {
                throw HookFailure(
                    features[i],
                    $"{nameof(IGenericRenderPipelineFeature.AddRenderGraphPasses)}({stage})",
                    ex);
            }
        }
    }

    public static void OnFrameSubmitted(
        IGenericRenderPipelineFeature[] features,
        in GenericRenderPipelineFeatureSubmissionContext context)
    {
        var failures = new GenericRenderPipelineSubmissionFailureState();
        for (int i = 0; i < features.Length; i++)
        {
            try
            {
                features[i].OnFrameSubmitted(context);
            }
            catch (Exception ex)
            {
                failures.Capture(HookFailure(
                    features[i],
                    nameof(IGenericRenderPipelineFeature.OnFrameSubmitted),
                    ex));
            }
        }

        failures.ThrowIfFailed(
            "One or more Generic RP features failed during frame submission notification.");
    }

    public static void ReleaseDeviceResources(IGenericRenderPipelineFeature[] features)
    {
        for (int i = features.Length - 1; i >= 0; i--)
        {
            try
            {
                features[i].ReleaseDeviceResources();
            }
            catch (Exception ex)
            {
                KernelLog.ErrorFormat(
                    "[GenericRP.Features] Feature '{0}' failed during {1}: {2}",
                    features[i].FeatureId,
                    nameof(IGenericRenderPipelineFeature.ReleaseDeviceResources),
                    ex.Message);
            }
        }
    }

    private static InvalidOperationException HookFailure(
        IGenericRenderPipelineFeature feature,
        string hook,
        Exception innerException)
    {
        return new InvalidOperationException(
            $"[GenericRP.Features] Feature '{feature.FeatureId}' failed during {hook}.",
            innerException);
    }
}
