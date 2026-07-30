using ArisenEngine.Rendering.Resources;
using System.Numerics;

namespace ArisenEngine.Rendering;

internal readonly record struct DirectionalShadowProjection(
    Matrix4x4 ViewProjection,
    bool IsSceneFitted,
    float Diameter,
    float Depth,
    float WorldUnitsPerTexel,
    Vector2 SnappedLightSpaceCenter);

public readonly record struct DirectionalShadowCascade(
    Matrix4x4 ViewProjection,
    float SplitNear,
    float SplitFar,
    float TransitionStart,
    float Diameter,
    float Depth,
    float WorldUnitsPerTexel,
    Vector2 SnappedLightSpaceCenter);

public readonly struct DirectionalShadowCascadeSet
{
    private readonly DirectionalShadowCascade m_Cascade0;
    private readonly DirectionalShadowCascade m_Cascade1;
    private readonly DirectionalShadowCascade m_Cascade2;
    private readonly DirectionalShadowCascade m_Cascade3;

    internal DirectionalShadowCascadeSet(
        int count,
        Vector3 cameraPosition,
        float nearClip,
        float maximumDistance,
        float terminalFadeStart,
        in DirectionalShadowCascade cascade0,
        in DirectionalShadowCascade cascade1,
        in DirectionalShadowCascade cascade2,
        in DirectionalShadowCascade cascade3)
    {
        Count = count;
        CameraPosition = cameraPosition;
        NearClip = nearClip;
        MaximumDistance = maximumDistance;
        TerminalFadeStart = terminalFadeStart;
        m_Cascade0 = cascade0;
        m_Cascade1 = cascade1;
        m_Cascade2 = cascade2;
        m_Cascade3 = cascade3;
    }

    public int Count { get; }
    public Vector3 CameraPosition { get; }
    public float NearClip { get; }
    public float MaximumDistance { get; }
    public float TerminalFadeStart { get; }
    public bool IsValid => Count is >= 1 and <= 4 && MaximumDistance > NearClip;

    public DirectionalShadowCascade GetCascade(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return index switch
        {
            0 => m_Cascade0,
            1 => m_Cascade1,
            2 => m_Cascade2,
            _ => m_Cascade3
        };
    }
}

public readonly record struct DirectionalShadowCascadeDrawRange(int Start, int Count)
{
    public int End => checked(Start + Count);
    public bool IsEmpty => Count == 0;
}

public readonly struct DirectionalShadowCascadeDrawRangeSet
{
    private readonly DirectionalShadowCascadeDrawRange m_Range0;
    private readonly DirectionalShadowCascadeDrawRange m_Range1;
    private readonly DirectionalShadowCascadeDrawRange m_Range2;
    private readonly DirectionalShadowCascadeDrawRange m_Range3;

    public DirectionalShadowCascadeDrawRangeSet(
        int count,
        int totalDrawCount,
        int droppedDrawCount,
        in DirectionalShadowCascadeDrawRange range0,
        in DirectionalShadowCascadeDrawRange range1,
        in DirectionalShadowCascadeDrawRange range2,
        in DirectionalShadowCascadeDrawRange range3)
    {
        if (count < 1 || count > 4 || totalDrawCount < 0 || droppedDrawCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        Span<DirectionalShadowCascadeDrawRange> ranges =
            stackalloc DirectionalShadowCascadeDrawRange[4]
            {
                range0,
                range1,
                range2,
                range3
            };
        int expectedStart = 0;
        for (int index = 0; index < count; index++)
        {
            if (ranges[index].Start < 0 ||
                ranges[index].Count < 0 ||
                ranges[index].End > totalDrawCount ||
                ranges[index].Start != expectedStart)
            {
                throw new ArgumentOutOfRangeException(nameof(range0));
            }

            expectedStart = ranges[index].End;
        }

        if (expectedStart != totalDrawCount)
        {
            throw new ArgumentException(
                "Cascade draw ranges must compactly cover the complete draw buffer.",
                nameof(totalDrawCount));
        }

        Count = count;
        TotalDrawCount = totalDrawCount;
        DroppedDrawCount = droppedDrawCount;
        m_Range0 = range0;
        m_Range1 = range1;
        m_Range2 = range2;
        m_Range3 = range3;
    }

    public int Count { get; }
    public int TotalDrawCount { get; }
    public int DroppedDrawCount { get; }

    public DirectionalShadowCascadeDrawRange GetRange(int cascadeIndex)
    {
        if ((uint)cascadeIndex >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(cascadeIndex));
        }

        return cascadeIndex switch
        {
            0 => m_Range0,
            1 => m_Range1,
            2 => m_Range2,
            _ => m_Range3
        };
    }
}

public static class DirectionalShadowCoordinateSpace
{
    public static Matrix4x4 ToCameraRelative(
        in Matrix4x4 localToWorld,
        Vector3 cameraPosition)
    {
        Matrix4x4 result = localToWorld;
        result.M41 -= cameraPosition.X;
        result.M42 -= cameraPosition.Y;
        result.M43 -= cameraPosition.Z;
        return result;
    }

    public static MeshBounds ToCameraRelative(
        in MeshBounds bounds,
        Vector3 cameraPosition)
    {
        return new MeshBounds(
            bounds.Min - cameraPosition,
            bounds.Max - cameraPosition);
    }
}

internal struct DirectionalShadowBoundsAccumulator
{
    private Vector3 m_Min;
    private Vector3 m_Max;

    public bool IsValid { get; private set; }
    public int Count { get; private set; }
    public MeshBounds Bounds => IsValid ? new MeshBounds(m_Min, m_Max) : MeshBounds.Empty;

    public bool Add(in MeshBounds bounds)
    {
        if (!DirectionalShadowFitter.HasUsableBounds(bounds))
        {
            return false;
        }

        if (!IsValid)
        {
            m_Min = bounds.Min;
            m_Max = bounds.Max;
            IsValid = true;
        }
        else
        {
            m_Min = Vector3.Min(m_Min, bounds.Min);
            m_Max = Vector3.Max(m_Max, bounds.Max);
        }

        Count++;
        return true;
    }
}

internal static class DirectionalShadowFitter
{
    private const int MaximumCascadeCount = 4;
    private const float ShowcaseShadowDiameter = 10.0f;
    private const float ShowcaseShadowCenterY = 0.75f;
    private const float ShowcaseShadowEyeDistance = 11.0f;
    private const float ShowcaseShadowDepth = 24.0f;
    private const float MinimumNearPlane = 0.1f;
    private const float MinimumDiameter = 0.5f;
    private const float MinimumXYPadding = 0.25f;
    private const float RelativeXYPadding = 0.05f;
    private const float MinimumDepthPadding = 1.0f;
    private const float RelativeDepthPadding = 0.10f;
    private const float CascadeTransitionFraction = 0.10f;
    private const float RadiusQuantization = 16.0f;

    public static DirectionalShadowCascadeSet CreateCascades(
        in Camera camera,
        Vector3 directionToLight,
        in GenericShadowSettings settings)
    {
        int cascadeCount = Math.Clamp(settings.CascadeCount, 1, MaximumCascadeCount);
        float nearClip = float.IsFinite(camera.NearClip) && camera.NearClip > 0.0f
            ? camera.NearClip
            : MinimumNearPlane;
        float cameraFarClip = float.IsFinite(camera.FarClip) && camera.FarClip > nearClip
            ? camera.FarClip
            : settings.MaximumDistance;
        float configuredDistance = float.IsFinite(settings.MaximumDistance)
            ? settings.MaximumDistance
            : GenericShadowSettings.Default.MaximumDistance;
        float maximumDistance = MathF.Min(cameraFarClip, configuredDistance);
        maximumDistance = MathF.Max(maximumDistance, nearClip + MinimumDiameter);
        float splitWeight = float.IsFinite(settings.PracticalSplitWeight)
            ? Math.Clamp(settings.PracticalSplitWeight, 0.0f, 1.0f)
            : GenericShadowSettings.Default.PracticalSplitWeight;
        float terminalFadeFraction = float.IsFinite(settings.TerminalFadeFraction)
            ? Math.Clamp(settings.TerminalFadeFraction, 0.0f, 0.5f)
            : GenericShadowSettings.Default.TerminalFadeFraction;

        Camera fittingCamera = camera;
        fittingCamera.Position = Vector3.Zero;
        Matrix4x4 viewProjection = fittingCamera.ViewMatrix * fittingCamera.ProjectionMatrix;
        if (!Matrix4x4.Invert(viewProjection, out Matrix4x4 inverseViewProjection))
        {
            throw new InvalidOperationException(
                "[DirectionalShadowFitter] Camera view-projection matrix is not invertible.");
        }

        Span<Vector3> frustumNear = stackalloc Vector3[4];
        Span<Vector3> frustumFar = stackalloc Vector3[4];
        ExtractFrustumCorners(inverseViewProjection, frustumNear, frustumFar);

        Vector3 lightDirection = NormalizeOrDefault(directionToLight);
        uint shadowMapSize = settings.MapSize;
        Span<DirectionalShadowCascade> cascades =
            stackalloc DirectionalShadowCascade[MaximumCascadeCount];
        Span<Vector3> sliceCorners = stackalloc Vector3[8];
        float previousSplit = nearClip;
        for (int cascadeIndex = 0; cascadeIndex < cascadeCount; cascadeIndex++)
        {
            float splitFar = CalculatePracticalSplit(
                nearClip,
                maximumDistance,
                cascadeIndex + 1,
                cascadeCount,
                splitWeight);
            float nearInterpolation = NormalizeDepth(
                previousSplit,
                nearClip,
                cameraFarClip);
            float farInterpolation = NormalizeDepth(
                splitFar,
                nearClip,
                cameraFarClip);
            for (int cornerIndex = 0; cornerIndex < 4; cornerIndex++)
            {
                Vector3 ray = frustumFar[cornerIndex] - frustumNear[cornerIndex];
                sliceCorners[cornerIndex] =
                    frustumNear[cornerIndex] + ray * nearInterpolation;
                sliceCorners[cornerIndex + 4] =
                    frustumNear[cornerIndex] + ray * farInterpolation;
            }

            cascades[cascadeIndex] = FitCascade(
                lightDirection,
                sliceCorners,
                previousSplit,
                splitFar,
                shadowMapSize);
            previousSplit = splitFar;
        }

        float terminalFadeStart = MathF.Max(
            nearClip,
            maximumDistance * (1.0f - terminalFadeFraction));
        return new DirectionalShadowCascadeSet(
            cascadeCount,
            camera.Position,
            nearClip,
            maximumDistance,
            terminalFadeStart,
            cascades[0],
            cascades[1],
            cascades[2],
            cascades[3]);
    }

    internal static float CalculatePracticalSplit(
        float nearClip,
        float farClip,
        int splitIndex,
        int cascadeCount,
        float practicalSplitWeight)
    {
        if (!float.IsFinite(nearClip) ||
            !float.IsFinite(farClip) ||
            nearClip <= 0.0f ||
            farClip <= nearClip)
        {
            throw new ArgumentOutOfRangeException(nameof(farClip));
        }

        if (cascadeCount < 1 || splitIndex < 1 || splitIndex > cascadeCount)
        {
            throw new ArgumentOutOfRangeException(nameof(splitIndex));
        }

        float ratio = splitIndex / (float)cascadeCount;
        float uniform = nearClip + (farClip - nearClip) * ratio;
        float logarithmic = nearClip * MathF.Pow(farClip / nearClip, ratio);
        float weight = Math.Clamp(practicalSplitWeight, 0.0f, 1.0f);
        return splitIndex == cascadeCount
            ? farClip
            : uniform + (logarithmic - uniform) * weight;
    }

    public static DirectionalShadowProjection Create(
        Vector3 directionToLight,
        in MeshBounds receiverBounds,
        uint shadowMapSize)
    {
        var lightDirection = NormalizeOrDefault(directionToLight);
        if (!HasUsableBounds(receiverBounds) || shadowMapSize == 0)
        {
            return CreateShowcaseFallback(lightDirection, shadowMapSize);
        }

        var up = SelectUp(lightDirection);
        var orientationView = Matrix4x4.CreateLookAt(
            Vector3.Zero,
            -lightDirection,
            up);

        GetLightSpaceExtents(
            receiverBounds,
            orientationView,
            out var minimum,
            out var maximum);

        float width = maximum.X - minimum.X;
        float height = maximum.Y - minimum.Y;
        float unpaddedDiameter = MathF.Max(width, height);
        float xyPadding = MathF.Max(MinimumXYPadding, unpaddedDiameter * RelativeXYPadding);
        float diameter = MathF.Max(MinimumDiameter, unpaddedDiameter + xyPadding * 2.0f);
        float worldUnitsPerTexel = diameter / shadowMapSize;

        var center = new Vector2(
            (minimum.X + maximum.X) * 0.5f,
            (minimum.Y + maximum.Y) * 0.5f);
        var snappedCenter = new Vector2(
            SnapToIncrement(center.X, worldUnitsPerTexel),
            SnapToIncrement(center.Y, worldUnitsPerTexel));

        float unpaddedDepth = maximum.Z - minimum.Z;
        float depthPadding = MathF.Max(MinimumDepthPadding, unpaddedDepth * RelativeDepthPadding);
        float eyeDistance = maximum.Z + depthPadding + MinimumNearPlane;
        float farPlane = MinimumNearPlane + unpaddedDepth + depthPadding * 2.0f;
        var eye = lightDirection * eyeDistance;
        var view = Matrix4x4.CreateLookAt(eye, eye - lightDirection, up);
        float halfDiameter = diameter * 0.5f;
        var projection = Matrix4x4.CreateOrthographicOffCenter(
            snappedCenter.X - halfDiameter,
            snappedCenter.X + halfDiameter,
            snappedCenter.Y - halfDiameter,
            snappedCenter.Y + halfDiameter,
            MinimumNearPlane,
            farPlane);

        return new DirectionalShadowProjection(
            view * projection,
            IsSceneFitted: true,
            diameter,
            farPlane - MinimumNearPlane,
            worldUnitsPerTexel,
            snappedCenter);
    }

    public static bool HasUsableBounds(in MeshBounds bounds)
    {
        if (!IsFinite(bounds.Min) || !IsFinite(bounds.Max))
        {
            return false;
        }

        var size = bounds.Max - bounds.Min;
        return size.X >= 0.0f &&
               size.Y >= 0.0f &&
               size.Z >= 0.0f &&
               (size.X > 1.0e-6f || size.Y > 1.0e-6f || size.Z > 1.0e-6f);
    }

    private static DirectionalShadowCascade FitCascade(
        Vector3 lightDirection,
        ReadOnlySpan<Vector3> corners,
        float splitNear,
        float splitFar,
        uint shadowMapSize)
    {
        Vector3 center = Vector3.Zero;
        for (int index = 0; index < corners.Length; index++)
        {
            center += corners[index];
        }
        center /= corners.Length;

        float radius = 0.0f;
        for (int index = 0; index < corners.Length; index++)
        {
            radius = MathF.Max(radius, Vector3.Distance(center, corners[index]));
        }
        radius = MathF.Ceiling(MathF.Max(radius, MinimumDiameter * 0.5f) * RadiusQuantization) /
            RadiusQuantization;
        float diameter = radius * 2.0f;
        float worldUnitsPerTexel = shadowMapSize > 0
            ? diameter / shadowMapSize
            : 0.0f;

        Vector3 up = SelectUp(lightDirection);
        Matrix4x4 orientationView = Matrix4x4.CreateLookAt(
            Vector3.Zero,
            -lightDirection,
            up);
        Vector3 lightSpaceCenter = Vector3.Transform(center, orientationView);
        var snappedCenter = new Vector2(
            SnapToIncrement(lightSpaceCenter.X, worldUnitsPerTexel),
            SnapToIncrement(lightSpaceCenter.Y, worldUnitsPerTexel));

        float minimumZ = float.PositiveInfinity;
        float maximumZ = float.NegativeInfinity;
        for (int index = 0; index < corners.Length; index++)
        {
            float z = Vector3.Transform(corners[index], orientationView).Z;
            minimumZ = MathF.Min(minimumZ, z);
            maximumZ = MathF.Max(maximumZ, z);
        }

        float unpaddedDepth = MathF.Max(maximumZ - minimumZ, MinimumDiameter);
        float depthPadding = MathF.Max(
            MinimumDepthPadding,
            MathF.Max(unpaddedDepth, diameter) * RelativeDepthPadding);
        float eyeDistance = maximumZ + depthPadding + MinimumNearPlane;
        float farPlane = MinimumNearPlane + unpaddedDepth + depthPadding * 2.0f;
        Vector3 eye = lightDirection * eyeDistance;
        Matrix4x4 view = Matrix4x4.CreateLookAt(eye, eye - lightDirection, up);
        float halfDiameter = diameter * 0.5f;
        Matrix4x4 projection = Matrix4x4.CreateOrthographicOffCenter(
            snappedCenter.X - halfDiameter,
            snappedCenter.X + halfDiameter,
            snappedCenter.Y - halfDiameter,
            snappedCenter.Y + halfDiameter,
            MinimumNearPlane,
            farPlane);
        float transitionWidth = MathF.Max(
            (splitFar - splitNear) * CascadeTransitionFraction,
            worldUnitsPerTexel * 2.0f);

        return new DirectionalShadowCascade(
            view * projection,
            splitNear,
            splitFar,
            MathF.Max(splitNear, splitFar - transitionWidth),
            diameter,
            farPlane - MinimumNearPlane,
            worldUnitsPerTexel,
            snappedCenter);
    }

    private static void ExtractFrustumCorners(
        in Matrix4x4 inverseViewProjection,
        Span<Vector3> nearCorners,
        Span<Vector3> farCorners)
    {
        for (int cornerIndex = 0; cornerIndex < 4; cornerIndex++)
        {
            float x = (cornerIndex & 1) == 0 ? -1.0f : 1.0f;
            float y = (cornerIndex & 2) == 0 ? -1.0f : 1.0f;
            nearCorners[cornerIndex] = TransformClipCorner(
                new Vector4(x, y, 0.0f, 1.0f),
                inverseViewProjection);
            farCorners[cornerIndex] = TransformClipCorner(
                new Vector4(x, y, 1.0f, 1.0f),
                inverseViewProjection);
        }
    }

    private static Vector3 TransformClipCorner(
        Vector4 clipCorner,
        in Matrix4x4 inverseViewProjection)
    {
        Vector4 world = Vector4.Transform(clipCorner, inverseViewProjection);
        if (!float.IsFinite(world.W) || MathF.Abs(world.W) <= 1.0e-7f)
        {
            throw new InvalidOperationException(
                "[DirectionalShadowFitter] Camera frustum contains an invalid homogeneous corner.");
        }

        Vector3 result = new(world.X / world.W, world.Y / world.W, world.Z / world.W);
        if (!IsFinite(result))
        {
            throw new InvalidOperationException(
                "[DirectionalShadowFitter] Camera frustum contains a non-finite corner.");
        }

        return result;
    }

    private static float NormalizeDepth(float depth, float nearClip, float farClip)
    {
        return Math.Clamp((depth - nearClip) / (farClip - nearClip), 0.0f, 1.0f);
    }

    private static DirectionalShadowProjection CreateShowcaseFallback(
        Vector3 lightDirection,
        uint shadowMapSize)
    {
        var center = new Vector3(0.0f, ShowcaseShadowCenterY, 0.0f);
        var eye = center + lightDirection * ShowcaseShadowEyeDistance;
        var view = Matrix4x4.CreateLookAt(eye, center, SelectUp(lightDirection));
        var projection = Matrix4x4.CreateOrthographic(
            ShowcaseShadowDiameter,
            ShowcaseShadowDiameter,
            MinimumNearPlane,
            ShowcaseShadowDepth);
        float worldUnitsPerTexel = shadowMapSize > 0
            ? ShowcaseShadowDiameter / shadowMapSize
            : 0.0f;
        return new DirectionalShadowProjection(
            view * projection,
            IsSceneFitted: false,
            ShowcaseShadowDiameter,
            ShowcaseShadowDepth - MinimumNearPlane,
            worldUnitsPerTexel,
            Vector2.Zero);
    }

    private static void GetLightSpaceExtents(
        in MeshBounds bounds,
        in Matrix4x4 view,
        out Vector3 minimum,
        out Vector3 maximum)
    {
        minimum = new Vector3(float.PositiveInfinity);
        maximum = new Vector3(float.NegativeInfinity);
        for (int cornerIndex = 0; cornerIndex < 8; cornerIndex++)
        {
            var corner = new Vector3(
                (cornerIndex & 1) == 0 ? bounds.Min.X : bounds.Max.X,
                (cornerIndex & 2) == 0 ? bounds.Min.Y : bounds.Max.Y,
                (cornerIndex & 4) == 0 ? bounds.Min.Z : bounds.Max.Z);
            var lightSpaceCorner = Vector3.Transform(corner, view);
            minimum = Vector3.Min(minimum, lightSpaceCorner);
            maximum = Vector3.Max(maximum, lightSpaceCorner);
        }
    }

    private static Vector3 NormalizeOrDefault(Vector3 direction)
    {
        return IsFinite(direction) && direction.LengthSquared() > 1.0e-8f
            ? Vector3.Normalize(direction)
            : Vector3.Normalize(DirectionalLight.Default.Direction);
    }

    private static Vector3 SelectUp(Vector3 lightDirection)
    {
        return MathF.Abs(Vector3.Dot(lightDirection, Vector3.UnitY)) > 0.92f
            ? Vector3.UnitZ
            : Vector3.UnitY;
    }

    private static float SnapToIncrement(float value, float increment)
    {
        if (!float.IsFinite(value) || !float.IsFinite(increment) || increment <= 0.0f)
        {
            return value;
        }

        return MathF.Round(value / increment, MidpointRounding.AwayFromZero) * increment;
    }

    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.X) &&
               float.IsFinite(value.Y) &&
               float.IsFinite(value.Z);
    }
}
