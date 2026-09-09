namespace AcDream.App.Streaming;

internal readonly record struct ResidentStreamingWindowFact(
    ulong Revision,
    int CenterX,
    int CenterY,
    int CompleteRadiusLandblocks,
    int PublishedLandblockCount,
    bool HasPublishedCenter)
{
    internal const float LandblockSizeMeters = 192f;

    internal float MaximumReachMeters => HasPublishedCenter
        ? CompleteRadiusLandblocks * LandblockSizeMeters
        : 0f;

    internal static ResidentStreamingWindowFact Unavailable(ulong revision) =>
        new(
            revision,
            CenterX: 0,
            CenterY: 0,
            CompleteRadiusLandblocks: 0,
            PublishedLandblockCount: 0,
            HasPublishedCenter: false);
}
