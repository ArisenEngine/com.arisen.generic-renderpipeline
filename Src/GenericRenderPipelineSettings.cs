using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Math;
using ArisenEngine.Core.Serialization;

namespace ArisenEngine.Rendering;

public readonly record struct GenericRenderPipelineSettings(
    string Name,
    Color FallbackClearColor,
    GenericShadowSettings Shadows)
{
    public static GenericRenderPipelineSettings Default { get; } = new(
        "Generic RP Default",
        new Color(1.0f, 0.4f, 0.7f, 1.0f),
        GenericShadowSettings.Default);
}

public readonly record struct GenericShadowSettings(
    bool Enabled,
    uint MapSize,
    float DepthBias,
    float SlopeBias,
    float Strength,
    int PcfRadius,
    int CascadeCount,
    float MaximumDistance,
    float PracticalSplitWeight,
    float TerminalFadeFraction)
{
    public static GenericShadowSettings Default { get; } = new(
        Enabled: true,
        MapSize: 2048,
        DepthBias: 0.00165f,
        SlopeBias: 0.00231f,
        Strength: 0.78f,
        PcfRadius: 1,
        CascadeCount: 4,
        MaximumDistance: 250.0f,
        PracticalSplitWeight: 0.65f,
        TerminalFadeFraction: 0.10f);
}

public static class GenericRenderPipelineSettingsLoader
{
    public const string AssetType = "RenderPipelineSettings";
    public const string ProviderPackageId = "com.arisen.generic-renderpipeline";

    public static GenericRenderPipelineSettings Load(
        IAssetDatabase assetDatabase,
        AssetRef<RenderPipelineSettingsSourceAsset> settingsRef)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        return assetDatabase.CanReadSourceAssets
            ? LoadSource(assetDatabase, settingsRef)
            : GenericRenderPipelineSettingsCooker.LoadCooked(assetDatabase, settingsRef);
    }

    public static GenericRenderPipelineSettings LoadSource(
        IAssetDatabase assetDatabase,
        AssetRef<RenderPipelineSettingsSourceAsset> settingsRef)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);

        if (!settingsRef.IsValid || !assetDatabase.TryGetAsset(settingsRef, out var sourceAsset))
        {
            throw new InvalidOperationException(
                $"[GenericRP] Render-pipeline settings asset '{settingsRef.Guid:D}' was not found as '{AssetType}'.");
        }

        if (!string.IsNullOrWhiteSpace(settingsRef.PackageId) &&
            !string.Equals(
                sourceAsset.PackageId,
                settingsRef.PackageId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[GenericRP] Render-pipeline settings asset '{settingsRef.Guid:D}' belongs to package '{sourceAsset.PackageId}', expected '{settingsRef.PackageId}'.");
        }

        return LoadSource(sourceAsset);
    }

    public static GenericRenderPipelineSettings LoadSource(AssetRecord sourceAsset)
    {
        ArgumentNullException.ThrowIfNull(sourceAsset);

        if (!string.Equals(sourceAsset.AssetType, AssetType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[GenericRP] Asset '{sourceAsset.Guid:D}' has type '{sourceAsset.AssetType}', expected '{AssetType}'.");
        }

        if (!File.Exists(sourceAsset.SourcePath))
        {
            throw new InvalidOperationException(
                $"[GenericRP] Render-pipeline settings source was not found: {sourceAsset.SourcePath}");
        }

        var source = SerializationUtil.Deserialize<SerializedSettings>(
            sourceAsset.SourcePath,
            serializeIfNotExist: false);
        source.Validate(sourceAsset.SourcePath);

        return new GenericRenderPipelineSettings(
            string.IsNullOrWhiteSpace(source.Name)
                ? Path.GetFileNameWithoutExtension(sourceAsset.SourcePath)
                : source.Name.Trim(),
            source.Fallback.ClearColor.ToColor(),
            new GenericShadowSettings(
                source.Shadows.Enabled,
                source.Shadows.MapSize,
                source.Shadows.DepthBias,
                source.Shadows.SlopeBias,
                source.Shadows.Strength,
                source.Shadows.PcfRadius,
                source.Shadows.CascadeCount,
                source.Shadows.MaximumDistance,
                source.Shadows.PracticalSplitWeight,
                source.Shadows.TerminalFadeFraction));
    }

    private sealed class SerializedSettings
    {
        public int Version { get; set; } = 2;
        public string Pipeline { get; set; } = "GenericRP";
        public string Name { get; set; } = string.Empty;
        public SerializedFallback Fallback { get; set; } = new();
        public SerializedShadows Shadows { get; set; } = new();

        public void Validate(string sourcePath)
        {
            if (Version is not (1 or 2))
            {
                throw Invalid(sourcePath, $"version '{Version}' is not supported");
            }

            if (!string.Equals(Pipeline, "GenericRP", StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid(sourcePath, $"Pipeline must be 'GenericRP', found '{Pipeline}'");
            }

            if (Fallback == null)
            {
                throw Invalid(sourcePath, "Fallback settings are missing");
            }

            if (Shadows == null)
            {
                throw Invalid(sourcePath, "Shadows settings are missing");
            }

            Fallback.Validate(sourcePath);
            Shadows.Validate(sourcePath);
        }
    }

    private sealed class SerializedFallback
    {
        public SerializedColor ClearColor { get; set; } = new();

        public void Validate(string sourcePath)
        {
            if (ClearColor == null)
            {
                throw Invalid(sourcePath, "Fallback.ClearColor is missing");
            }

            ClearColor.Validate(sourcePath, "Fallback.ClearColor");
        }
    }

    private sealed class SerializedShadows
    {
        public bool Enabled { get; set; } = true;
        public uint MapSize { get; set; } = 2048;
        public float DepthBias { get; set; } = 0.00165f;
        public float SlopeBias { get; set; } = 0.00231f;
        public float Strength { get; set; } = 0.78f;
        public int PcfRadius { get; set; } = 1;
        public int CascadeCount { get; set; } = GenericShadowSettings.Default.CascadeCount;
        public float MaximumDistance { get; set; } = GenericShadowSettings.Default.MaximumDistance;
        public float PracticalSplitWeight { get; set; } = GenericShadowSettings.Default.PracticalSplitWeight;
        public float TerminalFadeFraction { get; set; } = GenericShadowSettings.Default.TerminalFadeFraction;

        public void Validate(string sourcePath)
        {
            if (MapSize < 256 || MapSize > 8192 || !IsPowerOfTwo(MapSize))
            {
                throw Invalid(sourcePath, "Shadows.MapSize must be a power of two between 256 and 8192");
            }

            ValidateFiniteRange(sourcePath, "Shadows.DepthBias", DepthBias, 0.0f, 0.1f);
            ValidateFiniteRange(sourcePath, "Shadows.SlopeBias", SlopeBias, 0.0f, 0.1f);
            ValidateFiniteRange(sourcePath, "Shadows.Strength", Strength, 0.0f, 1.0f);
            if (PcfRadius < 0 || PcfRadius > 3)
            {
                throw Invalid(sourcePath, "Shadows.PcfRadius must be between 0 and 3");
            }

            if (CascadeCount < 1 || CascadeCount > 4)
            {
                throw Invalid(sourcePath, "Shadows.CascadeCount must be between 1 and 4");
            }

            ValidateFiniteRange(
                sourcePath,
                "Shadows.MaximumDistance",
                MaximumDistance,
                5.0f,
                10000.0f);
            ValidateFiniteRange(
                sourcePath,
                "Shadows.PracticalSplitWeight",
                PracticalSplitWeight,
                0.0f,
                1.0f);
            ValidateFiniteRange(
                sourcePath,
                "Shadows.TerminalFadeFraction",
                TerminalFadeFraction,
                0.0f,
                0.5f);
        }
    }

    private sealed class SerializedColor
    {
        public float R { get; set; } = 1.0f;
        public float G { get; set; } = 0.4f;
        public float B { get; set; } = 0.7f;
        public float A { get; set; } = 1.0f;

        public void Validate(string sourcePath, string fieldName)
        {
            ValidateFiniteRange(sourcePath, $"{fieldName}.R", R, 0.0f, 64.0f);
            ValidateFiniteRange(sourcePath, $"{fieldName}.G", G, 0.0f, 64.0f);
            ValidateFiniteRange(sourcePath, $"{fieldName}.B", B, 0.0f, 64.0f);
            ValidateFiniteRange(sourcePath, $"{fieldName}.A", A, 0.0f, 1.0f);
        }

        public Color ToColor() => new(R, G, B, A);
    }

    private static bool IsPowerOfTwo(uint value) => (value & (value - 1)) == 0;

    private static void ValidateFiniteRange(
        string sourcePath,
        string fieldName,
        float value,
        float minimum,
        float maximum)
    {
        if (!float.IsFinite(value) || value < minimum || value > maximum)
        {
            throw Invalid(
                sourcePath,
                $"{fieldName} must be finite and between {minimum} and {maximum}");
        }
    }

    private static InvalidOperationException Invalid(string sourcePath, string diagnostic)
    {
        return new InvalidOperationException(
            $"[GenericRP] Render-pipeline settings '{sourcePath}' are invalid: {diagnostic}.");
    }
}
