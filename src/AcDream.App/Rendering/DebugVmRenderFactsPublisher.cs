using System.Numerics;
using AcDream.Core.Physics;

namespace AcDream.App.Rendering;

internal readonly record struct DebugVmRenderFacts(
    int VisibleLandblocks,
    int TotalLandblocks,
    float NearestObjectDistance,
    string NearestObjectLabel,
    bool Colliding)
{
    public static DebugVmRenderFacts Initial => new(
        VisibleLandblocks: 0,
        TotalLandblocks: 0,
        NearestObjectDistance: float.PositiveInfinity,
        NearestObjectLabel: "-",
        Colliding: false);
}

internal interface IDebugVmRenderFactsSource
{
    DebugVmRenderFacts DebugVmFacts { get; }
}

internal sealed class DebugVmRenderFactsPublisher : IDebugVmRenderFactsSource
{
    internal const float PlayerCollisionRadius = 0.48f;
    internal const float ContactThreshold = 0.05f;

    public DebugVmRenderFacts DebugVmFacts { get; private set; } =
        DebugVmRenderFacts.Initial;

    public void PublishDebugVmFacts(
        bool consumerActive,
        int visibleLandblocks,
        int totalLandblocks,
        Vector3 nearestOrigin,
        IEnumerable<ShadowEntry>? shadowObjects)
    {
        if (!consumerActive)
            return;
        ArgumentNullException.ThrowIfNull(shadowObjects);

        float nearestDistance = float.PositiveInfinity;
        string nearestLabel = "-";
        foreach (ShadowEntry shadow in shadowObjects)
        {
            float dx = shadow.Position.X - nearestOrigin.X;
            float dy = shadow.Position.Y - nearestOrigin.Y;
            float distance = MathF.Sqrt(dx * dx + dy * dy)
                - shadow.Radius
                - PlayerCollisionRadius;
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestLabel = $"0x{shadow.EntityId:X8} {shadow.CollisionType}";
            }
        }

        DebugVmFacts = new DebugVmRenderFacts(
            visibleLandblocks,
            totalLandblocks,
            nearestDistance < 0f ? 0f : nearestDistance,
            nearestLabel,
            nearestDistance < ContactThreshold);
    }
}
