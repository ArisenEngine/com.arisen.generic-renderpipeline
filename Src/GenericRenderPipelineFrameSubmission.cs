using System.Runtime.ExceptionServices;

namespace ArisenEngine.Rendering;

internal interface IGenericRenderPipelineFrameSubmissionActions
{
    void UpdatePreparedAssetTicket(ulong submittedTicket);

    void ReleaseCompletedResources();

    void NotifyFeatures(ulong submittedTicket);
}

internal static class GenericRenderPipelineFrameSubmission
{
    public static void Execute<TActions>(
        ref TActions actions,
        ulong submittedTicket)
        where TActions : struct, IGenericRenderPipelineFrameSubmissionActions
    {
        var failures = new GenericRenderPipelineSubmissionFailureState();

        try
        {
            actions.UpdatePreparedAssetTicket(submittedTicket);
        }
        catch (Exception ex)
        {
            failures.Capture(ex);
        }

        try
        {
            actions.ReleaseCompletedResources();
        }
        catch (Exception ex)
        {
            failures.CaptureSecondary(
                "completed-resource release",
                ex);
        }

        try
        {
            actions.NotifyFeatures(submittedTicket);
        }
        catch (Exception ex)
        {
            failures.CaptureSecondary(
                "feature notification",
                ex);
        }

        failures.ThrowIfFailed(
            "Generic RP frame submission callbacks failed.");
    }
}

internal struct GenericRenderPipelineSubmissionFailureState
{
    private Exception? m_PrimaryFailure;
    private List<Exception>? m_Failures;

    public void Capture(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (m_PrimaryFailure == null)
        {
            m_PrimaryFailure = failure;
            return;
        }

        EnsureFailureList().Add(failure);
    }

    public void CaptureSecondary(string stage, Exception failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentNullException.ThrowIfNull(failure);
        if (m_PrimaryFailure == null)
        {
            m_PrimaryFailure = failure;
            return;
        }

        EnsureFailureList().Add(new InvalidOperationException(
            $"Generic RP frame submission {stage} failed.",
            failure));
    }

    public void ThrowIfFailed(string aggregateMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregateMessage);
        if (m_PrimaryFailure == null)
        {
            return;
        }

        if (m_Failures == null)
        {
            ExceptionDispatchInfo.Capture(m_PrimaryFailure).Throw();
        }

        throw new AggregateException(aggregateMessage, m_Failures);
    }

    private List<Exception> EnsureFailureList()
    {
        if (m_Failures != null)
        {
            return m_Failures;
        }

        m_Failures = new List<Exception>(3)
        {
            m_PrimaryFailure!
        };
        return m_Failures;
    }
}
