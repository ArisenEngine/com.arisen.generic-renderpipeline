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
    private bool m_Disposed;

    public uint DefaultMaterialID { get; private set; }

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
            if (entry.Resource is not { IsValid: true } ||
                !MaterialDependsOnDirtyGuid(entry, dirtyGuids))
            {
                continue;
            }

            Logger.Log($"[GenericRenderMaterialLibrary] Asset change invalidated material ID {entry.MaterialID}; releasing prepared material.");
            m_DisposalQueue.Enqueue(entry.Resource, submittedTicket);
            entry.Resource = null;
            m_Materials[i] = entry;
        }
    }

    public void EnsurePrepared(RHIDevice device, ulong submittedTicket)
    {
        ThrowIfDisposed();

        if (!device.IsValid)
        {
            throw new ArgumentException("[GenericRenderMaterialLibrary] Cannot prepare materials with an invalid RHI device.", nameof(device));
        }

        for (int i = 0; i < m_Materials.Count; i++)
        {
            var entry = m_Materials[i];
            if (entry.Resource is { IsValid: true } && !entry.Resource.IsSourceStale())
            {
                continue;
            }

            if (entry.Resource != null)
            {
                Logger.Log($"[GenericRenderMaterialLibrary] Material dependency changed; reloading material ID {entry.MaterialID}.");
                m_DisposalQueue.Enqueue(entry.Resource, submittedTicket);
                entry.Resource = null;
            }

            var cookedMaterial = MaterialAssetCooker.LoadOrCook(m_AssetDatabase, entry.MaterialGuid);
            entry.Resource = new RHIMaterialResource(device, m_AssetDatabase, cookedMaterial.Asset, cookedMaterial.Handle);
            m_Materials[i] = entry;

            Logger.Log(
                $"[GenericRenderMaterialLibrary] Prepared material | ID: {entry.MaterialID} | Name: {cookedMaterial.Asset.Name} | Shader: {cookedMaterial.Asset.Shader.Name}");
        }
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
        for (int i = m_Materials.Count - 1; i >= 0; i--)
        {
            var entry = m_Materials[i];
            entry.Resource?.Dispose();
            entry.Resource = null;
            m_Materials[i] = entry;
        }
    }

    public void Dispose()
    {
        if (m_Disposed)
        {
            return;
        }

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
}
