namespace ArisenEngine.Rendering;

internal sealed class GenericRenderPipelineFeatureRegistryCore<TFeature>
    where TFeature : class
{
    private readonly record struct Registration(TFeature Feature, string FeatureId, int Order);

    private sealed class RegistrationComparer : IComparer<Registration>
    {
        public static RegistrationComparer Instance { get; } = new();

        public int Compare(Registration left, Registration right)
        {
            int order = left.Order.CompareTo(right.Order);
            return order != 0
                ? order
                : StringComparer.Ordinal.Compare(left.FeatureId, right.FeatureId);
        }
    }

    private readonly object m_Gate = new();
    private readonly Dictionary<string, Registration> m_Registrations = new(StringComparer.Ordinal);
    private TFeature[] m_ActiveFeatures = Array.Empty<TFeature>();
    private bool m_IsPipelineActive;

    public int Count
    {
        get
        {
            lock (m_Gate)
            {
                return m_Registrations.Count;
            }
        }
    }

    public bool IsPipelineActive
    {
        get
        {
            lock (m_Gate)
            {
                return m_IsPipelineActive;
            }
        }
    }

    public void Register(TFeature feature, string featureId, int order)
    {
        ArgumentNullException.ThrowIfNull(feature);
        ValidateFeatureId(featureId);

        lock (m_Gate)
        {
            if (m_IsPipelineActive)
            {
                throw new InvalidOperationException(
                    $"[GenericRP.Features] Cannot register feature '{featureId}' after pipeline activation. " +
                    "Register features during package OnLoad before RenderSubsystem initialization.");
            }

            if (!m_Registrations.TryAdd(
                    featureId,
                    new Registration(feature, featureId, order)))
            {
                throw new InvalidOperationException(
                    $"[GenericRP.Features] Feature ID '{featureId}' is already registered.");
            }
        }
    }

    public bool IsRegistered(TFeature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);

        lock (m_Gate)
        {
            foreach (Registration registration in m_Registrations.Values)
            {
                if (ReferenceEquals(registration.Feature, feature))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public bool Unregister(TFeature feature, string featureId)
    {
        ArgumentNullException.ThrowIfNull(feature);
        ValidateFeatureId(featureId);

        lock (m_Gate)
        {
            if (m_IsPipelineActive)
            {
                throw new InvalidOperationException(
                    $"[GenericRP.Features] Cannot unregister feature '{featureId}' while the pipeline is active.");
            }

            if (!m_Registrations.TryGetValue(featureId, out var registration))
            {
                return false;
            }

            if (!ReferenceEquals(registration.Feature, feature))
            {
                throw new InvalidOperationException(
                    $"[GenericRP.Features] Feature ID '{featureId}' is registered to another instance.");
            }

            return m_Registrations.Remove(featureId);
        }
    }

    public TFeature[] BeginPipelineActivation()
    {
        lock (m_Gate)
        {
            if (m_IsPipelineActive)
            {
                throw new InvalidOperationException(
                    "[GenericRP.Features] The feature registry is already frozen for an active pipeline.");
            }

            if (m_Registrations.Count == 0)
            {
                m_ActiveFeatures = Array.Empty<TFeature>();
            }
            else
            {
                var registrations = new Registration[m_Registrations.Count];
                m_Registrations.Values.CopyTo(registrations, 0);
                Array.Sort(registrations, RegistrationComparer.Instance);

                var activeFeatures = new TFeature[registrations.Length];
                for (int i = 0; i < registrations.Length; i++)
                {
                    activeFeatures[i] = registrations[i].Feature;
                }

                m_ActiveFeatures = activeFeatures;
            }

            m_IsPipelineActive = true;
            return m_ActiveFeatures;
        }
    }

    public void EndPipelineActivation()
    {
        lock (m_Gate)
        {
            m_IsPipelineActive = false;
            m_ActiveFeatures = Array.Empty<TFeature>();
        }
    }

    private static void ValidateFeatureId(string featureId)
    {
        if (string.IsNullOrWhiteSpace(featureId) ||
            !string.Equals(featureId, featureId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "[GenericRP.Features] Feature ID must be non-empty and cannot have leading or trailing whitespace.",
                nameof(featureId));
        }
    }
}
