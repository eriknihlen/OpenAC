using System;
using System.Numerics;

namespace AcDream.App.Rendering;

public sealed partial class PortalDepthMaskRenderer : IDisposable
{
    private readonly ResourceCleanupGroup _resources;

    private const int MaxFanVerts = 32;

    internal long DynamicBufferCapacityBytes => 0;

    public void BeginFrame(int frameSlot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameSlot);
        _rhiFrameStarted = true;
    }

    public void DrawDepthFan(
        ReadOnlySpan<Vector3> worldVerts,
        in Matrix4x4 viewProjection,
        ReadOnlySpan<Vector4> planes,
        bool forceFarZ)
    {
        if (worldVerts.Length < 3)
            return;
        DrawDepthFanRhi(worldVerts, in viewProjection, planes, forceFarZ);
    }

    public void Dispose() => DisposeRhiResources();
}
