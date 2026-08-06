using Arisen.Native.RHI;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.RHI;
using ArisenEngine.Rendering.Resources;

namespace ArisenEngine.Rendering;

public sealed class GenericRenderMaterialLibrary : IRenderMaterialLibrary, IDisposable
{
    private const uint FirstMaterialID = 1;

    private readonly IAssetDatabase m_AssetDatabase;
    private readonly Dictionary<Guid, uint> m_MaterialIDs = new();
    private readonly List<MaterialEntry> m_Materials = new();
    private readonly DeferredRenderResourceDisposalQueue m_DisposalQueue;
    private readonly SharedRHITexture2DResourceCache m_TextureCache = new();
    private readonly Dictionary<RHIMaterialResource, PreparedMaterialOwnership>
        m_PreparedOwnership = new(ReferenceEqualityComparer.Instance);
    private ulong m_NextPreparedPublicationGeneration;
    private int m_PreparedPublicationLeaseCount;
    private bool m_Disposed;

    public uint DefaultMaterialID { get; private set; }

    public int MaterialCount
    {
        get
        {
            ThrowIfDisposed();
            return m_Materials.Count;
        }
    }

    public int PreparedMaterialCount
    {
        get
        {
            ThrowIfDisposed();

            int preparedCount = 0;
            for (int i = 0; i < m_Materials.Count; i++)
            {
                if (m_Materials[i].Resource is { IsValid: true })
                {
                    preparedCount++;
                }
            }

            return preparedCount;
        }
    }

    public int PreparedTextureCount => m_TextureCache.ResourceCount;

    public long EstimatedTextureGpuBytes => m_TextureCache.EstimatedGpuBytes;

    internal int PreparedPublicationLeaseCount => m_PreparedPublicationLeaseCount;

    internal int RetiredPreparedPublicationCount
    {
        get
        {
            int count = 0;
            foreach (PreparedMaterialOwnership ownership in m_PreparedOwnership.Values)
            {
                if (!ownership.IsCurrent)
                {
                    count++;
                }
            }

            return count;
        }
    }

    internal long RetiredPreparedPublicationGpuBytes
    {
        get
        {
            long bytes = 0;
            foreach (PreparedMaterialOwnership ownership in m_PreparedOwnership.Values)
            {
                if (!ownership.IsCurrent)
                {
                    bytes = checked(bytes + ownership.EstimatedGpuBytes);
                }
            }

            return bytes;
        }
    }

    public IRHITexture2DResourceCache TextureResourceCache => m_TextureCache;

    public GenericRenderMaterialLibrary(
        IAssetDatabase assetDatabase,
        DeferredRenderResourceDisposalQueue disposalQueue)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        m_DisposalQueue = disposalQueue ?? throw new ArgumentNullException(nameof(disposalQueue));
    }

    public uint RegisterDefaultMaterial(Guid materialGuid)
    {
        ThrowIfDisposed();

        DefaultMaterialID = RegisterMaterial(materialGuid);
        return DefaultMaterialID;
    }

    public uint RegisterDefaultMaterial(AssetRef<MaterialSourceAsset> materialRef)
    {
        ThrowIfDisposed();

        DefaultMaterialID = RegisterMaterial(materialRef);
        return DefaultMaterialID;
    }

    public uint RegisterMaterial(Guid materialGuid)
    {
        ThrowIfDisposed();

        if (materialGuid == Guid.Empty)
        {
            throw new ArgumentException("[GenericRenderMaterialLibrary] Material GUID cannot be empty.", nameof(materialGuid));
        }

        if (m_MaterialIDs.TryGetValue(materialGuid, out var existingId))
        {
            return existingId;
        }

        var materialId = checked(FirstMaterialID + (uint)m_Materials.Count);
        m_MaterialIDs.Add(materialGuid, materialId);
        m_Materials.Add(new MaterialEntry(materialGuid, materialId));

        if (DefaultMaterialID == 0)
        {
            DefaultMaterialID = materialId;
        }

        Logger.Log($"[GenericRenderMaterialLibrary] Registered material | Guid: {materialGuid} | ID: {materialId}");
        return materialId;
    }

    public uint RegisterMaterial(AssetRef<MaterialSourceAsset> materialRef)
    {
        if (materialRef.AssetType.Length > 0 &&
            !string.Equals(materialRef.AssetType, "Material", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"[GenericRenderMaterialLibrary] AssetRef '{materialRef}' is not a Material asset ref.",
                nameof(materialRef));
        }

        return RegisterMaterial(materialRef.Guid);
    }

    public bool TryGetMaterialID(Guid materialGuid, out uint materialId)
    {
        ThrowIfDisposed();

        return m_MaterialIDs.TryGetValue(materialGuid, out materialId);
    }

    public bool TryGetMaterialID(AssetRef<MaterialSourceAsset> materialRef, out uint materialId)
    {
        return TryGetMaterialID(materialRef.Guid, out materialId);
    }

    public RHIMaterialResource GetPreparedMaterial(uint materialId)
    {
        ThrowIfDisposed();

        for (int i = 0; i < m_Materials.Count; i++)
        {
            var entry = m_Materials[i];
            if (entry.MaterialID == materialId)
            {
                return entry.Resource ??
                    throw new InvalidOperationException($"[GenericRenderMaterialLibrary] Material ID {materialId} has not been prepared.");
            }
        }

        throw new InvalidOperationException($"[GenericRenderMaterialLibrary] Material ID {materialId} is not registered.");
    }

    public RenderQueueInfo GetRenderQueue(uint materialId)
    {
        ThrowIfDisposed();

        if (materialId < FirstMaterialID)
        {
            return RenderQueueInfo.Opaque;
        }

        uint materialIndex = materialId - FirstMaterialID;
        if (materialIndex >= (uint)m_Materials.Count)
        {
            return RenderQueueInfo.Opaque;
        }

        var entry = m_Materials[(int)materialIndex];
        if (entry.MaterialID != materialId || entry.Resource is not { IsValid: true } resource)
        {
            return RenderQueueInfo.Opaque;
        }

        return RenderQueuePolicy.Resolve(
            resource.RenderState,
            resource.Asset.Shader.VariantKeywords);
    }

    public void InvalidateByAssetGuids(ReadOnlySpan<Guid> dirtyGuids, ulong submittedTicket)
    {
        ThrowIfDisposed();

        if (dirtyGuids.IsEmpty)
        {
            return;
        }

        for (int i = 0; i < m_Materials.Count; i++)
        {
            var entry = m_Materials[i];
            if (entry.Resource == null ||
                !MaterialDependsOnDirtyGuid(entry, dirtyGuids))
            {
                continue;
            }

            Logger.Log($"[GenericRenderMaterialLibrary] Asset change invalidated material ID {entry.MaterialID}; releasing prepared material.");
            RetirePreparedResource(
                entry.Resource,
                submittedTicket,
                disposeImmediately: false);
            entry.Resource = null;
            m_Materials[i] = entry;
        }
    }

    public Guid[] EnsurePrepared(RHIDevice device, ulong submittedTicket)
    {
        ThrowIfDisposed();

        if (!device.IsValid)
        {
            throw new ArgumentException("[GenericRenderMaterialLibrary] Cannot prepare materials with an invalid RHI device.", nameof(device));
        }

        List<Guid>? staleGuids = null;
        for (int i = 0; i < m_Materials.Count; i++)
        {
            Guid staleGuid = EnsurePreparedAtIndex(device, submittedTicket, i);
            if (staleGuid != Guid.Empty)
            {
                (staleGuids ??= new List<Guid>()).Add(staleGuid);
            }
        }

        return staleGuids?.ToArray() ?? Array.Empty<Guid>();
    }

    public Guid EnsurePrepared(RHIDevice device, uint materialId, ulong submittedTicket)
    {
        ThrowIfDisposed();
        if (!device.IsValid)
        {
            throw new ArgumentException(
                "[GenericRenderMaterialLibrary] Cannot prepare a material with an invalid RHI device.",
                nameof(device));
        }

        int index = GetMaterialIndex(materialId);
        return EnsurePreparedAtIndex(device, submittedTicket, index);
    }

    public bool TryGetPreparedMaterial(Guid materialGuid, out RHIMaterialResource resource)
    {
        ThrowIfDisposed();
        if (m_MaterialIDs.TryGetValue(materialGuid, out uint materialId))
        {
            int index = GetMaterialIndex(materialId);
            if (m_Materials[index].Resource is { IsValid: true } prepared)
            {
                resource = prepared;
                return true;
            }
        }

        resource = null!;
        return false;
    }

    public Guid[] GetStalePreparedMaterialGuids()
    {
        ThrowIfDisposed();
        List<Guid>? staleGuids = null;
        for (int index = 0; index < m_Materials.Count; index++)
        {
            MaterialEntry entry = m_Materials[index];
            if (entry.Resource is { } resource &&
                (!resource.IsValid || resource.IsSourceStale()))
            {
                (staleGuids ??= new List<Guid>()).Add(entry.MaterialGuid);
            }
        }

        return staleGuids?.ToArray() ?? Array.Empty<Guid>();
    }

    internal bool TryAcquirePreparedMaterial(
        Guid materialGuid,
        out PreparedMaterialLease lease)
    {
        ThrowIfDisposed();
        if (m_MaterialIDs.TryGetValue(materialGuid, out uint materialId))
        {
            int index = GetMaterialIndex(materialId);
            if (m_Materials[index].Resource is { IsValid: true } prepared &&
                !prepared.IsSourceStale() &&
                m_PreparedOwnership.TryGetValue(
                    prepared,
                    out PreparedMaterialOwnership? ownership) &&
                ownership.IsCurrent)
            {
                ownership.LeaseCount = checked(ownership.LeaseCount + 1);
                m_PreparedPublicationLeaseCount = checked(
                    m_PreparedPublicationLeaseCount + 1);
                lease = new PreparedMaterialLease(this, ownership);
                return true;
            }
        }

        lease = null!;
        return false;
    }

    internal bool ReleasePrepared(
        Guid materialGuid,
        RHIMaterialResource expectedResource,
        ulong expectedPublicationGeneration,
        ulong submittedTicket,
        bool disposeImmediately = false)
    {
        ThrowIfDisposed();
        if (!m_MaterialIDs.TryGetValue(materialGuid, out uint materialId) ||
            materialId == DefaultMaterialID)
        {
            return false;
        }

        int index = GetMaterialIndex(materialId);
        MaterialEntry entry = m_Materials[index];
        if (!ReferenceEquals(entry.Resource, expectedResource) ||
            !m_PreparedOwnership.TryGetValue(
                expectedResource,
                out PreparedMaterialOwnership? ownership) ||
            !ownership.IsCurrent ||
            ownership.PublicationGeneration != expectedPublicationGeneration)
        {
            return false;
        }

        RetirePreparedResource(entry.Resource, submittedTicket, disposeImmediately);
        entry.Resource = null;
        m_Materials[index] = entry;
        return true;
    }

    internal void SetPreparedPublicationEstimatedGpuBytes(
        RHIMaterialResource resource,
        ulong publicationGeneration,
        long estimatedGpuBytes)
    {
        if (estimatedGpuBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedGpuBytes));
        }
        if (!m_PreparedOwnership.TryGetValue(
                resource,
                out PreparedMaterialOwnership? ownership) ||
            !ownership.IsCurrent ||
            ownership.PublicationGeneration != publicationGeneration)
        {
            throw new InvalidOperationException(
                "[GenericRenderMaterialLibrary] Cannot account an inactive prepared material publication.");
        }

        ownership.EstimatedGpuBytes = Math.Max(
            ownership.EstimatedGpuBytes,
            estimatedGpuBytes);
    }

    public void ApplyMaterialSlots(StaticMeshPass pass)
    {
        ThrowIfDisposed();

        if (pass == null)
        {
            throw new ArgumentNullException(nameof(pass));
        }

        for (int i = 0; i < m_Materials.Count; i++)
        {
            var entry = m_Materials[i];
            if (entry.Resource is { IsValid: true })
            {
                pass.SetMaterialSlot(entry.MaterialID, entry.Resource);
            }
        }
    }

    public void ReleasePreparedResources()
    {
        if (m_PreparedPublicationLeaseCount != 0)
        {
            throw new InvalidOperationException(
                $"[GenericRenderMaterialLibrary] Cannot release prepared materials while " +
                $"{m_PreparedPublicationLeaseCount} exact-publication leases remain active.");
        }

        for (int i = m_Materials.Count - 1; i >= 0; i--)
        {
            var entry = m_Materials[i];
            if (entry.Resource != null)
            {
                RetirePreparedResource(
                    entry.Resource,
                    submittedTicket: 0,
                    disposeImmediately: true);
            }
            entry.Resource = null;
            m_Materials[i] = entry;
        }

        if (m_PreparedOwnership.Count != 0)
        {
            throw new InvalidOperationException(
                $"[GenericRenderMaterialLibrary] {m_PreparedOwnership.Count} prepared " +
                "material publications retained ownership after device-resource release.");
        }
    }

    public void Dispose()
    {
        if (m_Disposed)
        {
            return;
        }

        ReleasePreparedResources();
        m_TextureCache.Dispose();
        m_MaterialIDs.Clear();
        m_Materials.Clear();
        DefaultMaterialID = 0;
        m_Disposed = true;
    }

    private static bool MaterialDependsOnDirtyGuid(MaterialEntry entry, ReadOnlySpan<Guid> dirtyGuids)
    {
        if (ContainsGuid(dirtyGuids, entry.MaterialGuid))
        {
            return true;
        }

        var resource = entry.Resource;
        if (resource == null)
        {
            return false;
        }

        var asset = resource.Asset;
        if (ContainsGuid(dirtyGuids, asset.Shader.Guid))
        {
            return true;
        }

        if (asset.Texture2DRefs != null)
        {
            for (int i = 0; i < asset.Texture2DRefs.Count; i++)
            {
                if (ContainsGuid(dirtyGuids, asset.Texture2DRefs[i].Texture.Guid))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ContainsGuid(ReadOnlySpan<Guid> guids, Guid guid)
    {
        for (int i = 0; i < guids.Length; i++)
        {
            if (guids[i] == guid)
            {
                return true;
            }
        }

        return false;
    }

    private void ThrowIfDisposed()
    {
        if (m_Disposed)
        {
            throw new ObjectDisposedException(nameof(GenericRenderMaterialLibrary));
        }
    }

    private int GetMaterialIndex(uint materialId)
    {
        if (materialId < FirstMaterialID)
        {
            throw new InvalidOperationException(
                $"[GenericRenderMaterialLibrary] Material ID {materialId} is invalid.");
        }

        int index = checked((int)(materialId - FirstMaterialID));
        if ((uint)index >= (uint)m_Materials.Count || m_Materials[index].MaterialID != materialId)
        {
            throw new InvalidOperationException(
                $"[GenericRenderMaterialLibrary] Material ID {materialId} is not registered.");
        }

        return index;
    }

    private Guid EnsurePreparedAtIndex(
        RHIDevice device,
        ulong submittedTicket,
        int index)
    {
        var entry = m_Materials[index];
        if (entry.Resource is { } current)
        {
            return current.IsValid && !current.IsSourceStale()
                ? Guid.Empty
                : entry.MaterialGuid;
        }

        var cookedMaterial = MaterialAssetCooker.LoadOrCook(m_AssetDatabase, entry.MaterialGuid);
        var resource = new RHIMaterialResource(
            device,
            m_AssetDatabase,
            cookedMaterial.Asset,
            cookedMaterial.Handle,
            m_TextureCache);
        bool ownershipPublished = false;
        try
        {
            m_PreparedOwnership.Add(
                resource,
                new PreparedMaterialOwnership(
                    resource,
                    NextPreparedPublicationGeneration()));
            ownershipPublished = true;
            entry.Resource = resource;
            m_Materials[index] = entry;
        }
        catch
        {
            if (ownershipPublished)
            {
                RetirePreparedResource(
                    resource,
                    submittedTicket: 0,
                    disposeImmediately: true);
            }
            else
            {
                resource.Dispose();
            }
            throw;
        }

        Logger.Log(
            $"[GenericRenderMaterialLibrary] Prepared material | ID: {entry.MaterialID} | " +
            $"Name: {cookedMaterial.Asset.Name} | Shader: {cookedMaterial.Asset.Shader.Name}");
        return Guid.Empty;
    }

    private ulong NextPreparedPublicationGeneration()
    {
        m_NextPreparedPublicationGeneration = checked(
            m_NextPreparedPublicationGeneration + 1);
        return m_NextPreparedPublicationGeneration;
    }

    private void RetirePreparedResource(
        RHIMaterialResource resource,
        ulong submittedTicket,
        bool disposeImmediately)
    {
        if (!m_PreparedOwnership.TryGetValue(
                resource,
                out PreparedMaterialOwnership? ownership) ||
            !ownership.IsCurrent)
        {
            throw new InvalidOperationException(
                "[GenericRenderMaterialLibrary] Prepared material publication ownership is missing or already retired.");
        }

        if (ownership.LeaseCount != 0 && disposeImmediately)
        {
            throw new InvalidOperationException(
                $"[GenericRenderMaterialLibrary] Cannot immediately release material " +
                $"publication {ownership.PublicationGeneration} while " +
                $"{ownership.LeaseCount} leases remain active.");
        }

        ownership.IsCurrent = false;
        if (ownership.LeaseCount == 0)
        {
            try
            {
                DisposePreparedResource(resource, submittedTicket, disposeImmediately);
            }
            catch
            {
                ownership.IsCurrent = true;
                throw;
            }

            if (!m_PreparedOwnership.Remove(resource))
            {
                throw new InvalidOperationException(
                    "[GenericRenderMaterialLibrary] Retired prepared material publication was not tracked.");
            }
            return;
        }

        ownership.RetirementTicket = Math.Max(
            ownership.RetirementTicket,
            submittedTicket);
    }

    private void ReleasePreparedLease(PreparedMaterialOwnership ownership)
    {
        if (!m_PreparedOwnership.TryGetValue(
                ownership.Resource,
                out PreparedMaterialOwnership? current) ||
            !ReferenceEquals(current, ownership) ||
            ownership.LeaseCount <= 0 ||
            m_PreparedPublicationLeaseCount <= 0)
        {
            throw new InvalidOperationException(
                "[GenericRenderMaterialLibrary] Prepared material lease ownership is invalid.");
        }

        if (ownership.LeaseCount == 1 && !ownership.IsCurrent)
        {
            DisposePreparedResource(
                ownership.Resource,
                ownership.RetirementTicket,
                disposeImmediately: false);
            if (!m_PreparedOwnership.Remove(ownership.Resource))
            {
                throw new InvalidOperationException(
                    "[GenericRenderMaterialLibrary] Retired material publication was not tracked during lease release.");
            }
        }

        ownership.LeaseCount--;
        m_PreparedPublicationLeaseCount--;
    }

    private bool IsPreparedPublicationCurrent(PreparedMaterialOwnership ownership) =>
        !m_Disposed &&
        ownership.IsCurrent &&
        m_PreparedOwnership.TryGetValue(
            ownership.Resource,
            out PreparedMaterialOwnership? current) &&
        ReferenceEquals(current, ownership);

    private void DisposePreparedResource(
        RHIMaterialResource resource,
        ulong submittedTicket,
        bool disposeImmediately)
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

    private struct MaterialEntry
    {
        public readonly Guid MaterialGuid;
        public readonly uint MaterialID;
        public RHIMaterialResource? Resource;

        public MaterialEntry(Guid materialGuid, uint materialId)
        {
            MaterialGuid = materialGuid;
            MaterialID = materialId;
            Resource = null;
        }
    }

    internal sealed class PreparedMaterialOwnership
    {
        public PreparedMaterialOwnership(
            RHIMaterialResource resource,
            ulong publicationGeneration)
        {
            Resource = resource;
            PublicationGeneration = publicationGeneration;
        }

        public RHIMaterialResource Resource { get; }
        public ulong PublicationGeneration { get; }
        public int LeaseCount { get; set; }
        public bool IsCurrent { get; set; } = true;
        public ulong RetirementTicket { get; set; }
        public long EstimatedGpuBytes { get; set; }
    }

    internal sealed class PreparedMaterialLease : IDisposable
    {
        private readonly object m_DisposeGate = new();
        private GenericRenderMaterialLibrary? m_Owner;
        private readonly PreparedMaterialOwnership m_Ownership;

        internal PreparedMaterialLease(
            GenericRenderMaterialLibrary owner,
            PreparedMaterialOwnership ownership)
        {
            m_Owner = owner;
            m_Ownership = ownership;
        }

        public RHIMaterialResource Resource => m_Ownership.Resource;
        public ulong PublicationGeneration => m_Ownership.PublicationGeneration;
        public bool IsCurrent =>
            Volatile.Read(ref m_Owner)?.IsPreparedPublicationCurrent(m_Ownership) == true;

        public void Dispose()
        {
            lock (m_DisposeGate)
            {
                GenericRenderMaterialLibrary? owner = m_Owner;
                if (owner == null)
                {
                    return;
                }

                owner.ReleasePreparedLease(m_Ownership);
                m_Owner = null;
            }
        }
    }
}
