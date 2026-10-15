using System.Numerics;
using System.Runtime.InteropServices;
using Arisen.Native.RHI;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.RHI;
using ArisenEngine.Rendering;

namespace ArisenEngine.Rendering;

/// <summary>
/// One palette matrix in the layout the skinning shaders read: four float4 rows where row 0 is
/// (M11, M21, M31, M41), so the vertex shader can rebuild a row-vector matrix from Load4 calls.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct SkinPaletteMatrix
{
    public readonly Vector4 Row0;
    public readonly Vector4 Row1;
    public readonly Vector4 Row2;
    public readonly Vector4 Row3;

    public SkinPaletteMatrix(Matrix4x4 matrix)
    {
        Row0 = new Vector4(matrix.M11, matrix.M21, matrix.M31, matrix.M41);
        Row1 = new Vector4(matrix.M12, matrix.M22, matrix.M32, matrix.M42);
        Row2 = new Vector4(matrix.M13, matrix.M23, matrix.M33, matrix.M43);
        Row3 = new Vector4(matrix.M14, matrix.M24, matrix.M34, matrix.M44);
    }
}

/// <summary>
/// Frame-visible skin palette storage shared by every pass that draws skinned meshes. One ring slot
/// per frame in flight keeps the uploaded palette alive while the GPU still reads it, and the
/// buffer is registered in the bindless table so a pass can reference it from push constants.
/// </summary>
internal sealed class GenericSkinPaletteBuffer : IDisposable
{
    private const uint InvalidBindlessIndex = 0xFFFFFFFFu;
    private const int DefaultRingSize = 2;

    private readonly string m_Name;
    private SkinPaletteMatrix[] m_Palettes = new SkinPaletteMatrix[64];
    private Slot[] m_Slots = Array.Empty<Slot>();
    private RHIFactory m_Factory;
    private int m_RingSize;
    private int m_SlotIndex;
    private int m_Capacity;
    private int m_MatrixCount;
    private bool m_HasData;
    private bool m_Disposed;

    public GenericSkinPaletteBuffer(string name = "GenericRP.SkinPalette")
    {
        m_Name = name;
    }

    public RHIBufferHandle Buffer { get; private set; } = RHIBufferHandle.Invalid;

    public uint BindlessIndex { get; private set; } = InvalidBindlessIndex;

    public int MatrixCount => m_MatrixCount;

    public bool HasData => m_HasData && Buffer.IsValid && BindlessIndex != InvalidBindlessIndex;

    public void Prepare(RenderContext context, RHIFactory factory, ReadOnlySpan<Matrix4x4> palettes)
    {
        ThrowIfDisposed();
        m_Factory = factory;

        if (palettes.IsEmpty)
        {
            m_HasData = false;
            m_MatrixCount = 0;
            Buffer = RHIBufferHandle.Invalid;
            BindlessIndex = InvalidBindlessIndex;
            return;
        }

        EnsureCapacity(palettes.Length);
        for (int matrixIndex = 0; matrixIndex < palettes.Length; matrixIndex++)
        {
            m_Palettes[matrixIndex] = new SkinPaletteMatrix(palettes[matrixIndex]);
        }

        m_MatrixCount = palettes.Length;
        int ringSize = ResolveRingSize(context);
        EnsureRing(factory, ringSize);
        m_SlotIndex = checked((int)(context.FrameResourceIndex % (uint)m_RingSize));
        EnsureSlotBuffer(factory, m_SlotIndex, m_MatrixCount);
        Upload(m_MatrixCount);
        m_HasData = true;
        Profiler.PlotValue("GenericRP.SkinPaletteMatrixCount", m_MatrixCount);
    }

    public void RecordReadBarrier(RenderCommandList commandList)
    {
        if (!HasData)
        {
            return;
        }

        Span<RHIBufferMemoryBarrier> barriers = stackalloc RHIBufferMemoryBarrier[1];
        barriers[0] = new RHIBufferMemoryBarrier
        {
            SrcAccessMask = EAccessFlag.ACCESS_HOST_WRITE_BIT,
            DstAccessMask = EAccessFlag.ACCESS_SHADER_READ_BIT,
            SrcQueueFamilyIndex = RHIQueueFamily.Ignored,
            DstQueueFamilyIndex = RHIQueueFamily.Ignored,
            Buffer = Buffer,
            SrcStageMask = EPipelineStageFlagBits.PIPELINE_STAGE_HOST_BIT,
            DstStageMask = EPipelineStageFlagBits.PIPELINE_STAGE_VERTEX_SHADER_BIT
        };

        commandList.PipelineBarrier(
            EPipelineStageFlagBits.PIPELINE_STAGE_HOST_BIT,
            EPipelineStageFlagBits.PIPELINE_STAGE_VERTEX_SHADER_BIT,
            barriers);
    }

    public void Release()
    {
        for (int slotIndex = 0; slotIndex < m_Slots.Length; slotIndex++)
        {
            ReleaseSlot(slotIndex);
        }

        m_Slots = Array.Empty<Slot>();
        m_RingSize = 0;
        m_SlotIndex = 0;
        m_Capacity = 0;
        m_MatrixCount = 0;
        m_HasData = false;
        Buffer = RHIBufferHandle.Invalid;
        BindlessIndex = InvalidBindlessIndex;
    }

    public void Dispose()
    {
        if (m_Disposed)
        {
            return;
        }

        Release();
        m_Disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (m_Disposed)
        {
            throw new ObjectDisposedException(nameof(GenericSkinPaletteBuffer));
        }
    }

    private void EnsureCapacity(int matrixCount)
    {
        if (m_Palettes.Length >= matrixCount)
        {
            return;
        }

        Array.Resize(ref m_Palettes, Math.Max(matrixCount, m_Palettes.Length * 2));
    }

    private static int ResolveRingSize(RenderContext context)
    {
        uint maxFramesInFlight = context.Device.GetInstance().MaxFramesInFlight;
        return maxFramesInFlight == 0
            ? DefaultRingSize
            : checked((int)Math.Max(1u, maxFramesInFlight));
    }

    private void EnsureRing(RHIFactory factory, int ringSize)
    {
        if (ringSize <= 0)
        {
            ringSize = DefaultRingSize;
        }

        if (m_Slots.Length == ringSize && m_RingSize == ringSize)
        {
            return;
        }

        Release();
        m_Factory = factory;
        m_Slots = new Slot[ringSize];
        m_RingSize = ringSize;
    }

    private unsafe void EnsureSlotBuffer(RHIFactory factory, int slotIndex, int matrixCount)
    {
        ref Slot slot = ref m_Slots[slotIndex];
        if (slot.Buffer.IsValid && slot.Capacity >= matrixCount)
        {
            SetActive(slot);
            return;
        }

        ReleaseSlot(slotIndex);

        ulong byteSize = checked((ulong)matrixCount * (ulong)sizeof(SkinPaletteMatrix));
        var buffer = factory.CreateBuffer(
            byteSize,
            (uint)EBufferUsageFlagBits.BUFFER_USAGE_STORAGE_BUFFER_BIT,
            ESharingMode.SHARING_MODE_EXCLUSIVE,
            ERHIMemoryUsage.Upload,
            $"{m_Name}[{slotIndex}]");
        if (!buffer.IsValid)
        {
            throw new InvalidOperationException("[GenericSkinPaletteBuffer] Failed to create the skin palette buffer.");
        }

        uint bindlessIndex = factory.RegisterBindlessResourceBuffer(buffer);
        if (bindlessIndex == InvalidBindlessIndex)
        {
            factory.ReleaseBuffer(buffer);
            throw new InvalidOperationException("[GenericSkinPaletteBuffer] Failed to register the skin palette buffer in the bindless table.");
        }

        slot = new Slot(buffer, bindlessIndex, matrixCount);
        SetActive(slot);
        Logger.Log(
            $"[GenericSkinPaletteBuffer] Created palette buffer | Slot: {slotIndex}/{m_RingSize} | Matrices: {matrixCount} | Bytes: {byteSize} | Buffer: {buffer.Index}:{buffer.Generation} | Bindless: {bindlessIndex}");
    }

    private void SetActive(Slot slot)
    {
        Buffer = slot.Buffer;
        BindlessIndex = slot.BindlessIndex;
        m_Capacity = slot.Capacity;
    }

    private unsafe void Upload(int matrixCount)
    {
        if (!Buffer.IsValid || matrixCount <= 0)
        {
            return;
        }

        var mapped = m_Factory.MapBuffer(Buffer);
        if (mapped == IntPtr.Zero)
        {
            throw new InvalidOperationException("[GenericSkinPaletteBuffer] Failed to map the skin palette buffer.");
        }

        try
        {
            var source = new ReadOnlySpan<SkinPaletteMatrix>(m_Palettes, 0, matrixCount);
            source.CopyTo(new Span<SkinPaletteMatrix>(mapped.ToPointer(), matrixCount));
        }
        finally
        {
            m_Factory.UnmapBuffer(Buffer);
        }
    }

    private void ReleaseSlot(int slotIndex)
    {
        if ((uint)slotIndex >= (uint)m_Slots.Length)
        {
            return;
        }

        ref Slot slot = ref m_Slots[slotIndex];
        if (m_Factory.IsValid && slot.Buffer.IsValid)
        {
            if (slot.BindlessIndex != InvalidBindlessIndex)
            {
                m_Factory.UnregisterBindlessResourceBuffer(slot.BindlessIndex);
            }

            m_Factory.ReleaseBuffer(slot.Buffer);
        }

        slot = default;
    }

    private struct Slot
    {
        public readonly RHIBufferHandle Buffer;
        public readonly uint BindlessIndex;
        public readonly int Capacity;

        public Slot(RHIBufferHandle buffer, uint bindlessIndex, int capacity)
        {
            Buffer = buffer;
            BindlessIndex = bindlessIndex;
            Capacity = capacity;
        }
    }
}
