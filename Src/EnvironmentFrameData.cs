using System.Numerics;
using System.Runtime.InteropServices;
using Arisen.Native.RHI;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.RHI;
using ArisenEngine.Rendering.Resources;

namespace ArisenEngine.Rendering;

internal readonly struct EnvironmentFrameData
{
    internal EnvironmentFrameData(
        RHIBufferHandle constantsBuffer,
        uint constantsBufferBindlessIndex,
        in OutdoorEnvironmentProfile profile,
        float effectiveExposure)
    {
        ConstantsBuffer = constantsBuffer;
        ConstantsBufferBindlessIndex = constantsBufferBindlessIndex;
        Profile = profile;
        EffectiveExposure = effectiveExposure;
    }

    public RHIBufferHandle ConstantsBuffer { get; }
    public uint ConstantsBufferBindlessIndex { get; }
    public OutdoorEnvironmentProfile Profile { get; }
    public float EffectiveExposure { get; }
    public bool AtmosphereEnabled => Profile.IsAtmosphereEnabled;
    public bool IsValid =>
        ConstantsBuffer.IsValid &&
        ConstantsBufferBindlessIndex != EnvironmentFrameBuffer.InvalidBindlessIndex;
}

internal sealed class EnvironmentFrameBuffer : IDisposable
{
    internal const uint InvalidBindlessIndex = 0xFFFFFFFFu;
    private const int DefaultRingSize = 2;

    private EnvironmentBufferSlot[] m_Slots = Array.Empty<EnvironmentBufferSlot>();
    private RHIFactory m_Factory;
    private int m_RingSize;
    private bool m_Disposed;

    public unsafe EnvironmentFrameData Prepare(
        RenderContext context,
        in SceneEnvironment environment,
        in DirectionalLight directionalLight,
        RHIEnvironmentTextureResource? environmentTexture,
        RenderGraphTexture frameDepthTexture,
        in OutdoorEnvironmentProfile profile,
        DeviceDepthConvention depthConvention)
    {
        ObjectDisposedException.ThrowIf(m_Disposed, this);
        ArgumentNullException.ThrowIfNull(frameDepthTexture);
        if (!context.Device.IsValid)
        {
            throw new ArgumentException(
                "[EnvironmentFrameBuffer] RHI device is invalid.",
                nameof(context));
        }

        profile.Validate("environment frame");
        int ringSize = GetRingSize(context);
        EnsureRing(context.Device.GetFactory(), ringSize);
        int slotIndex = checked((int)(context.FrameResourceIndex % (uint)ringSize));
        ref EnvironmentBufferSlot slot = ref m_Slots[slotIndex];
        EnsureSlot(ref slot, slotIndex);

        float effectiveExposure = SceneEnvironment.NormalizeExposure(
            profile.ResolveExposure(environment.Exposure));
        EnvironmentGpuConstants constants = EnvironmentGpuConstants.Create(
            context,
            environment,
            directionalLight,
            environmentTexture,
            frameDepthTexture,
            profile,
            effectiveExposure,
            depthConvention);
        IntPtr mapped = m_Factory.MapBuffer(slot.Buffer);
        if (mapped == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "[EnvironmentFrameBuffer] Failed to map environment constants buffer.");
        }

        try
        {
            *(EnvironmentGpuConstants*)mapped.ToPointer() = constants;
        }
        finally
        {
            m_Factory.UnmapBuffer(slot.Buffer);
        }

        return new EnvironmentFrameData(
            slot.Buffer,
            slot.BindlessIndex,
            profile,
            effectiveExposure);
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

        m_Slots = Array.Empty<EnvironmentBufferSlot>();
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
                "[EnvironmentFrameBuffer] Cannot resize an active environment buffer ring.");
        }

        m_Factory = factory;
        m_RingSize = ringSize;
        m_Slots = new EnvironmentBufferSlot[ringSize];
    }

    private unsafe void EnsureSlot(ref EnvironmentBufferSlot slot, int slotIndex)
    {
        if (slot.IsValid)
        {
            return;
        }

        ulong byteSize = (ulong)sizeof(EnvironmentGpuConstants);
        RHIBufferHandle buffer = m_Factory.CreateBuffer(
            byteSize,
            (uint)EBufferUsageFlagBits.BUFFER_USAGE_STORAGE_BUFFER_BIT,
            ESharingMode.SHARING_MODE_EXCLUSIVE,
            ERHIMemoryUsage.Upload,
            $"Environment.FrameData[{slotIndex}]");
        if (!buffer.IsValid)
        {
            throw new InvalidOperationException(
                "[EnvironmentFrameBuffer] Failed to create environment constants buffer.");
        }

        uint bindlessIndex = m_Factory.RegisterBindlessResourceBuffer(buffer);
        if (bindlessIndex == InvalidBindlessIndex)
        {
            m_Factory.ReleaseBuffer(buffer);
            throw new InvalidOperationException(
                "[EnvironmentFrameBuffer] Failed to register environment constants buffer.");
        }

        slot = new EnvironmentBufferSlot(buffer, bindlessIndex);
        Logger.Log(
            $"[EnvironmentFrameBuffer] Created slot | Slot: {slotIndex}/{m_RingSize} | " +
            $"Bytes: {byteSize} | Buffer: {buffer.Index}:{buffer.Generation} | Bindless: {bindlessIndex}");
    }

    private void ReleaseSlot(ref EnvironmentBufferSlot slot)
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

    private readonly struct EnvironmentBufferSlot
    {
        public EnvironmentBufferSlot(RHIBufferHandle buffer, uint bindlessIndex)
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
internal readonly struct EnvironmentGpuConstants
{
    private const uint InvalidBindlessIndex = 0xFFFFFFFFu;
    public const int VectorCount = 16;

    private readonly Vector4 m_SkyColorIntensity;
    private readonly Vector4 m_HorizonColor;
    private readonly Vector4 m_GroundColor;
    private readonly Vector4 m_CameraRightTanHalfFov;
    private readonly Vector4 m_CameraUpAspect;
    private readonly Vector4 m_CameraForwardProjection;
    private readonly Vector4 m_CameraPositionNearClip;
    private readonly Vector4 m_SunDirectionIntensity;
    private readonly Vector4 m_SunColorCoupling;
    private readonly Vector4 m_PanoramaResources;
    private readonly Vector4 m_SkyProfile;
    private readonly Vector4 m_SunProfileExposure;
    private readonly Vector4 m_AerialProfile;
    private readonly Vector4 m_HeightFogProfile;
    private readonly Vector4 m_DepthResources;
    private readonly Vector4 m_CameraDepthProjection;

    private EnvironmentGpuConstants(
        Vector4 skyColorIntensity,
        Vector4 horizonColor,
        Vector4 groundColor,
        Vector4 cameraRightTanHalfFov,
        Vector4 cameraUpAspect,
        Vector4 cameraForwardProjection,
        Vector4 cameraPositionNearClip,
        Vector4 sunDirectionIntensity,
        Vector4 sunColorCoupling,
        Vector4 panoramaResources,
        Vector4 skyProfile,
        Vector4 sunProfileExposure,
        Vector4 aerialProfile,
        Vector4 heightFogProfile,
        Vector4 depthResources,
        Vector4 cameraDepthProjection)
    {
        m_SkyColorIntensity = skyColorIntensity;
        m_HorizonColor = horizonColor;
        m_GroundColor = groundColor;
        m_CameraRightTanHalfFov = cameraRightTanHalfFov;
        m_CameraUpAspect = cameraUpAspect;
        m_CameraForwardProjection = cameraForwardProjection;
        m_CameraPositionNearClip = cameraPositionNearClip;
        m_SunDirectionIntensity = sunDirectionIntensity;
        m_SunColorCoupling = sunColorCoupling;
        m_PanoramaResources = panoramaResources;
        m_SkyProfile = skyProfile;
        m_SunProfileExposure = sunProfileExposure;
        m_AerialProfile = aerialProfile;
        m_HeightFogProfile = heightFogProfile;
        m_DepthResources = depthResources;
        m_CameraDepthProjection = cameraDepthProjection;
    }

    public static EnvironmentGpuConstants Create(
        RenderContext context,
        in SceneEnvironment environment,
        in DirectionalLight directionalLight,
        RHIEnvironmentTextureResource? environmentTexture,
        RenderGraphTexture frameDepthTexture,
        in OutdoorEnvironmentProfile profile,
        float effectiveExposure,
        DeviceDepthConvention depthConvention)
    {
        Camera camera = GetCamera(context);
        Matrix4x4 rotation = Matrix4x4.CreateFromYawPitchRoll(
            camera.Rotation.Y * (MathF.PI / 180.0f),
            camera.Rotation.X * (MathF.PI / 180.0f),
            camera.Rotation.Z * (MathF.PI / 180.0f));
        Vector3 right = NormalizeOrFallback(
            Vector3.Transform(Vector3.UnitX, rotation),
            Vector3.UnitX);
        Vector3 up = NormalizeOrFallback(
            Vector3.Transform(Vector3.UnitY, rotation),
            Vector3.UnitY);
        Vector3 forward = NormalizeOrFallback(
            Vector3.Transform(Vector3.UnitZ, rotation),
            Vector3.UnitZ);
        Vector3 sunDirection = NormalizeOrFallback(
            directionalLight.Direction,
            DirectionalLight.Default.Direction);
        float tanHalfFov = MathF.Tan(
            Math.Clamp(camera.FieldOfView, 1.0f, 179.0f) *
            (MathF.PI / 360.0f));
        float aspect = camera.AspectRatio > 0.0f && float.IsFinite(camera.AspectRatio)
            ? camera.AspectRatio
            : context.Height > 0
                ? context.Width / (float)context.Height
                : 1.0f;
        uint environmentImageIndex = environmentTexture is { IsValid: true }
            ? environmentTexture.BindlessImageIndex
            : InvalidBindlessIndex;
        uint environmentSamplerIndex = environmentTexture is { IsValid: true }
            ? environmentTexture.BindlessSamplerIndex
            : InvalidBindlessIndex;
        float environmentRotation = environmentTexture is { IsValid: true }
            ? environmentTexture.RotationRadians
            : 0.0f;
        float environmentIntensity = environmentTexture is { IsValid: true }
            ? environmentTexture.Intensity
            : 0.0f;
        uint depthImageIndex = profile.IsAtmosphereEnabled
            ? frameDepthTexture.BindlessImageIndex
            : InvalidBindlessIndex;
        double baseHeightRelativeDouble = profile.HeightFogBaseHeight - context.RenderOrigin.Y;
        float baseHeightRelative = (float)Math.Clamp(
            baseHeightRelativeDouble,
            -OutdoorEnvironmentProfile.MaximumAbsoluteHeight,
            OutdoorEnvironmentProfile.MaximumAbsoluteHeight);

        return new EnvironmentGpuConstants(
            new Vector4(environment.SkyColor, MathF.Max(0.0f, environment.SkyIntensity)),
            new Vector4(environment.HorizonColor, 0.0f),
            new Vector4(environment.GroundColor, 0.0f),
            new Vector4(right, tanHalfFov),
            new Vector4(up, aspect),
            new Vector4(forward, AsFloat((uint)camera.ProjectionType)),
            new Vector4(camera.Position, camera.NearClip),
            new Vector4(sunDirection, MathF.Max(0.0f, directionalLight.Intensity)),
            new Vector4(Vector3.Max(Vector3.Zero, directionalLight.Color), profile.SunSkyCoupling),
            new Vector4(
                AsFloat(environmentImageIndex),
                AsFloat(environmentSamplerIndex),
                environmentRotation,
                MathF.Max(0.0f, environmentIntensity)),
            new Vector4(
                AsFloat((uint)profile.SkyMode),
                profile.HorizonExponent,
                profile.ZenithExponent,
                profile.SunAngularRadiusDegrees * (MathF.PI / 180.0f)),
            new Vector4(
                profile.SunDiscIntensity,
                profile.SunGlowIntensity,
                profile.SunGlowExponent,
                effectiveExposure),
            new Vector4(
                AsFloat(profile.AerialPerspectiveEnabled ? 1u : 0u),
                profile.AerialStartDistance,
                profile.AerialDistance,
                profile.AerialStrength),
            new Vector4(
                AsFloat(profile.HeightFogEnabled ? 1u : 0u),
                baseHeightRelative,
                profile.HeightFogDensity,
                profile.HeightFogFalloff),
            new Vector4(
                AsFloat(depthImageIndex),
                frameDepthTexture.Width,
                frameDepthTexture.Height,
                AsFloat((uint)depthConvention)),
            new Vector4(
                camera.NearClip,
                camera.FarClip,
                AsFloat((uint)camera.ProjectionType),
                camera.OrthographicSize));
    }

    private static Camera GetCamera(RenderContext context)
    {
        if (context.CameraCount > 0)
        {
            return context.Cameras[0];
        }

        return new Camera
        {
            FieldOfView = 60.0f,
            NearClip = 0.1f,
            FarClip = 1000.0f,
            AspectRatio = context.Height > 0
                ? context.Width / (float)context.Height
                : 1.0f,
            OrthographicSize = 5.0f,
            ProjectionType = CameraProjectionType.Perspective,
            Position = Vector3.Zero,
            Rotation = Vector3.Zero
        };
    }

    private static Vector3 NormalizeOrFallback(Vector3 value, Vector3 fallback)
    {
        return float.IsFinite(value.X) &&
               float.IsFinite(value.Y) &&
               float.IsFinite(value.Z) &&
               value.LengthSquared() > 1.0e-8f
            ? Vector3.Normalize(value)
            : fallback;
    }

    private static float AsFloat(uint value) => BitConverter.UInt32BitsToSingle(value);
}
