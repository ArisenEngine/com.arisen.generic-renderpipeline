using ArisenEngine.Resources.Serialization;

namespace ArisenEngine.Rendering;

internal sealed class GenericPreparedAssetProviderLifecycleState
{
    private readonly object m_Gate = new();
    private readonly Queue<RuntimeAssetResidencyKey> m_PendingReleases = new();
    private readonly HashSet<RuntimeAssetResidencyKey> m_ReleaseTombstones = new();
    private RuntimePreparedAssetProviderMetrics m_PhysicalMetrics;
    private MetricsPublication m_PublishedMetrics = new(default);

    internal object Gate => m_Gate;

    internal int PendingReleaseCount
    {
        get
        {
            lock (m_Gate)
            {
                return m_PendingReleases.Count;
            }
        }
    }

    internal bool RequestRelease(in RuntimeAssetResidencyKey key)
    {
        lock (m_Gate)
        {
            if (!m_ReleaseTombstones.Add(key))
            {
                return false;
            }

            m_PendingReleases.Enqueue(key);
            PublishMetricsLocked();
            return true;
        }
    }

    internal bool IsReleasePending(in RuntimeAssetResidencyKey key)
    {
        lock (m_Gate)
        {
            return m_ReleaseTombstones.Contains(key);
        }
    }

    internal bool IsReleasePendingLocked(in RuntimeAssetResidencyKey key)
    {
        EnsureGateHeld();
        return m_ReleaseTombstones.Contains(key);
    }

    internal bool TryPeekPendingRelease(out RuntimeAssetResidencyKey key)
    {
        lock (m_Gate)
        {
            if (m_PendingReleases.Count == 0)
            {
                key = default;
                return false;
            }

            key = m_PendingReleases.Peek();
            return true;
        }
    }

    internal void CompletePendingRelease(
        in RuntimeAssetResidencyKey key,
        in RuntimePreparedAssetProviderMetrics physicalMetrics)
    {
        lock (m_Gate)
        {
            if (m_PendingReleases.Count == 0 ||
                m_PendingReleases.Peek() != key ||
                !m_ReleaseTombstones.Remove(key))
            {
                throw new InvalidOperationException(
                    $"GenericRP prepared-asset release ordering was lost for '{key}'.");
            }

            m_PendingReleases.Dequeue();
            m_PhysicalMetrics = physicalMetrics;
            PublishMetricsLocked();
        }
    }

    internal void PublishPhysicalMetrics(
        in RuntimePreparedAssetProviderMetrics physicalMetrics)
    {
        lock (m_Gate)
        {
            m_PhysicalMetrics = physicalMetrics;
            PublishMetricsLocked();
        }
    }

    internal RuntimePreparedAssetProviderMetrics ReadMetrics() =>
        Volatile.Read(ref m_PublishedMetrics).Metrics;

    private void PublishMetricsLocked()
    {
        int pendingDisposalCount = checked(
            m_PhysicalMetrics.PendingDisposalCount + m_PendingReleases.Count);
        var nextMetrics = new RuntimePreparedAssetProviderMetrics(
            m_PhysicalMetrics.PreparedResourceCount,
            m_PhysicalMetrics.EstimatedGpuBytes,
            pendingDisposalCount,
            m_PhysicalMetrics.DescriptorCount);
        MetricsPublication currentPublication =
            Volatile.Read(ref m_PublishedMetrics);
        if (currentPublication.Metrics == nextMetrics)
        {
            return;
        }

        Volatile.Write(
            ref m_PublishedMetrics,
            new MetricsPublication(nextMetrics));
    }

    private void EnsureGateHeld()
    {
        if (!Monitor.IsEntered(m_Gate))
        {
            throw new InvalidOperationException(
                "GenericRP prepared-asset lifecycle gate must be held for this operation.");
        }
    }

    private sealed record MetricsPublication(
        RuntimePreparedAssetProviderMetrics Metrics);
}

internal sealed class GenericPreparedEnvironmentRetirementState
{
    private readonly IDisposable m_Lighting;
    private readonly IDisposable m_Texture;
    private bool m_RetirementStarted;
    private bool m_LightingTransferred;
    private bool m_TextureTransferred;

    internal GenericPreparedEnvironmentRetirementState(
        IDisposable lighting,
        IDisposable texture)
    {
        m_Lighting = lighting ?? throw new ArgumentNullException(nameof(lighting));
        m_Texture = texture ?? throw new ArgumentNullException(nameof(texture));
    }

    internal bool IsCurrent => !m_RetirementStarted;

    internal bool IsComplete =>
        m_LightingTransferred && m_TextureTransferred;

    internal void TransferOwnership(Action<IDisposable> transfer)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        m_RetirementStarted = true;

        if (!m_LightingTransferred)
        {
            transfer(m_Lighting);
            m_LightingTransferred = true;
        }

        if (!m_TextureTransferred)
        {
            transfer(m_Texture);
            m_TextureTransferred = true;
        }
    }
}
