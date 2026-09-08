using System.Numerics;

namespace AcDream.App.Rendering;

internal readonly record struct DirectionalShadowCascadeFitInput(
    Matrix4x4 CameraView,
    Matrix4x4 CameraProjection,
    Vector3 SurfaceToLightDirection,
    DirectionalShadowQuality Quality,
    float CameraNearMeters = 0.1f,
    float PracticalSplitLambda = 0.65f,
    float CasterDepthPaddingMeters = 48f,
    float ResidentMaximumReachMeters = float.PositiveInfinity);

internal readonly record struct DirectionalShadowCascade(
    int Index,
    float SplitNearMeters,
    float SplitFarMeters,
    Matrix4x4 LightView,
    Matrix4x4 LightProjection,
    Matrix4x4 WorldToShadowClip,
    Vector2 StabilizedLightSpaceCenter,
    float HalfExtentMeters,
    float TexelWorldSize,
    float CasterDepthPaddingMeters,
    DirectionalShadowWorldBias Bias);

internal static class DirectionalShadowCascadeFitter
{
    private const float RadiusQuantizationMeters = 1f / 16f;

    public static int Fit(
        in DirectionalShadowCascadeFitInput input,
        Span<DirectionalShadowCascade> destination)
    {
        Validate(in input, destination.Length);
        if (!Matrix4x4.Invert(input.CameraView, out Matrix4x4 inverseView))
            throw new ArgumentException("Camera view matrix is not invertible.", nameof(input));
        if (!Matrix4x4.Invert(input.CameraProjection, out Matrix4x4 inverseProjection))
            throw new ArgumentException("Camera projection matrix is not invertible.", nameof(input));

        Vector3 lightDirection = Vector3.Normalize(input.SurfaceToLightDirection);
        float maximumReach = MathF.Min(
            input.Quality.MaximumReachMeters,
            input.ResidentMaximumReachMeters);
        if (maximumReach <= input.CameraNearMeters)
            return 0;
        float splitNear = input.CameraNearMeters;
        Span<Vector3> corners = stackalloc Vector3[8];
        for (int cascadeIndex = 0;
             cascadeIndex < input.Quality.CascadeCount;
             cascadeIndex++)
        {
            float splitFar = PracticalSplit(
                input.CameraNearMeters,
                maximumReach,
                cascadeIndex + 1,
                input.Quality.CascadeCount,
                input.PracticalSplitLambda);
            BuildFrustumSliceCorners(
                inverseView,
                inverseProjection,
                splitNear,
                splitFar,
                corners);
            destination[cascadeIndex] = FitCascade(
                cascadeIndex,
                splitNear,
                splitFar,
                corners,
                lightDirection,
                input.Quality.MapResolution,
                input.CasterDepthPaddingMeters,
                input.Quality.BiasPolicy);
            splitNear = splitFar;
        }

        return input.Quality.CascadeCount;
    }

    internal static float PracticalSplit(
        float nearMeters,
        float farMeters,
        int splitIndex,
        int splitCount,
        float lambda)
    {
        if (!float.IsFinite(nearMeters)
            || !float.IsFinite(farMeters)
            || nearMeters <= 0f
            || farMeters <= nearMeters)
        {
            throw new ArgumentOutOfRangeException(nameof(farMeters));
        }
        if (splitCount <= 0 || splitIndex <= 0 || splitIndex > splitCount)
            throw new ArgumentOutOfRangeException(nameof(splitIndex));
        if (!float.IsFinite(lambda) || lambda < 0f || lambda > 1f)
            throw new ArgumentOutOfRangeException(nameof(lambda));

        float fraction = (float)splitIndex / splitCount;
        float logarithmic = nearMeters * MathF.Pow(farMeters / nearMeters, fraction);
        float uniform = nearMeters + (farMeters - nearMeters) * fraction;
        return lambda * logarithmic + (1f - lambda) * uniform;
    }

    private static DirectionalShadowCascade FitCascade(
        int index,
        float splitNear,
        float splitFar,
        ReadOnlySpan<Vector3> corners,
        Vector3 surfaceToLight,
        int mapResolution,
        float depthPadding,
        in DirectionalShadowBiasPolicy biasPolicy)
    {
        Vector3 center = Vector3.Zero;
        for (int i = 0; i < corners.Length; i++)
            center += corners[i];
        center /= corners.Length;

        float radius = 0f;
        for (int i = 0; i < corners.Length; i++)
            radius = MathF.Max(radius, Vector3.Distance(center, corners[i]));
        radius = MathF.Ceiling(radius / RadiusQuantizationMeters)
            * RadiusQuantizationMeters;
        radius = MathF.Max(radius, RadiusQuantizationMeters);

        Vector3 up = StableLightUp(surfaceToLight);
        Matrix4x4 lightRotation = Matrix4x4.CreateLookAt(
            Vector3.Zero,
            -surfaceToLight,
            up);

        Vector3 lightCenter = Vector3.Transform(center, lightRotation);
        float texelWorldSize = (2f * radius) / mapResolution;
        float snappedX = SnapToTexel(lightCenter.X, texelWorldSize);
        float snappedY = SnapToTexel(lightCenter.Y, texelWorldSize);

        float minZ = float.PositiveInfinity;
        float maxZ = float.NegativeInfinity;
        for (int i = 0; i < corners.Length; i++)
        {
            float z = Vector3.Transform(corners[i], lightRotation).Z;
            minZ = MathF.Min(minZ, z);
            maxZ = MathF.Max(maxZ, z);
        }

        float eyeAxis = maxZ + depthPadding;
        Vector3 eye = surfaceToLight * eyeAxis;
        Matrix4x4 lightView = Matrix4x4.CreateLookAt(
            eye,
            eye - surfaceToLight,
            up);
        float nearPlane = 0.1f;
        float farPlane = MathF.Max(
            nearPlane + 0.1f,
            (maxZ - minZ) + 2f * depthPadding);
        Matrix4x4 lightProjection = Matrix4x4.CreateOrthographicOffCenter(
            snappedX - radius,
            snappedX + radius,
            snappedY - radius,
            snappedY + radius,
            nearPlane,
            farPlane);

        return new DirectionalShadowCascade(
            index,
            splitNear,
            splitFar,
            lightView,
            lightProjection,
            lightView * lightProjection,
            new Vector2(snappedX, snappedY),
            radius,
            texelWorldSize,
            depthPadding,
            biasPolicy.Resolve(texelWorldSize));
    }

    private static void BuildFrustumSliceCorners(
        Matrix4x4 inverseView,
        Matrix4x4 inverseProjection,
        float nearMeters,
        float farMeters,
        Span<Vector3> destination)
    {
        int cursor = 0;
        for (int depthIndex = 0; depthIndex < 2; depthIndex++)
        {
            float distance = depthIndex == 0 ? nearMeters : farMeters;
            for (int yIndex = 0; yIndex < 2; yIndex++)
            {
                float y = yIndex == 0 ? -1f : 1f;
                for (int xIndex = 0; xIndex < 2; xIndex++)
                {
                    float x = xIndex == 0 ? -1f : 1f;
                    Vector4 viewCorner = Vector4.Transform(
                        new Vector4(x, y, 1f, 1f),
                        inverseProjection);
                    if (MathF.Abs(viewCorner.W) <= 1e-6f)
                        throw new ArgumentException("Camera projection produced a corner at infinity.");
                    Vector3 view = new(
                        viewCorner.X / viewCorner.W,
                        viewCorner.Y / viewCorner.W,
                        viewCorner.Z / viewCorner.W);
                    float viewDepth = MathF.Abs(view.Z);
                    if (viewDepth <= 1e-6f)
                        throw new ArgumentException("Camera projection produced zero view depth.");
                    view *= distance / viewDepth;
                    destination[cursor++] = Vector3.Transform(view, inverseView);
                }
            }
        }
    }

    private static float SnapToTexel(float value, float texelWorldSize) =>
        MathF.Round(value / texelWorldSize, MidpointRounding.AwayFromZero)
        * texelWorldSize;

    internal static Vector3 StableLightUp(Vector3 surfaceToLight)
    {
        surfaceToLight = Vector3.Normalize(surfaceToLight);
        float sign = MathF.CopySign(1f, surfaceToLight.Z);
        float a = -1f / (sign + surfaceToLight.Z);
        float b = surfaceToLight.X * surfaceToLight.Y * a;
        return Vector3.Normalize(new Vector3(
            b,
            sign + surfaceToLight.Y * surfaceToLight.Y * a,
            -surfaceToLight.Y));
    }

    private static void Validate(
        in DirectionalShadowCascadeFitInput input,
        int destinationLength)
    {
        DirectionalShadowQuality quality = input.Quality;
        if (quality.CascadeCount <= 0 || quality.CascadeCount > 4)
            throw new ArgumentOutOfRangeException(nameof(input), "Cascade count must be in [1,4].");
        if (destinationLength < quality.CascadeCount)
            throw new ArgumentException("Destination cannot hold every configured cascade.");
        if (quality.MapResolution <= 0
            || !float.IsFinite(quality.MaximumReachMeters)
            || quality.MaximumReachMeters <= input.CameraNearMeters)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Shadow quality dimensions are invalid.");
        }
        if (!float.IsFinite(input.CameraNearMeters) || input.CameraNearMeters <= 0f)
            throw new ArgumentOutOfRangeException(nameof(input), "Camera near distance must be positive.");
        if (float.IsNaN(input.ResidentMaximumReachMeters)
            || input.ResidentMaximumReachMeters < 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                "Resident shadow reach must be nonnegative or positive infinity.");
        }
        if (!float.IsFinite(input.PracticalSplitLambda)
            || input.PracticalSplitLambda < 0f
            || input.PracticalSplitLambda > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Split lambda must be in [0,1].");
        }
        if (!float.IsFinite(input.CasterDepthPaddingMeters)
            || input.CasterDepthPaddingMeters <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Caster depth padding must be positive.");
        }
        float lightLength = input.SurfaceToLightDirection.Length();
        if (!float.IsFinite(lightLength) || lightLength <= 1e-6f)
            throw new ArgumentOutOfRangeException(nameof(input), "Light direction must be finite and nonzero.");
    }
}
