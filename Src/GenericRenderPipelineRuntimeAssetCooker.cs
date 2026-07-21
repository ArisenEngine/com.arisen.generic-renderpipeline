using System.Buffers.Binary;
using System.Text;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Math;
using ArisenEngine.Rendering.Resources;

namespace ArisenEngine.Rendering;

public sealed record CookedGenericRenderPipelineSettings(
    Guid Guid,
    string Variant,
    string Path,
    long SizeInBytes);

public static class GenericRenderPipelineSettingsCooker
{
    public const string RuntimeVariant = "generic-rp.settings.v1";
    public const string CookedExtension = ".renderpipeline";
    public const int CookedFormatVersion = 1;

    private const int MaxNameBytes = 16 * 1024;
    private static readonly byte[] s_Magic = Encoding.ASCII.GetBytes("ARISGRPS");
    private static readonly UTF8Encoding s_StrictUtf8 = new(false, true);

    public static GenericRenderPipelineSettings LoadCooked(
        IAssetDatabase assetDatabase,
        AssetRef<RenderPipelineSettingsSourceAsset> settingsRef)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        if (!settingsRef.IsValid)
        {
            throw new ArgumentException(
                "[GenericRPSettingsCooker] Cooked settings require a valid asset reference.",
                nameof(settingsRef));
        }

        if (!assetDatabase.TryGetAssetDescriptor(settingsRef.Guid, out AssetDescriptor descriptor))
        {
            throw new InvalidOperationException(
                $"[GenericRPSettingsCooker] Settings asset '{settingsRef.Guid:D}' has no available identity metadata.");
        }

        if (!string.Equals(
                descriptor.AssetType,
                GenericRenderPipelineSettingsLoader.AssetType,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[GenericRPSettingsCooker] Asset '{settingsRef.Guid:D}' has type " +
                $"'{descriptor.AssetType}', expected '{GenericRenderPipelineSettingsLoader.AssetType}'.");
        }

        if (!string.IsNullOrWhiteSpace(settingsRef.PackageId) &&
            !string.Equals(
                descriptor.PackageId,
                settingsRef.PackageId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[GenericRPSettingsCooker] Settings asset '{settingsRef.Guid:D}' belongs to package " +
                $"'{descriptor.PackageId}', expected '{settingsRef.PackageId}'.");
        }

        if (!assetDatabase.TryLoadCookedAsset(
                settingsRef.Guid,
                RuntimeVariant,
                GenericRenderPipelineSettingsLoader.AssetType,
                out CookedAssetHandle handle))
        {
            throw new InvalidOperationException(
                $"[GenericRPSettingsCooker] Cooked settings '{settingsRef.Guid:D}' variant " +
                $"'{RuntimeVariant}' are unavailable.");
        }

        try
        {
            return ReadPayload(
                assetDatabase.GetCookedAssetBytes(handle).Span,
                settingsRef.Guid);
        }
        catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException or OverflowException)
        {
            throw new InvalidOperationException(
                $"[GenericRPSettingsCooker] Cooked settings '{settingsRef.Guid:D}' are invalid: {ex.Message}",
                ex);
        }
        finally
        {
            assetDatabase.Release(handle);
        }
    }

    public static CookedGenericRenderPipelineSettings Cook(
        IAssetDatabase assetDatabase,
        AssetRef<RenderPipelineSettingsSourceAsset> settingsRef)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        GenericRenderPipelineSettings settings =
            GenericRenderPipelineSettingsLoader.LoadSource(assetDatabase, settingsRef);
        if (!assetDatabase.TryGetAsset(settingsRef, out AssetRecord? sourceAsset))
        {
            throw new InvalidOperationException(
                $"[GenericRPSettingsCooker] Settings asset '{settingsRef.Guid:D}' is not indexed.");
        }

        string outputPath = assetDatabase.GetCookedArtifactPath(
            settingsRef.Guid,
            RuntimeVariant,
            CookedExtension);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        byte[] nameBytes = Encoding.UTF8.GetBytes(settings.Name);
        if (nameBytes.Length > MaxNameBytes)
        {
            throw new InvalidOperationException(
                $"[GenericRPSettingsCooker] Settings name exceeds {MaxNameBytes} UTF-8 bytes.");
        }

        using (var stream = File.Create(outputPath))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
        {
            writer.Write(s_Magic);
            writer.Write(CookedFormatVersion);
            writer.Write(settingsRef.Guid.ToByteArray());
            writer.Write(nameBytes.Length);
            writer.Write(nameBytes);
            writer.Write(settings.FallbackClearColor.r);
            writer.Write(settings.FallbackClearColor.g);
            writer.Write(settings.FallbackClearColor.b);
            writer.Write(settings.FallbackClearColor.a);
            writer.Write(settings.Shadows.Enabled ? 1 : 0);
            writer.Write(settings.Shadows.MapSize);
            writer.Write(settings.Shadows.DepthBias);
            writer.Write(settings.Shadows.SlopeBias);
            writer.Write(settings.Shadows.Strength);
            writer.Write(settings.Shadows.PcfRadius);
        }

        var output = new FileInfo(outputPath);
        assetDatabase.RegisterCookedArtifact(new CookedAssetRecord(
            settingsRef.Guid,
            sourceAsset.AssetType,
            RuntimeVariant,
            output.FullName,
            output.Length,
            output.LastWriteTimeUtc));
        return new CookedGenericRenderPipelineSettings(
            settingsRef.Guid,
            RuntimeVariant,
            output.FullName,
            output.Length);
    }

    private static GenericRenderPipelineSettings ReadPayload(
        ReadOnlySpan<byte> bytes,
        Guid expectedGuid)
    {
        var reader = new SettingsPayloadReader(bytes);
        if (!reader.ReadBytes(s_Magic.Length).SequenceEqual(s_Magic))
        {
            throw new InvalidDataException("header magic is invalid");
        }

        int version = reader.ReadInt32();
        if (version != CookedFormatVersion)
        {
            throw new InvalidDataException(
                $"format version '{version}' is unsupported; expected '{CookedFormatVersion}'");
        }

        Guid payloadGuid = reader.ReadGuid();
        if (payloadGuid != expectedGuid)
        {
            throw new InvalidDataException(
                $"payload belongs to '{payloadGuid:D}', expected '{expectedGuid:D}'");
        }

        string name = reader.ReadString(MaxNameBytes);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidDataException("settings name is empty");
        }

        var clearColor = new Color(
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle());
        int rawEnabled = reader.ReadInt32();
        if (rawEnabled is not (0 or 1))
        {
            throw new InvalidDataException("shadow enabled flag is not canonical");
        }

        var shadows = new GenericShadowSettings(
            rawEnabled != 0,
            reader.ReadUInt32(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadInt32());
        reader.EnsureFullyRead();
        ValidateCookedSettings(clearColor, shadows);
        return new GenericRenderPipelineSettings(name, clearColor, shadows);
    }

    private static void ValidateCookedSettings(
        Color clearColor,
        GenericShadowSettings shadows)
    {
        ValidateFiniteRange("Fallback.ClearColor.R", clearColor.r, 0.0f, 64.0f);
        ValidateFiniteRange("Fallback.ClearColor.G", clearColor.g, 0.0f, 64.0f);
        ValidateFiniteRange("Fallback.ClearColor.B", clearColor.b, 0.0f, 64.0f);
        ValidateFiniteRange("Fallback.ClearColor.A", clearColor.a, 0.0f, 1.0f);
        if (shadows.MapSize < 256 || shadows.MapSize > 8192 ||
            (shadows.MapSize & (shadows.MapSize - 1)) != 0)
        {
            throw new InvalidDataException(
                "Shadows.MapSize must be a power of two between 256 and 8192");
        }

        ValidateFiniteRange("Shadows.DepthBias", shadows.DepthBias, 0.0f, 0.1f);
        ValidateFiniteRange("Shadows.SlopeBias", shadows.SlopeBias, 0.0f, 0.1f);
        ValidateFiniteRange("Shadows.Strength", shadows.Strength, 0.0f, 1.0f);
        if (shadows.PcfRadius < 0 || shadows.PcfRadius > 3)
        {
            throw new InvalidDataException("Shadows.PcfRadius must be between 0 and 3");
        }
    }

    private static void ValidateFiniteRange(
        string fieldName,
        float value,
        float minimum,
        float maximum)
    {
        if (!float.IsFinite(value) || value < minimum || value > maximum)
        {
            throw new InvalidDataException(
                $"{fieldName} must be finite and between {minimum} and {maximum}");
        }
    }

    private ref struct SettingsPayloadReader
    {
        private readonly ReadOnlySpan<byte> m_Bytes;
        private int m_Offset;

        public SettingsPayloadReader(ReadOnlySpan<byte> bytes)
        {
            m_Bytes = bytes;
            m_Offset = 0;
        }

        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            if (count < 0 || count > m_Bytes.Length - m_Offset)
            {
                throw new InvalidDataException("payload is truncated");
            }

            ReadOnlySpan<byte> value = m_Bytes.Slice(m_Offset, count);
            m_Offset += count;
            return value;
        }

        public int ReadInt32()
        {
            return BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(sizeof(int)));
        }

        public uint ReadUInt32()
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(sizeof(uint)));
        }

        public float ReadSingle()
        {
            return BinaryPrimitives.ReadSingleLittleEndian(ReadBytes(sizeof(float)));
        }

        public Guid ReadGuid()
        {
            return new Guid(ReadBytes(16));
        }

        public string ReadString(int maximumByteCount)
        {
            int byteCount = ReadInt32();
            if (byteCount < 0 || byteCount > maximumByteCount)
            {
                throw new InvalidDataException(
                    $"string byte count '{byteCount}' exceeds the limit '{maximumByteCount}'");
            }

            return s_StrictUtf8.GetString(ReadBytes(byteCount));
        }

        public void EnsureFullyRead()
        {
            if (m_Offset != m_Bytes.Length)
            {
                throw new InvalidDataException(
                    $"payload has {m_Bytes.Length - m_Offset} trailing byte(s)");
            }
        }
    }
}

public sealed class GenericRenderPipelineRuntimeAssetCooker : IRuntimeAssetCooker
{
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly IRuntimeShaderCookRecipeRegistry m_ShaderRecipes;
    private readonly AssetRef<MaterialSourceAsset> m_DefaultMaterial;
    private readonly AssetRef<MeshSourceAsset> m_FallbackMesh;
    private readonly ShaderAsset[] m_RuntimeShaders;

    public GenericRenderPipelineRuntimeAssetCooker(
        IAssetDatabase assetDatabase,
        IRuntimeShaderCookRecipeRegistry shaderRecipes,
        AssetRef<MaterialSourceAsset> defaultMaterial,
        AssetRef<MeshSourceAsset> fallbackMesh,
        IEnumerable<ShaderAsset> runtimeShaders)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        m_ShaderRecipes = shaderRecipes ?? throw new ArgumentNullException(nameof(shaderRecipes));
        if (!defaultMaterial.IsValid)
        {
            throw new ArgumentException(
                "The Generic RP default material reference must be valid.",
                nameof(defaultMaterial));
        }

        if (!fallbackMesh.IsValid)
        {
            throw new ArgumentException(
                "The Generic RP fallback mesh reference must be valid.",
                nameof(fallbackMesh));
        }

        ArgumentNullException.ThrowIfNull(runtimeShaders);
        m_DefaultMaterial = defaultMaterial;
        m_FallbackMesh = fallbackMesh;
        m_RuntimeShaders = runtimeShaders.ToArray();
        if (m_RuntimeShaders.Length == 0 || m_RuntimeShaders.Any(shader => shader == null))
        {
            throw new ArgumentException(
                "At least one non-null Generic RP runtime shader is required.",
                nameof(runtimeShaders));
        }
    }

    public string ProviderId => "com.arisen.generic-renderpipeline.settings-cooker";

    public IReadOnlyCollection<string> AssetTypes { get; } =
        [GenericRenderPipelineSettingsLoader.AssetType];

    public RuntimeAssetCookerOutput Cook(
        RuntimeAssetCookContext context,
        RuntimeAssetCookRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateRequest(request);
        var settingsRef = new AssetRef<RenderPipelineSettingsSourceAsset>(
            request.Guid,
            GenericRenderPipelineSettingsLoader.AssetType,
            request.PackageId);
        CookedGenericRenderPipelineSettings cooked =
            GenericRenderPipelineSettingsCooker.Cook(m_AssetDatabase, settingsRef);
        RuntimeAssetCookDependencyRequest[] dependencies = BuildDependencies();
        return RuntimeAssetCookerOutput.FromFile(
            request,
            cooked.Variant,
            $"{request.PackageId}/{request.Guid:N}/{cooked.Variant}" +
            GenericRenderPipelineSettingsCooker.CookedExtension,
            cooked.Path,
            GenericRenderPipelineSettingsCooker.CookedFormatVersion,
            dependencies);
    }

    private RuntimeAssetCookDependencyRequest[] BuildDependencies()
    {
        var dependencies = new List<RuntimeAssetCookDependencyRequest>();
        AssetRecord defaultMaterial = GetAsset(
            m_DefaultMaterial.Guid,
            "Material");
        dependencies.Add(new RuntimeAssetCookDependencyRequest(
            defaultMaterial.Guid,
            defaultMaterial.PackageId,
            defaultMaterial.AssetType,
            Variant: string.Empty,
            Required: true));

        foreach (ShaderAsset shader in m_RuntimeShaders)
        {
            AssetRecord shaderSource = GetAsset(
                shader.Guid,
                ShaderAssetCooker.ShaderSourceAssetType);
            foreach (ShaderStageAsset stage in shader.Stages)
            {
                m_ShaderRecipes.RegisterRecipe(
                    shader,
                    stage.Name,
                    "com.arisen.generic-renderpipeline");
                dependencies.Add(new RuntimeAssetCookDependencyRequest(
                    shader.Guid,
                    shaderSource.PackageId,
                    shaderSource.AssetType,
                    shader.Variant.GetCookedVariant(
                        stage.EntryPoint,
                        shader.VariantKeywords),
                    Required: true));
            }
        }

        AssetRecord fallbackMesh = GetAsset(m_FallbackMesh.Guid, "Mesh");
        dependencies.Add(new RuntimeAssetCookDependencyRequest(
            fallbackMesh.Guid,
            fallbackMesh.PackageId,
            fallbackMesh.AssetType,
            Variant: string.Empty,
            Required: true));

        return dependencies.ToArray();
    }

    private void ValidateRequest(RuntimeAssetCookRequest request)
    {
        if (!string.Equals(
                request.AssetType,
                GenericRenderPipelineSettingsLoader.AssetType,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"[GenericRPSettingsCooker] Unsupported asset type '{request.AssetType}'.");
        }

        if (request.Variant.Length > 0 &&
            !string.Equals(
                request.Variant,
                GenericRenderPipelineSettingsCooker.RuntimeVariant,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"[GenericRPSettingsCooker] Variant '{request.Variant}' is unsupported.");
        }

        AssetRecord sourceAsset = GetAsset(request.Guid, request.AssetType);
        if (!string.Equals(
                sourceAsset.PackageId,
                request.PackageId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[GenericRPSettingsCooker] Settings '{request.Guid:D}' belong to " +
                $"'{sourceAsset.PackageId}', not '{request.PackageId}'.");
        }
    }

    private AssetRecord GetAsset(Guid guid, string assetType)
    {
        if (!m_AssetDatabase.TryGetAsset(guid, out AssetRecord? sourceAsset) ||
            !string.Equals(sourceAsset.AssetType, assetType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[GenericRPSettingsCooker] Required {assetType} asset '{guid:D}' is not indexed.");
        }

        return sourceAsset;
    }
}
