using Arisen.Native.RHI;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.RHI;

namespace ArisenEngine.Rendering;

internal sealed class DirectionalShadowTarget : IDisposable
{
    private const uint InvalidBindlessIndex = 0xFFFFFFFFu;

    private RHIFactory m_Factory;
    private RHIImageHandle m_Image = RHIImageHandle.Invalid;
    private RHIImageViewHandle m_ImageView = RHIImageViewHandle.Invalid;
    private RHISamplerHandle m_Sampler = RHISamplerHandle.Invalid;
    private uint m_BindlessImageIndex = InvalidBindlessIndex;
    private uint m_BindlessSamplerIndex = InvalidBindlessIndex;
    private uint m_Size;
    private EFormat m_Format = EFormat.FORMAT_D32_SFLOAT;
    private EImageLayout m_ExpectedLayout = EImageLayout.IMAGE_LAYOUT_UNDEFINED;
    private bool m_Disposed;

    public RHIImageHandle Image => m_Image;
    public RHIImageViewHandle ImageView => m_ImageView;
    public uint BindlessImageIndex => m_BindlessImageIndex;
    public uint BindlessSamplerIndex => m_BindlessSamplerIndex;
    public uint Size => m_Size;
    public EFormat Format => m_Format;
    public EImageLayout ExpectedLayout => m_ExpectedLayout;

    public bool IsValid =>
        m_Image.IsValid &&
        m_ImageView.IsValid &&
        m_Sampler.IsValid &&
        m_BindlessImageIndex != InvalidBindlessIndex &&
        m_BindlessSamplerIndex != InvalidBindlessIndex;

    public void Ensure(
        RHIFactory factory,
        uint size,
        EFormat format = EFormat.FORMAT_D32_SFLOAT)
    {
        ThrowIfDisposed();

        if (!factory.IsValid)
        {
            throw new ArgumentException("[DirectionalShadowTarget] Cannot create shadow map with an invalid RHI factory.", nameof(factory));
        }

        if (size == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "[DirectionalShadowTarget] Shadow map size must be non-zero.");
        }

        if (IsValid &&
            m_Size == size &&
            m_Format == format)
        {
            return;
        }

        ReleaseResources();

        m_Factory = factory;
        m_Size = size;
        m_Format = format;
        m_Image = factory.CreateImage(
            size,
            size,
            1,
            1,
            1,
            format,
            (uint)EImageUsageFlagBits.IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT |
            (uint)EImageUsageFlagBits.IMAGE_USAGE_SAMPLED_BIT,
            ERHIMemoryUsage.GpuOnly,
            "GenericDirectionalShadowMap");
        if (!m_Image.IsValid)
        {
            throw new InvalidOperationException("[DirectionalShadowTarget] Failed to create shadow map image.");
        }

        m_ImageView = factory.CreateImageView(
            m_Image,
            EImageViewType.IMAGE_VIEW_TYPE_2D,
            format,
            (uint)EImageAspectFlagBits.IMAGE_ASPECT_DEPTH_BIT,
            0,
            1,
            0,
            1);
        if (!m_ImageView.IsValid)
        {
            ReleaseResources();
            throw new InvalidOperationException("[DirectionalShadowTarget] Failed to create shadow map image view.");
        }

        m_Sampler = factory.CreateSampler(
            EFilter.FILTER_LINEAR,
            EFilter.FILTER_LINEAR,
            ESamplerMipmapMode.SAMPLER_MIPMAP_MODE_NEAREST,
            ESamplerAddressMode.SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE);
        if (!m_Sampler.IsValid)
        {
            ReleaseResources();
            throw new InvalidOperationException("[DirectionalShadowTarget] Failed to create shadow map sampler.");
        }

        m_BindlessImageIndex = factory.RegisterBindlessResourceImage(m_ImageView);
        m_BindlessSamplerIndex = factory.RegisterBindlessResourceSampler(m_Sampler);
        if (m_BindlessImageIndex == InvalidBindlessIndex ||
            m_BindlessSamplerIndex == InvalidBindlessIndex)
        {
            ReleaseResources();
            throw new InvalidOperationException("[DirectionalShadowTarget] Failed to register shadow map bindless descriptors.");
        }

        m_ExpectedLayout = EImageLayout.IMAGE_LAYOUT_UNDEFINED;
        Logger.Log(
            $"[DirectionalShadowTarget] Created shadow map | Size: {size}x{size} | Format: {format} | Image: {m_Image.Index}:{m_Image.Generation} | View: {m_ImageView.Index}:{m_ImageView.Generation} | BindlessImage: {m_BindlessImageIndex} | BindlessSampler: {m_BindlessSamplerIndex}");
    }

    public void SetExpectedLayout(EImageLayout layout)
    {
        m_ExpectedLayout = layout;
    }

    public void Dispose()
    {
        if (m_Disposed)
        {
            return;
        }

        ReleaseResources();
        m_Disposed = true;
    }

    private void ReleaseResources()
    {
        if (m_Factory.IsValid)
        {
            if (m_BindlessSamplerIndex != InvalidBindlessIndex)
            {
                m_Factory.UnregisterBindlessResourceSampler(m_BindlessSamplerIndex);
            }

            if (m_BindlessImageIndex != InvalidBindlessIndex)
            {
                m_Factory.UnregisterBindlessResourceImage(m_BindlessImageIndex);
            }

            if (m_Sampler.IsValid)
            {
                m_Factory.ReleaseSampler(m_Sampler);
            }

            if (m_ImageView.IsValid)
            {
                m_Factory.ReleaseImageView(m_ImageView);
            }

            if (m_Image.IsValid)
            {
                m_Factory.ReleaseImage(m_Image);
            }
        }

        m_Image = RHIImageHandle.Invalid;
        m_ImageView = RHIImageViewHandle.Invalid;
        m_Sampler = RHISamplerHandle.Invalid;
        m_BindlessImageIndex = InvalidBindlessIndex;
        m_BindlessSamplerIndex = InvalidBindlessIndex;
        m_Size = 0;
        m_Format = EFormat.FORMAT_D32_SFLOAT;
        m_ExpectedLayout = EImageLayout.IMAGE_LAYOUT_UNDEFINED;
    }

    private void ThrowIfDisposed()
    {
        if (m_Disposed)
        {
            throw new ObjectDisposedException(nameof(DirectionalShadowTarget));
        }
    }
}
