using Arisen.Native.RHI;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.RHI;
using ArisenEngine.Rendering.Resources;

namespace ArisenEngine.Rendering;

internal sealed class SharedRHITexture2DResourceCache : IRHITexture2DResourceCache, IDisposable
{
    private readonly Dictionary<TextureKey, TextureEntry> m_Entries = new();
    private bool m_Disposed;

    public int ResourceCount => m_Entries.Count;

    public long EstimatedGpuBytes => m_Entries.Values.Sum(entry => entry.EstimatedGpuBytes);

    public IRHITexture2DLease Acquire(
        RHIDevice device,
        IAssetDatabase assetDatabase,
        Texture2DAsset asset,
        MaterialTextureSamplerSettings samplerSettings)
    {
        if (m_Disposed) throw new ObjectDisposedException(nameof(SharedRHITexture2DResourceCache));
        var key = new TextureKey(asset.Guid, asset.Variant, samplerSettings);
        if (!m_Entries.TryGetValue(key, out TextureEntry? entry))
        {
            var resource = new RHITexture2DResource(
                device,
                assetDatabase,
                asset,
                samplerSettings);
            entry = new TextureEntry(
                resource,
                checked((long)resource.Width * resource.Height * 4));
            m_Entries.Add(key, entry);
        }

        entry.ReferenceCount++;
        return new TextureLease(this, key, entry.Resource);
    }

    public void Dispose()
    {
        if (m_Disposed) return;
        foreach (TextureEntry entry in m_Entries.Values) entry.Resource.Dispose();
        m_Entries.Clear();
        m_Disposed = true;
    }

    private void Release(TextureKey key)
    {
        if (m_Disposed || !m_Entries.TryGetValue(key, out TextureEntry? entry)) return;
        entry.ReferenceCount--;
        if (entry.ReferenceCount > 0) return;
        m_Entries.Remove(key);
        entry.Resource.Dispose();
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
        private readonly RHITexture2DResource m_Resource;

        public TextureLease(
            SharedRHITexture2DResourceCache owner,
            TextureKey key,
            RHITexture2DResource resource)
        {
            m_Owner = owner;
            m_Key = key;
            m_Resource = resource;
        }

        public bool IsValid => m_Resource.IsValid;
        public uint BindlessImageIndex => m_Resource.BindlessImageIndex;
        public uint BindlessSamplerIndex => m_Resource.BindlessSamplerIndex;

        public void Dispose()
        {
            Interlocked.Exchange(ref m_Owner, null)?.Release(m_Key);
        }
    }
}
