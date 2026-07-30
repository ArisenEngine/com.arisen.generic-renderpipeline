namespace ArisenEngine.Rendering;

public interface IGenericRenderPipelineRuntimeShaderRegistry
{
    void RegisterRuntimeShaders(
        string ownerId,
        IReadOnlyList<ShaderAsset> shaders);

    bool UnregisterRuntimeShaders(string ownerId);
}

internal readonly record struct GenericRenderPipelineRuntimeShaderContribution(
    string OwnerId,
    ShaderAsset Shader);

public sealed class GenericRenderPipelineRuntimeShaderRegistry
    : IGenericRenderPipelineRuntimeShaderRegistry
{
    private readonly SortedDictionary<string, ShaderAsset[]> m_ShadersByOwner =
        new(StringComparer.Ordinal);

    public void RegisterRuntimeShaders(
        string ownerId,
        IReadOnlyList<ShaderAsset> shaders)
    {
        ValidateOwnerId(ownerId);
        ArgumentNullException.ThrowIfNull(shaders);
        if (shaders.Count == 0)
        {
            throw Invalid("A runtime shader contribution cannot be empty.");
        }

        var copy = new ShaderAsset[shaders.Count];
        var identities = new HashSet<Guid>();
        for (int index = 0; index < shaders.Count; index++)
        {
            ShaderAsset shader = shaders[index]
                ?? throw Invalid("A runtime shader contribution cannot contain null shaders.");
            if (shader.Guid == Guid.Empty || shader.Stages.Count == 0)
            {
                throw Invalid(
                    "A contributed runtime shader requires a non-empty GUID and at least one stage.");
            }

            if (!identities.Add(shader.Guid))
            {
                throw Invalid(
                    $"Owner '{ownerId}' contributed shader '{shader.Guid:D}' more than once.");
            }

            copy[index] = shader;
        }

        Array.Sort(copy, static (left, right) => left.Guid.CompareTo(right.Guid));
        if (!m_ShadersByOwner.TryAdd(ownerId, copy))
        {
            throw Invalid($"Owner '{ownerId}' already registered runtime shaders.");
        }
    }

    public bool UnregisterRuntimeShaders(string ownerId)
    {
        return !string.IsNullOrWhiteSpace(ownerId) &&
               m_ShadersByOwner.Remove(ownerId);
    }

    internal GenericRenderPipelineRuntimeShaderContribution[] GetContributions()
    {
        var result = new GenericRenderPipelineRuntimeShaderContribution[
            m_ShadersByOwner.Sum(pair => pair.Value.Length)];
        int outputIndex = 0;
        foreach ((string ownerId, ShaderAsset[] shaders) in m_ShadersByOwner)
        {
            for (int shaderIndex = 0; shaderIndex < shaders.Length; shaderIndex++)
            {
                result[outputIndex++] = new(
                    ownerId,
                    shaders[shaderIndex]);
            }
        }

        return result;
    }

    private static void ValidateOwnerId(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId) ||
            !string.Equals(ownerId, ownerId.Trim(), StringComparison.Ordinal) ||
            ownerId.Any(char.IsControl))
        {
            throw Invalid("The runtime shader owner ID must be non-empty canonical text.");
        }
    }

    private static InvalidOperationException Invalid(string message)
    {
        return new InvalidOperationException(
            $"[GenericRP.RuntimeShaders] {message}");
    }
}
