using Arisen.Native.RHI;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.RHI;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ArisenEngine.Rendering;

public readonly struct DirectionalShadowFrameData
{
    private const uint InvalidBindlessIndex = 0xFFFFFFFFu;

    private readonly uint m_ImageIndex0;
    private readonly uint m_ImageIndex1;
    private readonly uint m_ImageIndex2;
    private readonly uint m_ImageIndex3;

    internal DirectionalShadowFrameData(
        in DirectionalShadowCascadeSet cascades,
        RHIBufferHandle constantsBuffer,
        uint constantsBufferBindlessIndex,
        uint samplerIndex,
        uint imageIndex0,
        uint imageIndex1,
        uint imageIndex2,
        uint imageIndex3,
        bool enabled)
    {
        Cascades = cascades;
        ConstantsBuffer = constantsBuffer;
        ConstantsBufferBindlessIndex = constantsBufferBindlessIndex;
        SamplerIndex = samplerIndex;
        m_ImageIndex0 = imageIndex0;
        m_ImageIndex1 = imageIndex1;
        m_ImageIndex2 = imageIndex2;
        m_ImageIndex3 = imageIndex3;
        Enabled = enabled &&
                  cascades.IsValid &&
                  constantsBuffer.IsValid &&
                  constantsBufferBindlessIndex != InvalidBindlessIndex &&
                  samplerIndex != InvalidBindlessIndex;
    }

    public DirectionalShadowCascadeSet Cascades { get; }
    public RHIBufferHandle ConstantsBuffer { get; }
    public uint ConstantsBufferBindlessIndex { get; }
    public uint SamplerIndex { get; }
    public bool Enabled { get; }

    public uint GetImageIndex(int cascadeIndex)
    {
        if ((uint)cascadeIndex >= (uint)Cascades.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(cascadeIndex));
        }

        return cascadeIndex switch
        {
            0 => m_ImageIndex0,
            1 => m_ImageIndex1,
            2 => m_ImageIndex2,
            _ => m_ImageIndex3
        };
    }
}

internal sealed class DirectionalShadowFrameBuffer : IDisposable
{
    private const uint InvalidBindlessIndex = 0xFFFFFFFFu;
    private const int DefaultRingSize = 2;

    private DirectionalShadowBufferSlot[] m_Slots = Array.Empty<DirectionalShadowBufferSlot>();
    private RHIFactory m_Factory;
    private int m_RingSize;
    private bool m_Disposed;

    public unsafe DirectionalShadowFrameData Prepare(
        RenderContext context,
        in DirectionalShadowCascadeSet cascades,
        RenderGraphTexture shadowTexture,
        in GenericShadowSettings settings,
        bool enabled)
    {
        ObjectDisposedException.ThrowIf(m_Disposed, this);
        ArgumentNullException.ThrowIfNull(shadowTexture);
        if (!context.Device.IsValid)
        {
            throw new ArgumentException(
                "[DirectionalShadowFrameBuffer] RHI device is invalid.",
                nameof(context));
        }

        int ringSize = GetRingSize(context);
        EnsureRing(context.Device.GetFactory(), ringSize);
        int slotIndex = checked((int)(context.FrameResourceIndex % (uint)ringSize));
        ref DirectionalShadowBufferSlot slot = ref m_Slots[slotIndex];
        EnsureSlot(ref slot, slotIndex);

        DirectionalShadowGpuConstants constants = DirectionalShadowGpuConstants.Create(
            cascades,
            shadowTexture,
            settings,
            enabled);
        IntPtr mapped = m_Factory.MapBuffer(slot.Buffer);
        if (mapped == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "[DirectionalShadowFrameBuffer] Failed to map cascade constants buffer.");
        }

        try
        {
            *(DirectionalShadowGpuConstants*)mapped.ToPointer() = constants;
        }
        finally
        {
            m_Factory.UnmapBuffer(slot.Buffer);
        }

        uint image0 = cascades.Count > 0 ? shadowTexture.GetLayerBindlessImageIndex(0) : InvalidBindlessIndex;
        uint image1 = cascades.Count > 1 ? shadowTexture.GetLayerBindlessImageIndex(1) : InvalidBindlessIndex;
        uint image2 = cascades.Count > 2 ? shadowTexture.GetLayerBindlessImageIndex(2) : InvalidBindlessIndex;
        uint image3 = cascades.Count > 3 ? shadowTexture.GetLayerBindlessImageIndex(3) : InvalidBindlessIndex;
        return new DirectionalShadowFrameData(
            cascades,
            slot.Buffer,
            slot.BindlessIndex,
            shadowTexture.BindlessSamplerIndex,
            image0,
            image1,
            image2,
            image3,
            enabled);
    }

    public void Dispose()
    {
        if (m_Disposed)
        {
            return;
        }

        for (int index = m_Slots.Length - 1; index >= 0; index--)
        {
            ReleaseSlot(ref m_Slots[index]);
        }

        m_Slots = Array.Empty<DirectionalShadowBufferSlot>();
        m_RingSize = 0;
        m_Factory = default;
        m_Disposed = true;
    }

    private static int GetRingSize(RenderContext context)
    {
        uint maxFramesInFlight = context.Device.GetInstance().MaxFramesInFlight;
        return maxFramesInFlight == 0
            ? DefaultRingSize
            : checked((int)Math.Max(1u, maxFramesInFlight));
    }

    private void EnsureRing(RHIFactory factory, int ringSize)
    {
        if (m_RingSize == ringSize &&
            m_Slots.Length == ringSize &&
            m_Factory.IsValid)
        {
            return;
        }

        if (m_Factory.IsValid)
        {
            throw new InvalidOperationException(
                "[DirectionalShadowFrameBuffer] Cannot resize an active cascade buffer ring.");
        }

        m_Factory = factory;
        m_RingSize = ringSize;
        m_Slots = new DirectionalShadowBufferSlot[ringSize];
    }

    private unsafe void EnsureSlot(ref DirectionalShadowBufferSlot slot, int slotIndex)
    {
        if (slot.IsValid)
        {
            return;
        }

        ulong byteSize = (ulong)sizeof(DirectionalShadowGpuConstants);
        RHIBufferHandle buffer = m_Factory.CreateBuffer(
            byteSize,
            (uint)EBufferUsageFlagBits.BUFFER_USAGE_STORAGE_BUFFER_BIT,
            ESharingMode.SHARING_MODE_EXCLUSIVE,
            ERHIMemoryUsage.Upload,
            $"DirectionalShadow.FrameData[{slotIndex}]");
        if (!buffer.IsValid)
        {
            throw new InvalidOperationException(
                "[DirectionalShadowFrameBuffer] Failed to create cascade constants buffer.");
        }

        uint bindlessIndex = m_Factory.RegisterBindlessResourceBuffer(buffer);
        if (bindlessIndex == InvalidBindlessIndex)
        {
            m_Factory.ReleaseBuffer(buffer);
            throw new InvalidOperationException(
                "[DirectionalShadowFrameBuffer] Failed to register cascade constants buffer.");
        }

        slot = new DirectionalShadowBufferSlot(buffer, bindlessIndex);
        Logger.Log(
            $"[DirectionalShadowFrameBuffer] Created slot | Slot: {slotIndex}/{m_RingSize} | " +
            $"Bytes: {byteSize} | Buffer: {buffer.Index}:{buffer.Generation} | Bindless: {bindlessIndex}");
    }

    private void ReleaseSlot(ref DirectionalShadowBufferSlot slot)
    {
        if (!m_Factory.IsValid)
        {
            slot = default;
            return;
        }

        if (slot.BindlessIndex != InvalidBindlessIndex)
        {
            m_Factory.UnregisterBindlessResourceBuffer(slot.BindlessIndex);
        }

        if (slot.Buffer.IsValid)
        {
            m_Factory.ReleaseBuffer(slot.Buffer);
        }

        slot = default;
    }

    private readonly struct DirectionalShadowBufferSlot
    {
        public DirectionalShadowBufferSlot(RHIBufferHandle buffer, uint bindlessIndex)
        {
            Buffer = buffer;
            BindlessIndex = bindlessIndex;
        }

        public RHIBufferHandle Buffer { get; }
        public uint BindlessIndex { get; }
        public bool IsValid => Buffer.IsValid && BindlessIndex != InvalidBindlessIndex;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct DirectionalShadowGpuConstants
{
    public const int VectorCount = 22;

    private readonly MatrixColumns m_Cascade0;
    private readonly MatrixColumns m_Cascade1;
    private readonly MatrixColumns m_Cascade2;
    private readonly MatrixColumns m_Cascade3;
    private readonly Vector4 m_SplitFar;
    private readonly Vector4 m_TransitionStart;
    private readonly Vector4 m_ImageIndices;
    private readonly Vector4 m_SamplingParameters;
    private readonly Vector4 m_Metadata;
    private readonly Vector4 m_DistanceParameters;

    private DirectionalShadowGpuConstants(
        in MatrixColumns cascade0,
        in MatrixColumns cascade1,
        in MatrixColumns cascade2,
        in MatrixColumns cascade3,
        Vector4 splitFar,
        Vector4 transitionStart,
        Vector4 imageIndices,
        Vector4 samplingParameters,
        Vector4 metadata,
        Vector4 distanceParameters)
    {
        m_Cascade0 = cascade0;
        m_Cascade1 = cascade1;
        m_Cascade2 = cascade2;
        m_Cascade3 = cascade3;
        m_SplitFar = splitFar;
        m_TransitionStart = transitionStart;
        m_ImageIndices = imageIndices;
        m_SamplingParameters = samplingParameters;
        m_Metadata = metadata;
        m_DistanceParameters = distanceParameters;
    }

    public static DirectionalShadowGpuConstants Create(
        in DirectionalShadowCascadeSet cascades,
        RenderGraphTexture shadowTexture,
        in GenericShadowSettings settings,
        bool enabled)
    {
        DirectionalShadowCascade cascade0 = cascades.Count > 0 ? cascades.GetCascade(0) : default;
        DirectionalShadowCascade cascade1 = cascades.Count > 1 ? cascades.GetCascade(1) : default;
        DirectionalShadowCascade cascade2 = cascades.Count > 2 ? cascades.GetCascade(2) : default;
        DirectionalShadowCascade cascade3 = cascades.Count > 3 ? cascades.GetCascade(3) : default;
        uint invalidIndex = 0xFFFFFFFFu;
        uint image0 = cascades.Count > 0 ? shadowTexture.GetLayerBindlessImageIndex(0) : invalidIndex;
        uint image1 = cascades.Count > 1 ? shadowTexture.GetLayerBindlessImageIndex(1) : invalidIndex;
        uint image2 = cascades.Count > 2 ? shadowTexture.GetLayerBindlessImageIndex(2) : invalidIndex;
        uint image3 = cascades.Count > 3 ? shadowTexture.GetLayerBindlessImageIndex(3) : invalidIndex;
        bool usable = enabled && cascades.IsValid;

        return new DirectionalShadowGpuConstants(
            MatrixColumns.From(cascade0.ViewProjection),
            MatrixColumns.From(cascade1.ViewProjection),
            MatrixColumns.From(cascade2.ViewProjection),
            MatrixColumns.From(cascade3.ViewProjection),
            new Vector4(cascade0.SplitFar, cascade1.SplitFar, cascade2.SplitFar, cascade3.SplitFar),
            new Vector4(
                cascade0.TransitionStart,
                cascade1.TransitionStart,
                cascade2.TransitionStart,
                cascade3.TransitionStart),
            new Vector4(
                AsFloat(image0),
                AsFloat(image1),
                AsFloat(image2),
                AsFloat(image3)),
            new Vector4(
                settings.DepthBias,
                settings.SlopeBias,
                settings.Strength,
                1.0f / Math.Max(1u, shadowTexture.Width)),
            new Vector4(
                AsFloat(shadowTexture.BindlessSamplerIndex),
                AsFloat(checked((uint)Math.Clamp(settings.PcfRadius, 0, 3))),
                AsFloat(checked((uint)cascades.Count)),
                AsFloat(usable ? 1u : 0u)),
            new Vector4(
                cascades.NearClip,
                cascades.MaximumDistance,
                cascades.TerminalFadeStart,
                0.0f));
    }

    private static float AsFloat(uint value) => BitConverter.UInt32BitsToSingle(value);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MatrixColumns
    {
        private readonly Vector4 m_Column0;
        private readonly Vector4 m_Column1;
        private readonly Vector4 m_Column2;
        private readonly Vector4 m_Column3;

        private MatrixColumns(
            Vector4 column0,
            Vector4 column1,
            Vector4 column2,
            Vector4 column3)
        {
            m_Column0 = column0;
            m_Column1 = column1;
            m_Column2 = column2;
            m_Column3 = column3;
        }

        public static MatrixColumns From(Matrix4x4 matrix)
        {
            return new MatrixColumns(
                new Vector4(matrix.M11, matrix.M21, matrix.M31, matrix.M41),
                new Vector4(matrix.M12, matrix.M22, matrix.M32, matrix.M42),
                new Vector4(matrix.M13, matrix.M23, matrix.M33, matrix.M43),
                new Vector4(matrix.M14, matrix.M24, matrix.M34, matrix.M44));
        }
    }
}
