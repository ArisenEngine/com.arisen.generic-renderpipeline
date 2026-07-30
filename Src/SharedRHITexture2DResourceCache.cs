using Arisen.Native.RHI;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.RHI;
using ArisenEngine.Rendering.Resources;

namespace ArisenEngine.Rendering;

public sealed class SharedRHITexture2DResourceCache : IRHITexture2DResourceCache, IDisposable
{
    private readonly Dictionary<TextureKey, TextureEntry> m_Entries = new();
    private readonly HashSet<TextureEntry> m_LiveEntries = new();
    private bool m_Disposed;

    public int ResourceCount => m_LiveEntries.Count;

    public long EstimatedGpuBytes =>
        m_LiveEntries.Sum(entry => entry.EstimatedGpuBytes);

    public IRHITexture2DLease Acquire(
        RHIDevice device,
        IAssetDatabase assetDatabase,
        Texture2DAsset asset,
        MaterialTextureSamplerSettings samplerSettings)
    {
        if (m_Disposed) throw new ObjectDisposedException(nameof(SharedRHITexture2DResourceCache));
        var key = new TextureKey(asset.Guid, asset.Variant, samplerSettings);
        if (!m_Entries.TryGetValue(key, out TextureEntry? entry) ||
            entry.Resource.IsSourceStale())
        {
            var resource = new RHITexture2DResource(
                device,
                assetDatabase,
                asset,
                samplerSettings);
            entry = new TextureEntry(
                resource,
                checked((long)resource.Width * resource.Height * 4));
            m_Entries[key] = entry;
            m_LiveEntries.Add(entry);
        }

        entry.ReferenceCount++;
        return new TextureLease(this, key, entry);
    }

    public void Dispose()
    {
        if (m_Disposed) return;
        foreach (TextureEntry entry in m_LiveEntries) entry.Resource.Dispose();
        m_LiveEntries.Clear();
        m_Entries.Clear();
        m_Disposed = true;
    }

    private void Release(TextureKey key, TextureEntry entry)
    {
        if (m_Disposed) return;
        entry.ReferenceCount--;
        if (entry.ReferenceCount > 0) return;
        if (m_Entries.TryGetValue(key, out TextureEntry? current) &&
            ReferenceEquals(current, entry))
        {
            m_Entries.Remove(key);
        }
        if (m_LiveEntries.Remove(entry)) entry.Resource.Dispose();
    }

    private readonly record struct TextureKey(
        Guid Guid,
        Texture2DVariantKey Variant,
        MaterialTextureSamplerSettings Sampler);

    private sealed class TextureEntry
    {
        public TextureEntry(RHITexture2DResource resource, long estimatedGpuBytes)
        {
            Resource = resource;
            EstimatedGpuBytes = estimatedGpuBytes;
        }

        public RHITexture2DResource Resource { get; }
        public long EstimatedGpuBytes { get; }
        public int ReferenceCount { get; set; }
    }

    private sealed class TextureLease : IRHITexture2DLease
    {
        private SharedRHITexture2DResourceCache? m_Owner;
        private readonly TextureKey m_Key;
        private readonly TextureEntry m_Entry;

        public TextureLease(
            SharedRHITexture2DResourceCache owner,
            TextureKey key,
            TextureEntry entry)
        {
            m_Owner = owner;
            m_Key = key;
            m_Entry = entry;
        }

        public bool IsValid => m_Entry.Resource.IsValid;
        public uint BindlessImageIndex => m_Entry.Resource.BindlessImageIndex;
        public uint BindlessSamplerIndex => m_Entry.Resource.BindlessSamplerIndex;

        public void Dispose()
        {
            Interlocked.Exchange(ref m_Owner, null)?.Release(m_Key, m_Entry);
        }
    }
}
