using Arisen.Native.RHI;
using System.Diagnostics;
using System.IO;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.RHI;
using ArisenEngine.Rendering.Resources;
using ArisenEngine.Resources.Serialization;

namespace ArisenEngine.Rendering;

public sealed class GenericPreparedAssetProvider : IRuntimePreparedAssetProvider
{
    public const string Id = "com.arisen.generic-renderpipeline.prepared-assets";

    private readonly IAssetDatabase m_AssetDatabase;
    private readonly GenericRenderMaterialLibrary m_MaterialLibrary;
    private readonly DeferredRenderResourceDisposalQueue m_DisposalQueue;
    private readonly Dictionary<RuntimeAssetResidencyKey, RHIStaticMeshResource> m_Meshes = new();
    private readonly HashSet<RuntimeAssetResidencyKey> m_Materials = new();
    private readonly Dictionary<RuntimeAssetResidencyKey, PreparedEnvironment> m_Environments = new();
    private RHIDevice m_Device;
    private ulong m_DeviceGeneration;
    private ulong m_LastSubmittedTicket;
    private long m_EstimatedGpuBytes;

    public GenericPreparedAssetProvider(
        IAssetDatabase assetDatabase,
        GenericRenderMaterialLibrary materialLibrary,
        DeferredRenderResourceDisposalQueue disposalQueue)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        m_MaterialLibrary = materialLibrary ?? throw new ArgumentNullException(nameof(materialLibrary));
        m_DisposalQueue = disposalQueue ?? throw new ArgumentNullException(nameof(disposalQueue));
    }

    public string ProviderId => Id;

    public bool Supports(string assetType) =>
        assetType is "Mesh" or "Material" or "EnvironmentTexture";

    public RuntimePreparedAssetResult Prepare(RuntimeAssetResidencyKey key)
    {
        if (!Supports(key.AssetType))
        {
            return RuntimePreparedAssetResult.Failed(
                $"GenericRP does not prepare asset type '{key.AssetType}'.");
        }

        if (!m_Device.IsValid)
        {
            return RuntimePreparedAssetResult.Waiting(
                "GenericRP is waiting for the first valid RHI frame context.");
        }

        return key.AssetType switch
        {
            "Mesh" => PrepareMesh(key),
            "Material" => PrepareMaterial(key),
            "EnvironmentTexture" => PrepareEnvironment(key),
            _ => throw new UnreachableException()
        };
    }

    public void Release(RuntimeAssetResidencyKey key)
    {
        if (m_Meshes.Remove(key, out RHIStaticMeshResource? mesh))
        {
            m_EstimatedGpuBytes -= EstimateMeshBytes(mesh);
            m_DisposalQueue.Enqueue(mesh, m_LastSubmittedTicket);
        }

        if (m_Materials.Remove(key))
        {
            long bytes = GetCookedSize(key);
            m_EstimatedGpuBytes = Math.Max(0, m_EstimatedGpuBytes - bytes);
            m_MaterialLibrary.ReleasePrepared(key.Guid, m_LastSubmittedTicket);
        }

        if (m_Environments.Remove(key, out PreparedEnvironment? environment))
        {
            m_EstimatedGpuBytes = Math.Max(
                0,
                m_EstimatedGpuBytes - environment.EstimatedGpuBytes);
            m_DisposalQueue.Enqueue(environment.Lighting, m_LastSubmittedTicket);
            m_DisposalQueue.Enqueue(environment.Texture, m_LastSubmittedTicket);
        }
    }

    public RuntimePreparedAssetProviderMetrics GetMetrics() => new(
        m_Meshes.Count + m_Materials.Count + m_Environments.Count +
            m_MaterialLibrary.PreparedTextureCount,
        m_EstimatedGpuBytes + m_MaterialLibrary.EstimatedTextureGpuBytes,
        m_DisposalQueue.PendingCount,
        m_MaterialLibrary.PreparedMaterialCount);

    public ulong UpdateFrameContext(
        RHIDevice device,
        ulong deviceGeneration,
        ulong lastSubmittedTicket)
    {
        if (device.IsValid)
        {
            m_DisposalQueue.BindDevice(device, deviceGeneration);
            m_Device = device;
            m_DeviceGeneration = deviceGeneration;
        }
        m_LastSubmittedTicket = Math.Max(m_LastSubmittedTicket, lastSubmittedTicket);
        return m_LastSubmittedTicket;
    }

    public void UpdateSubmittedTicket(ulong submittedTicket)
    {
        m_LastSubmittedTicket = Math.Max(m_LastSubmittedTicket, submittedTicket);
    }

    public bool TryGetMesh(Guid meshGuid, out RHIStaticMeshResource mesh)
    {
        foreach ((RuntimeAssetResidencyKey key, RHIStaticMeshResource resource) in m_Meshes)
        {
            if (key.Guid == meshGuid && resource.IsValid)
            {
                mesh = resource;
                return true;
            }
        }

        mesh = null!;
        return false;
    }

    public bool TryGetEnvironment(
        Guid environmentGuid,
        out RHIEnvironmentTextureResource texture,
        out RHIEnvironmentLightingResource lighting)
    {
        foreach ((RuntimeAssetResidencyKey key, PreparedEnvironment environment) in m_Environments)
        {
            if (key.Guid == environmentGuid &&
                environment.Texture.IsValid &&
                environment.Lighting.IsValid)
            {
                texture = environment.Texture;
                lighting = environment.Lighting;
                return true;
            }
        }

        texture = null!;
        lighting = null!;
        return false;
    }

    public void InvalidateByAssetGuids(ReadOnlySpan<Guid> dirtyGuids)
    {
        if (dirtyGuids.IsEmpty) return;
        var meshKeys = new List<RuntimeAssetResidencyKey>();
        foreach (RuntimeAssetResidencyKey key in m_Meshes.Keys)
        {
            if (ContainsGuid(dirtyGuids, key.Guid)) meshKeys.Add(key);
        }
        foreach (RuntimeAssetResidencyKey key in meshKeys) Release(key);

        var environmentKeys = new List<RuntimeAssetResidencyKey>();
        foreach (RuntimeAssetResidencyKey key in m_Environments.Keys)
        {
            if (ContainsGuid(dirtyGuids, key.Guid)) environmentKeys.Add(key);
        }
        foreach (RuntimeAssetResidencyKey key in environmentKeys) Release(key);
    }

    public void ReleaseAll(bool disposeImmediately)
    {
        foreach (RHIStaticMeshResource mesh in m_Meshes.Values)
        {
            if (disposeImmediately) mesh.Dispose();
            else m_DisposalQueue.Enqueue(mesh, m_LastSubmittedTicket);
        }

        foreach (RuntimeAssetResidencyKey material in m_Materials)
        {
            m_MaterialLibrary.ReleasePrepared(
                material.Guid,
                m_LastSubmittedTicket,
                disposeImmediately);
        }

        foreach (PreparedEnvironment environment in m_Environments.Values)
        {
            if (disposeImmediately)
            {
                environment.Lighting.Dispose();
                environment.Texture.Dispose();
            }
            else
            {
                m_DisposalQueue.Enqueue(environment.Lighting, m_LastSubmittedTicket);
                m_DisposalQueue.Enqueue(environment.Texture, m_LastSubmittedTicket);
            }
        }

        m_Meshes.Clear();
        m_Materials.Clear();
        m_Environments.Clear();
        m_EstimatedGpuBytes = 0;
    }

    public void ReleaseDevice()
    {
        if (m_Meshes.Count != 0 ||
            m_Materials.Count != 0 ||
            m_Environments.Count != 0)
        {
            throw new InvalidOperationException(
                "GenericRP cannot release its RHI device while prepared resources remain.");
        }

        if (m_Device.IsValid)
        {
            m_DisposalQueue.ReleaseDevice(
                m_Device,
                m_DeviceGeneration,
                m_LastSubmittedTicket);
        }
        else if (m_DisposalQueue.PendingCount != 0)
        {
            throw new InvalidOperationException(
                $"GenericRP cannot release {m_DisposalQueue.PendingCount} deferred resources without a valid RHI device.");
        }

        m_Device = default;
        m_DeviceGeneration = 0;
        m_LastSubmittedTicket = 0;
    }

    public void ReleaseAllDeviceResources()
    {
        if (m_Device.IsValid && m_LastSubmittedTicket != 0)
        {
            m_Device.WaitQueueTicket(m_LastSubmittedTicket);
        }

        ReleaseAll(disposeImmediately: true);
        ReleaseDevice();
    }

    private RuntimePreparedAssetResult PrepareMesh(RuntimeAssetResidencyKey key)
    {
        if (!string.Equals(key.Variant, RuntimeAssetVariantPolicy.StaticMesh, StringComparison.Ordinal))
        {
            return RuntimePreparedAssetResult.Failed(
                $"GenericRP mesh variant '{key.Variant}' is unsupported.");
        }

        if (m_Meshes.TryGetValue(key, out RHIStaticMeshResource? existing))
        {
            return existing.IsValid
                ? RuntimePreparedAssetResult.Ready(EstimateMeshBytes(existing))
                : RuntimePreparedAssetResult.Failed(
                    $"Prepared mesh '{key}' no longer owns valid RHI buffers.");
        }

        MeshAsset asset = CreateMeshAsset(key);
        var mesh = new RHIStaticMeshResource(m_Device, m_AssetDatabase, asset);
        if (!mesh.IsValid)
        {
            mesh.Dispose();
            return RuntimePreparedAssetResult.Failed(
                $"GenericRP failed to prepare valid RHI buffers for mesh '{key}'.");
        }

        long bytes = EstimateMeshBytes(mesh);
        m_Meshes.Add(key, mesh);
        m_EstimatedGpuBytes += bytes;
        return RuntimePreparedAssetResult.Ready(bytes);
    }

    private RuntimePreparedAssetResult PrepareMaterial(RuntimeAssetResidencyKey key)
    {
        if (!string.Equals(key.Variant, RuntimeAssetVariantPolicy.Material, StringComparison.Ordinal))
        {
            return RuntimePreparedAssetResult.Failed(
                $"GenericRP material variant '{key.Variant}' is unsupported.");
        }

        if (m_Materials.Contains(key) &&
            m_MaterialLibrary.TryGetPreparedMaterial(key.Guid, out _))
        {
            return RuntimePreparedAssetResult.Ready(GetCookedSize(key));
        }

        uint materialId = m_MaterialLibrary.RegisterMaterial(
            new AssetRef<MaterialSourceAsset>(key.Guid, key.AssetType, key.PackageId));
        m_MaterialLibrary.EnsurePrepared(m_Device, materialId, m_LastSubmittedTicket);
        if (!m_MaterialLibrary.TryGetPreparedMaterial(key.Guid, out _))
        {
            return RuntimePreparedAssetResult.Failed(
                $"GenericRP material '{key}' did not become ready after preparation.");
        }

        long bytes = GetCookedSize(key);
        if (m_Materials.Add(key)) m_EstimatedGpuBytes += bytes;
        return RuntimePreparedAssetResult.Ready(bytes);
    }

    private RuntimePreparedAssetResult PrepareEnvironment(RuntimeAssetResidencyKey key)
    {
        if (!string.Equals(
                key.Variant,
                RuntimeAssetVariantPolicy.EnvironmentTexture,
                StringComparison.Ordinal))
        {
            return RuntimePreparedAssetResult.Failed(
                $"GenericRP environment variant '{key.Variant}' is unsupported.");
        }

        if (m_Environments.TryGetValue(key, out PreparedEnvironment? existing))
        {
            return existing.Texture.IsValid && existing.Lighting.IsValid
                ? RuntimePreparedAssetResult.Ready(existing.EstimatedGpuBytes)
                : RuntimePreparedAssetResult.Failed(
                    $"Prepared environment '{key}' no longer owns valid RHI resources.");
        }

        EnvironmentTextureAsset asset = EnvironmentTextureAssetLoader.Load(
            m_AssetDatabase,
            key.Guid);
        var texture = new RHIEnvironmentTextureResource(m_Device, m_AssetDatabase, asset);
        try
        {
            var lighting = new RHIEnvironmentLightingResource(m_Device, m_AssetDatabase, asset);
            long bytes = checked(
                GetCookedSize(key) +
                GetCookedSize(new RuntimeAssetResidencyKey(
                    key.Guid,
                    key.PackageId,
                    key.AssetType,
                    EnvironmentLightingAssetCooker.CookedVariant)));
            var environment = new PreparedEnvironment(texture, lighting, bytes);
            m_Environments.Add(key, environment);
            m_EstimatedGpuBytes += bytes;
            return RuntimePreparedAssetResult.Ready(bytes);
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    private MeshAsset CreateMeshAsset(RuntimeAssetResidencyKey key)
    {
        if (m_AssetDatabase.CanReadSourceAssets &&
            m_AssetDatabase.TryGetAsset(key.Guid, out AssetRecord? sourceAsset))
        {
            return new MeshAsset(
                key.Guid,
                Path.GetFileNameWithoutExtension(sourceAsset.SourcePath),
                MeshVariantKey.Default,
                ResolveMeshSourceFormat(sourceAsset.SourcePath));
        }

        return new MeshAsset(
            key.Guid,
            $"RuntimeMesh/{key.Guid:N}",
            MeshVariantKey.Default,
            MeshSourceFormat.ArisenTextMesh);
    }

    private long GetCookedSize(RuntimeAssetResidencyKey key)
    {
        return m_AssetDatabase.TryGetCookedArtifact(key.Guid, key.Variant, out CookedAssetRecord? artifact)
            ? Math.Max(0, artifact.SizeInBytes)
            : 0;
    }

    private static long EstimateMeshBytes(RHIStaticMeshResource mesh) =>
        checked((long)mesh.VertexCount * mesh.VertexStride + (long)mesh.IndexCount * sizeof(uint));

    private static MeshSourceFormat ResolveMeshSourceFormat(string sourcePath)
    {
        return Path.GetExtension(sourcePath).ToLowerInvariant() switch
        {
            ".armesh" => MeshSourceFormat.ArisenTextMesh,
            ".obj" => MeshSourceFormat.WavefrontObj,
            ".gltf" => MeshSourceFormat.GltfJson,
            ".glb" => MeshSourceFormat.GltfBinary,
            string extension => throw new NotSupportedException(
                $"GenericRP residency does not support mesh extension '{extension}'.")
        };
    }

    private static bool ContainsGuid(ReadOnlySpan<Guid> guids, Guid guid)
    {
        for (int index = 0; index < guids.Length; index++)
        {
            if (guids[index] == guid) return true;
        }

        return false;
    }

    private sealed record PreparedEnvironment(
        RHIEnvironmentTextureResource Texture,
        RHIEnvironmentLightingResource Lighting,
        long EstimatedGpuBytes);
}
