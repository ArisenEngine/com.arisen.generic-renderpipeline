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
