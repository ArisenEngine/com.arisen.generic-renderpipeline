using Arisen.Native.RHI;
using System.Diagnostics;
using System.IO;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.RHI;
using ArisenEngine.Rendering.Resources;
using ArisenEngine.Resources.Serialization;

namespace ArisenEngine.Rendering;

public sealed class GenericPreparedAssetProvider :
    IRuntimePreparedAssetProvider,
    IGenericRenderPipelinePreparedAssetSource
{
    public const string Id = "com.arisen.generic-renderpipeline.prepared-assets";

    private static readonly Action<IDisposable> s_DisposeEnvironmentResource =
        static resource => resource.Dispose();

    private readonly IAssetDatabase m_AssetDatabase;
    private readonly GenericRenderMaterialLibrary m_MaterialLibrary;
    private readonly DeferredRenderResourceDisposalQueue m_DisposalQueue;
    private readonly IRuntimeAssetResidencyService m_ResidencyService;
    private readonly GenericPreparedAssetProviderLifecycleState m_LifecycleState = new();
    private readonly Dictionary<RuntimeAssetResidencyKey, PreparedMeshEntry> m_Meshes = new();
    private readonly Dictionary<RuntimeAssetResidencyKey, PreparedMaterialEntry> m_Materials = new();
    private readonly HashSet<PreparedMeshEntry> m_RetiredMeshes =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<RuntimeAssetResidencyKey, PreparedEnvironment> m_Environments = new();
    private readonly Action<IDisposable> m_DeferEnvironmentResource;
    private RHIDevice m_Device;
    private ulong m_DeviceGeneration;
    private ulong m_LastSubmittedTicket;
    private ulong m_NextMeshPublicationGeneration;
    private int m_PreparedMeshLeaseCount;
    private int m_PreparedAssetThreadId;
    private long m_RetiredMeshGpuBytes;
    private long m_EstimatedGpuBytes;

    public GenericPreparedAssetProvider(
        IAssetDatabase assetDatabase,
        GenericRenderMaterialLibrary materialLibrary,
        DeferredRenderResourceDisposalQueue disposalQueue,
        IRuntimeAssetResidencyService residencyService)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        m_MaterialLibrary = materialLibrary ?? throw new ArgumentNullException(nameof(materialLibrary));
        m_DisposalQueue = disposalQueue ?? throw new ArgumentNullException(nameof(disposalQueue));
        m_ResidencyService = residencyService
            ?? throw new ArgumentNullException(nameof(residencyService));
        m_DeferEnvironmentResource = DeferEnvironmentResource;
        PublishMetricsSnapshot();
    }

    public string ProviderId => Id;

    public bool Supports(string assetType) =>
        assetType is "Mesh" or "Material" or "EnvironmentTexture";

    public RuntimePreparedAssetResult Prepare(RuntimeAssetResidencyKey key)
    {
        EnsurePreparedAssetThread();
        DrainPendingReleases();
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

        lock (m_LifecycleState.Gate)
        {
            if (m_LifecycleState.IsReleasePendingLocked(key))
            {
                return RuntimePreparedAssetResult.Waiting(
                    $"GenericRP is retiring the previous exact publication for '{key}'.");
            }

            try
            {
                return key.AssetType switch
                {
                    "Mesh" => PrepareMesh(key),
                    "Material" => PrepareMaterial(key),
                    "EnvironmentTexture" => PrepareEnvironment(key),
                    _ => throw new UnreachableException()
                };
            }
            finally
            {
                PublishMetricsSnapshot();
            }
        }
    }

    public void Release(RuntimeAssetResidencyKey key) =>
        m_LifecycleState.RequestRelease(key);

    public RuntimePreparedAssetProviderMetrics GetMetrics() =>
        m_LifecycleState.ReadMetrics();

    public ulong UpdateFrameContext(
        RHIDevice device,
        ulong deviceGeneration,
        ulong lastSubmittedTicket)
    {
        BindPreparedAssetThread();
        m_LastSubmittedTicket = Math.Max(m_LastSubmittedTicket, lastSubmittedTicket);
        DrainPendingReleases();
        if (device.IsValid)
        {
            m_DisposalQueue.BindDevice(device, deviceGeneration);
            m_Device = device;
            m_DeviceGeneration = deviceGeneration;
        }
        PublishMetricsSnapshot();
        return m_LastSubmittedTicket;
    }

    public void UpdateSubmittedTicket(ulong submittedTicket)
    {
        EnsurePreparedAssetThread();
        m_LastSubmittedTicket = Math.Max(m_LastSubmittedTicket, submittedTicket);
        DrainPendingReleases();
        PublishMetricsSnapshot();
    }

    public bool TryAcquirePreparedMesh(
        in RuntimeAssetResidencyKey key,
        out IGenericRenderPipelinePreparedMeshLease lease)
    {
        BindPreparedAssetThread();
        DrainPendingReleases();
        lock (m_LifecycleState.Gate)
        {
            if (!m_LifecycleState.IsReleasePendingLocked(key) &&
                m_Meshes.TryGetValue(key, out PreparedMeshEntry? entry) &&
                entry.IsCurrent &&
                entry.DeviceGeneration == m_DeviceGeneration &&
                entry.Resource.IsValid)
            {
                entry.LeaseCount = checked(entry.LeaseCount + 1);
                m_PreparedMeshLeaseCount = checked(m_PreparedMeshLeaseCount + 1);
                lease = new PreparedMeshLease(this, entry);
                return true;
            }
        }

        lease = null!;
        return false;
    }

    public bool TryAcquirePreparedMaterial(
        in RuntimeAssetResidencyKey key,
        out IGenericRenderPipelinePreparedMaterialLease lease)
    {
        BindPreparedAssetThread();
        DrainPendingReleases();
        lock (m_LifecycleState.Gate)
        {
            if (!m_LifecycleState.IsReleasePendingLocked(key) &&
                m_Materials.TryGetValue(key, out PreparedMaterialEntry? entry) &&
                entry.DeviceGeneration == m_DeviceGeneration &&
                m_MaterialLibrary.TryAcquirePreparedMaterial(
                    key.Guid,
                    out GenericRenderMaterialLibrary.PreparedMaterialLease materialLease))
            {
                if (ReferenceEquals(entry.Resource, materialLease.Resource) &&
                    entry.PublicationGeneration == materialLease.PublicationGeneration)
                {
                    lease = new PreparedMaterialLease(this, entry, materialLease);
                    return true;
                }

                materialLease.Dispose();
            }
        }

        lease = null!;
        return false;
    }

    internal bool TryGetMesh(Guid meshGuid, out RHIStaticMeshResource mesh)
    {
        EnsurePreparedAssetThread();
        DrainPendingReleases();
        lock (m_LifecycleState.Gate)
        {
            foreach ((RuntimeAssetResidencyKey key, PreparedMeshEntry entry) in m_Meshes)
            {
                if (!m_LifecycleState.IsReleasePendingLocked(key) &&
                    key.Guid == meshGuid &&
                    entry.IsCurrent &&
                    entry.Resource.IsValid)
                {
                    mesh = entry.Resource;
                    return true;
                }
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
        EnsurePreparedAssetThread();
        lock (m_LifecycleState.Gate)
        {
            foreach ((RuntimeAssetResidencyKey key, PreparedEnvironment environment) in m_Environments)
            {
                if (!m_LifecycleState.IsReleasePendingLocked(key) &&
                    key.Guid == environmentGuid &&
                    environment.IsCurrent &&
                    environment.Texture.IsValid &&
                    environment.Lighting.IsValid)
                {
                    texture = environment.Texture;
                    lighting = environment.Lighting;
                    return true;
                }
            }
        }

        texture = null!;
        lighting = null!;
        return false;
    }

    public void InvalidateByAssetGuids(ReadOnlySpan<Guid> dirtyGuids)
    {
        EnsurePreparedAssetThread();
        DrainPendingReleases();
        if (dirtyGuids.IsEmpty) return;

        try
        {
            m_MaterialLibrary.InvalidateByAssetGuids(dirtyGuids, m_LastSubmittedTicket);
            if (!m_ResidencyService.IsPreparedProviderRegistered(this))
            {
                throw new InvalidOperationException(
                    "GenericRP cannot invalidate prepared assets after its residency provider was unregistered.");
            }
            if (!m_ResidencyService.InvalidatePreparedProvider(
                    Id,
                    "Generic RP prepared resources were invalidated by an asset dependency change."))
            {
                throw new InvalidOperationException(
                    "GenericRP residency provider disappeared during asset invalidation.");
            }

            DrainPendingReleases();
        }
        finally
        {
            PublishMetricsSnapshot();
        }
    }

    public void ReleaseAll(bool disposeImmediately)
    {
        EnsurePreparedAssetThread();
        lock (m_LifecycleState.Gate)
        {
            DrainPendingReleases();
            try
            {
                if (disposeImmediately &&
                    (m_PreparedMeshLeaseCount != 0 ||
                     m_MaterialLibrary.PreparedPublicationLeaseCount != 0))
                {
                    throw new InvalidOperationException(
                        $"GenericRP cannot force device-resource release while " +
                        $"{m_PreparedMeshLeaseCount} mesh and " +
                        $"{m_MaterialLibrary.PreparedPublicationLeaseCount} material " +
                        "publication leases remain active.");
                }

                RuntimeAssetResidencyKey[] meshKeys = m_Meshes.Keys.ToArray();
                for (int index = 0; index < meshKeys.Length; index++)
                {
                    RuntimeAssetResidencyKey key = meshKeys[index];
                    PreparedMeshEntry mesh = m_Meshes[key];
                    long bytes = EstimateMeshBytes(mesh.Resource);
                    RetirePreparedMesh(mesh, disposeImmediately);
                    m_Meshes.Remove(key);
                    m_EstimatedGpuBytes = Math.Max(0, m_EstimatedGpuBytes - bytes);
                }

                RuntimeAssetResidencyKey[] materialKeys = m_Materials.Keys.ToArray();
                for (int index = 0; index < materialKeys.Length; index++)
                {
                    RuntimeAssetResidencyKey key = materialKeys[index];
                    PreparedMaterialEntry material = m_Materials[key];
                    m_MaterialLibrary.ReleasePrepared(
                        material.Key.Guid,
                        material.Resource,
                        material.PublicationGeneration,
                        m_LastSubmittedTicket,
                        disposeImmediately);
                    RemovePreparedMaterialMapping(key);
                }

                RuntimeAssetResidencyKey[] environmentKeys =
                    m_Environments.Keys.ToArray();
                Action<IDisposable> retireEnvironmentResource = disposeImmediately
                    ? s_DisposeEnvironmentResource
                    : m_DeferEnvironmentResource;
                for (int index = 0; index < environmentKeys.Length; index++)
                {
                    RuntimeAssetResidencyKey key = environmentKeys[index];
                    PreparedEnvironment environment = m_Environments[key];
                    environment.TransferRetirementOwnership(retireEnvironmentResource);
                    RemovePreparedEnvironment(key, environment);
                }

                m_EstimatedGpuBytes = 0;
            }
            finally
            {
                PublishMetricsSnapshot();
            }
        }
    }

    public void ReleaseDevice()
    {
        EnsurePreparedAssetThread();
        lock (m_LifecycleState.Gate)
        {
            DrainPendingReleases();
            if (m_LifecycleState.PendingReleaseCount != 0 ||
                m_Meshes.Count != 0 ||
                m_Materials.Count != 0 ||
                m_Environments.Count != 0 ||
                m_RetiredMeshes.Count != 0 ||
                m_PreparedMeshLeaseCount != 0 ||
                m_MaterialLibrary.PreparedPublicationLeaseCount != 0)
            {
                throw new InvalidOperationException(
                    "GenericRP cannot release its RHI device while prepared resources, " +
                    "pending lifecycle releases, or exact-publication leases remain.");
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
            PublishMetricsSnapshot();
            Volatile.Write(ref m_PreparedAssetThreadId, 0);
        }
    }

    public void ReleaseAllDeviceResources()
    {
        EnsurePreparedAssetThread();
        lock (m_LifecycleState.Gate)
        {
            if (m_Device.IsValid && m_LastSubmittedTicket != 0)
            {
                m_Device.WaitQueueTicket(m_LastSubmittedTicket);
            }

            ReleaseAll(disposeImmediately: true);
            ReleaseDevice();
        }
    }

    private RuntimePreparedAssetResult PrepareMesh(RuntimeAssetResidencyKey key)
    {
        if (!string.Equals(key.Variant, RuntimeAssetVariantPolicy.StaticMesh, StringComparison.Ordinal))
        {
            return RuntimePreparedAssetResult.Failed(
                $"GenericRP mesh variant '{key.Variant}' is unsupported.");
        }

        foreach (RuntimeAssetResidencyKey preparedKey in m_Meshes.Keys)
        {
            if (preparedKey.Guid == key.Guid && preparedKey != key)
            {
                return RuntimePreparedAssetResult.Failed(
                    $"GenericRP refuses to alias mesh GUID '{key.Guid:D}' across exact " +
                    $"residency keys '{preparedKey}' and '{key}'.");
            }
        }

        if (m_Meshes.TryGetValue(key, out PreparedMeshEntry? existing))
        {
            return existing.IsCurrent && existing.Resource.IsValid
                ? RuntimePreparedAssetResult.Ready(EstimateMeshBytes(existing.Resource))
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
        m_Meshes.Add(
            key,
            new PreparedMeshEntry(
                key,
                mesh,
                m_DeviceGeneration,
                NextMeshPublicationGeneration()));
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

        foreach (RuntimeAssetResidencyKey preparedKey in m_Materials.Keys)
        {
            if (preparedKey.Guid == key.Guid && preparedKey != key)
            {
                return RuntimePreparedAssetResult.Failed(
                    $"GenericRP refuses to alias material GUID '{key.Guid:D}' across exact " +
                    $"residency keys '{preparedKey}' and '{key}'.");
            }
        }

        if (m_Materials.TryGetValue(key, out PreparedMaterialEntry? existing) &&
            existing.DeviceGeneration == m_DeviceGeneration &&
            m_MaterialLibrary.TryAcquirePreparedMaterial(
                key.Guid,
                out GenericRenderMaterialLibrary.PreparedMaterialLease existingLease))
        {
            try
            {
                if (ReferenceEquals(existing.Resource, existingLease.Resource) &&
                    existing.PublicationGeneration == existingLease.PublicationGeneration)
                {
                    return RuntimePreparedAssetResult.Ready(GetCookedSize(key));
                }
            }
            finally
            {
                existingLease.Dispose();
            }
        }

        RemovePreparedMaterialMapping(key);

        uint materialId = m_MaterialLibrary.RegisterMaterial(
            new AssetRef<MaterialSourceAsset>(key.Guid, key.AssetType, key.PackageId));
        Guid staleMaterialGuid = m_MaterialLibrary.EnsurePrepared(
            m_Device,
            materialId,
            m_LastSubmittedTicket);
        if (staleMaterialGuid != Guid.Empty)
        {
            return RuntimePreparedAssetResult.Waiting(
                $"GenericRP material '{key}' changed after residency setup began; " +
                "the setup coordinator must invalidate its current publication before replacement.");
        }
        if (!m_MaterialLibrary.TryAcquirePreparedMaterial(
                key.Guid,
                out GenericRenderMaterialLibrary.PreparedMaterialLease materialLease))
        {
            return RuntimePreparedAssetResult.Failed(
                $"GenericRP material '{key}' did not become ready after preparation.");
        }

        try
        {
            long bytes = GetCookedSize(key);
            m_MaterialLibrary.SetPreparedPublicationEstimatedGpuBytes(
                materialLease.Resource,
                materialLease.PublicationGeneration,
                bytes);
            m_Materials.Add(
                key,
                new PreparedMaterialEntry(
                    key,
                    materialLease.Resource,
                    m_DeviceGeneration,
                    materialLease.PublicationGeneration,
                    bytes));
            m_EstimatedGpuBytes += bytes;
            return RuntimePreparedAssetResult.Ready(bytes);
        }
        finally
        {
            materialLease.Dispose();
        }
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

        foreach (RuntimeAssetResidencyKey preparedKey in m_Environments.Keys)
        {
            if (preparedKey.Guid == key.Guid && preparedKey != key)
            {
                return RuntimePreparedAssetResult.Failed(
                    $"GenericRP refuses to alias environment GUID '{key.Guid:D}' across " +
                    $"exact residency keys '{preparedKey}' and '{key}'.");
            }
        }

        if (m_Environments.TryGetValue(key, out PreparedEnvironment? existing))
        {
            if (!existing.IsCurrent)
            {
                return RuntimePreparedAssetResult.Waiting(
                    $"Prepared environment '{key}' is still retiring its previous publication.");
            }

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

    private void DrainPendingReleases()
    {
        EnsurePreparedAssetThread();
        while (m_LifecycleState.TryPeekPendingRelease(out RuntimeAssetResidencyKey key))
        {
            try
            {
                ReleasePhysicalResources(key);
                RuntimePreparedAssetProviderMetrics physicalMetrics =
                    CapturePhysicalMetrics();
                m_LifecycleState.CompletePendingRelease(key, physicalMetrics);
            }
            catch
            {
                PublishMetricsSnapshot();
                throw;
            }
        }
    }

    private void ReleasePhysicalResources(RuntimeAssetResidencyKey key)
    {
        if (m_Meshes.TryGetValue(key, out PreparedMeshEntry? mesh))
        {
            long bytes = EstimateMeshBytes(mesh.Resource);
            RetirePreparedMesh(mesh, disposeImmediately: false);
            m_Meshes.Remove(key);
            m_EstimatedGpuBytes = Math.Max(0, m_EstimatedGpuBytes - bytes);
        }

        if (m_Materials.TryGetValue(key, out PreparedMaterialEntry? material))
        {
            m_MaterialLibrary.ReleasePrepared(
                material.Key.Guid,
                material.Resource,
                material.PublicationGeneration,
                m_LastSubmittedTicket);
            RemovePreparedMaterialMapping(key);
        }

        if (m_Environments.TryGetValue(key, out PreparedEnvironment? environment))
        {
            environment.TransferRetirementOwnership(m_DeferEnvironmentResource);
            RemovePreparedEnvironment(key, environment);
        }
    }

    private RuntimePreparedAssetProviderMetrics CapturePhysicalMetrics() =>
        new(
            m_Meshes.Count + m_Materials.Count + m_Environments.Count +
                m_MaterialLibrary.PreparedTextureCount,
            m_EstimatedGpuBytes + m_RetiredMeshGpuBytes +
                m_MaterialLibrary.RetiredPreparedPublicationGpuBytes +
                m_MaterialLibrary.EstimatedTextureGpuBytes,
            m_DisposalQueue.PendingCount + m_RetiredMeshes.Count +
                m_MaterialLibrary.RetiredPreparedPublicationCount,
            m_MaterialLibrary.PreparedMaterialCount +
                m_MaterialLibrary.RetiredPreparedPublicationCount);

    private void PublishMetricsSnapshot() =>
        m_LifecycleState.PublishPhysicalMetrics(CapturePhysicalMetrics());

    private void RemovePreparedMaterialMapping(RuntimeAssetResidencyKey key)
    {
        if (!m_Materials.Remove(key, out PreparedMaterialEntry? entry))
        {
            return;
        }

        m_EstimatedGpuBytes = Math.Max(
            0,
            m_EstimatedGpuBytes - entry.EstimatedGpuBytes);
    }

    private void RemovePreparedEnvironment(
        RuntimeAssetResidencyKey key,
        PreparedEnvironment environment)
    {
        if (!environment.RetirementComplete ||
            !m_Environments.TryGetValue(key, out PreparedEnvironment? current) ||
            !ReferenceEquals(current, environment) ||
            !m_Environments.Remove(key))
        {
            throw new InvalidOperationException(
                $"Prepared environment retirement ownership was lost for '{key}'.");
        }

        m_EstimatedGpuBytes = Math.Max(
            0,
            m_EstimatedGpuBytes - environment.EstimatedGpuBytes);
    }

    private void DeferEnvironmentResource(IDisposable resource) =>
        m_DisposalQueue.Enqueue(resource, m_LastSubmittedTicket);

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

    private void BindPreparedAssetThread()
    {
        int currentThreadId = Environment.CurrentManagedThreadId;
        int ownerThreadId = Volatile.Read(ref m_PreparedAssetThreadId);
        if (ownerThreadId == 0)
        {
            ownerThreadId = Interlocked.CompareExchange(
                ref m_PreparedAssetThreadId,
                currentThreadId,
                comparand: 0);
            if (ownerThreadId == 0)
            {
                ownerThreadId = currentThreadId;
            }
        }

        if (ownerThreadId != currentThreadId)
        {
            throw new InvalidOperationException(
                $"GenericRP prepared assets are setup-thread affine to thread " +
                $"{ownerThreadId}; thread {currentThreadId} attempted access.");
        }
    }

    private void EnsurePreparedAssetThread()
    {
        int ownerThreadId = Volatile.Read(ref m_PreparedAssetThreadId);
        if (ownerThreadId != 0 && ownerThreadId != Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException(
                $"GenericRP prepared assets are setup-thread affine to thread " +
                $"{ownerThreadId}; thread {Environment.CurrentManagedThreadId} attempted access.");
        }
    }

    private ulong NextMeshPublicationGeneration()
    {
        m_NextMeshPublicationGeneration = checked(m_NextMeshPublicationGeneration + 1);
        return m_NextMeshPublicationGeneration;
    }

    private void RetirePreparedMesh(
        PreparedMeshEntry entry,
        bool disposeImmediately)
    {
        if (!entry.IsCurrent || entry.Released)
        {
            throw new InvalidOperationException(
                $"Prepared mesh publication {entry.PublicationGeneration} for " +
                $"'{entry.Key}' is already retired.");
        }

        if (entry.LeaseCount == 0)
        {
            entry.IsCurrent = false;
            try
            {
                DisposePreparedMesh(
                    entry.Resource,
                    disposeImmediately,
                    m_LastSubmittedTicket);
            }
            catch
            {
                entry.IsCurrent = true;
                throw;
            }

            entry.Released = true;
            return;
        }

        if (disposeImmediately)
        {
            throw new InvalidOperationException(
                $"Cannot immediately release prepared mesh publication " +
                $"{entry.PublicationGeneration} for '{entry.Key}' while " +
                $"{entry.LeaseCount} leases remain active.");
        }

        entry.IsCurrent = false;
        entry.RetirementTicket = m_LastSubmittedTicket;
        try
        {
            if (!m_RetiredMeshes.Add(entry))
            {
                throw new InvalidOperationException(
                    "Prepared mesh publication was already tracked as retired.");
            }
            m_RetiredMeshGpuBytes = checked(
                m_RetiredMeshGpuBytes + EstimateMeshBytes(entry.Resource));
        }
        catch
        {
            m_RetiredMeshes.Remove(entry);
            entry.RetirementTicket = 0;
            entry.IsCurrent = true;
            throw;
        }
    }

    private void ReleasePreparedMeshLease(PreparedMeshEntry entry)
    {
        EnsurePreparedAssetThread();
        if (entry.LeaseCount <= 0 || m_PreparedMeshLeaseCount <= 0 || entry.Released)
        {
            throw new InvalidOperationException(
                "Prepared mesh lease ownership is invalid.");
        }

        if (entry.LeaseCount == 1 && !entry.IsCurrent)
        {
            if (!m_RetiredMeshes.Contains(entry))
            {
                throw new InvalidOperationException(
                    "Retired prepared mesh publication was not tracked by its provider.");
            }

            ulong retirementTicket = Math.Max(
                entry.RetirementTicket,
                m_LastSubmittedTicket);
            DisposePreparedMesh(
                entry.Resource,
                disposeImmediately: false,
                submittedTicket: retirementTicket);
            if (!m_RetiredMeshes.Remove(entry))
            {
                throw new InvalidOperationException(
                    "Retired prepared mesh publication was not tracked by its provider.");
            }

            m_RetiredMeshGpuBytes = Math.Max(
                0,
                m_RetiredMeshGpuBytes - EstimateMeshBytes(entry.Resource));
            entry.Released = true;
        }

        entry.LeaseCount--;
        m_PreparedMeshLeaseCount--;
        PublishMetricsSnapshot();
    }

    private bool IsPreparedMeshCurrent(PreparedMeshEntry entry)
    {
        EnsurePreparedAssetThread();
        lock (m_LifecycleState.Gate)
        {
            return !m_LifecycleState.IsReleasePendingLocked(entry.Key) &&
                entry.IsCurrent &&
                !entry.Released &&
                entry.DeviceGeneration == m_DeviceGeneration &&
                m_Meshes.TryGetValue(entry.Key, out PreparedMeshEntry? current) &&
                ReferenceEquals(current, entry);
        }
    }

    private bool IsPreparedMaterialCurrent(
        PreparedMaterialEntry entry,
        GenericRenderMaterialLibrary.PreparedMaterialLease lease)
    {
        EnsurePreparedAssetThread();
        lock (m_LifecycleState.Gate)
        {
            return !m_LifecycleState.IsReleasePendingLocked(entry.Key) &&
                entry.DeviceGeneration == m_DeviceGeneration &&
                m_Materials.TryGetValue(entry.Key, out PreparedMaterialEntry? current) &&
                ReferenceEquals(current, entry) &&
                ReferenceEquals(entry.Resource, lease.Resource) &&
                entry.PublicationGeneration == lease.PublicationGeneration &&
                lease.IsCurrent;
        }
    }

    private void ReleasePreparedMaterialLease(
        GenericRenderMaterialLibrary.PreparedMaterialLease lease)
    {
        EnsurePreparedAssetThread();
        lease.Dispose();
        PublishMetricsSnapshot();
    }

    private void DisposePreparedMesh(
        RHIStaticMeshResource resource,
        bool disposeImmediately,
        ulong submittedTicket)
    {
        if (disposeImmediately)
        {
            resource.Dispose();
        }
        else
        {
            m_DisposalQueue.Enqueue(resource, submittedTicket);
        }
    }

    private sealed class PreparedMeshEntry
    {
        public PreparedMeshEntry(
            RuntimeAssetResidencyKey key,
            RHIStaticMeshResource resource,
            ulong deviceGeneration,
            ulong publicationGeneration)
        {
            Key = key;
            Resource = resource;
            DeviceGeneration = deviceGeneration;
            PublicationGeneration = publicationGeneration;
        }

        public RuntimeAssetResidencyKey Key { get; }
        public RHIStaticMeshResource Resource { get; }
        public ulong DeviceGeneration { get; }
        public ulong PublicationGeneration { get; }
        public int LeaseCount { get; set; }
        public bool IsCurrent { get; set; } = true;
        public bool Released { get; set; }
        public ulong RetirementTicket { get; set; }
    }

    private sealed class PreparedMaterialEntry
    {
        public PreparedMaterialEntry(
            RuntimeAssetResidencyKey key,
            RHIMaterialResource resource,
            ulong deviceGeneration,
            ulong publicationGeneration,
            long estimatedGpuBytes)
        {
            Key = key;
            Resource = resource;
            DeviceGeneration = deviceGeneration;
            PublicationGeneration = publicationGeneration;
            EstimatedGpuBytes = estimatedGpuBytes;
        }

        public RuntimeAssetResidencyKey Key { get; }
        public RHIMaterialResource Resource { get; }
        public ulong DeviceGeneration { get; }
        public ulong PublicationGeneration { get; }
        public long EstimatedGpuBytes { get; }
    }

    private sealed class PreparedMeshLease : IGenericRenderPipelinePreparedMeshLease
    {
        private readonly object m_DisposeGate = new();
        private GenericPreparedAssetProvider? m_Owner;
        private readonly PreparedMeshEntry m_Entry;

        public PreparedMeshLease(
            GenericPreparedAssetProvider owner,
            PreparedMeshEntry entry)
        {
            m_Owner = owner;
            m_Entry = entry;
        }

        public RuntimeAssetResidencyKey Key => m_Entry.Key;
        public ulong DeviceGeneration => m_Entry.DeviceGeneration;
        public ulong PublicationGeneration => m_Entry.PublicationGeneration;
        public RHIStaticMeshResource Resource => m_Entry.Resource;
        public bool IsCurrent =>
            Volatile.Read(ref m_Owner)?.IsPreparedMeshCurrent(m_Entry) == true;

        public void Dispose()
        {
            lock (m_DisposeGate)
            {
                GenericPreparedAssetProvider? owner = m_Owner;
                if (owner == null)
                {
                    return;
                }

                owner.ReleasePreparedMeshLease(m_Entry);
                m_Owner = null;
            }
        }
    }

    private sealed class PreparedMaterialLease :
        IGenericRenderPipelinePreparedMaterialLease
    {
        private readonly object m_DisposeGate = new();
        private GenericPreparedAssetProvider? m_Owner;
        private GenericRenderMaterialLibrary.PreparedMaterialLease? m_Lease;
        private readonly PreparedMaterialEntry m_Entry;

        public PreparedMaterialLease(
            GenericPreparedAssetProvider owner,
            PreparedMaterialEntry entry,
            GenericRenderMaterialLibrary.PreparedMaterialLease lease)
        {
            m_Owner = owner;
            m_Entry = entry;
            m_Lease = lease;
        }

        public RuntimeAssetResidencyKey Key => m_Entry.Key;
        public ulong DeviceGeneration => m_Entry.DeviceGeneration;
        public ulong PublicationGeneration => m_Entry.PublicationGeneration;
        public RHIMaterialResource Resource => m_Entry.Resource;
        public bool IsCurrent
        {
            get
            {
                GenericPreparedAssetProvider? owner = Volatile.Read(ref m_Owner);
                GenericRenderMaterialLibrary.PreparedMaterialLease? lease =
                    Volatile.Read(ref m_Lease);
                return lease != null &&
                    owner?.IsPreparedMaterialCurrent(m_Entry, lease) == true;
            }
        }

        public void Dispose()
        {
            lock (m_DisposeGate)
            {
                GenericRenderMaterialLibrary.PreparedMaterialLease? lease = m_Lease;
                if (lease == null)
                {
                    return;
                }

                GenericPreparedAssetProvider owner = m_Owner
                    ?? throw new InvalidOperationException(
                        "Prepared material lease lost its provider ownership.");
                owner.ReleasePreparedMaterialLease(lease);
                m_Lease = null;
                m_Owner = null;
            }
        }
    }

    private sealed class PreparedEnvironment
    {
        private readonly GenericPreparedEnvironmentRetirementState m_Retirement;

        public PreparedEnvironment(
            RHIEnvironmentTextureResource texture,
            RHIEnvironmentLightingResource lighting,
            long estimatedGpuBytes)
        {
            Texture = texture;
            Lighting = lighting;
            EstimatedGpuBytes = estimatedGpuBytes;
            m_Retirement = new GenericPreparedEnvironmentRetirementState(
                lighting,
                texture);
        }

        public RHIEnvironmentTextureResource Texture { get; }
        public RHIEnvironmentLightingResource Lighting { get; }
        public long EstimatedGpuBytes { get; }
        public bool IsCurrent => m_Retirement.IsCurrent;
        public bool RetirementComplete => m_Retirement.IsComplete;

        public void TransferRetirementOwnership(Action<IDisposable> transfer) =>
            m_Retirement.TransferOwnership(transfer);
    }
}
