namespace AcDream.App.Streaming;

internal readonly record struct TeleportLandblockTransition(
    uint SourceLandblockId,
    uint DestinationLandblockId,
    uint StreamingCenterLandblockId)
{
    public bool CrossesLandblock => SourceLandblockId != DestinationLandblockId;
    public bool ChangesStreamingCenter =>
        StreamingCenterLandblockId != DestinationLandblockId;

    public static TeleportLandblockTransition Classify(
        uint sourceCellId,
        uint destinationCellId,
        uint currentStreamingCenterLandblockId)
    {
        uint sourceLandblockId = sourceCellId != 0
            ? NormalizeLandblockId(sourceCellId)
            : NormalizeLandblockId(currentStreamingCenterLandblockId);

        return new TeleportLandblockTransition(
            sourceLandblockId,
            NormalizeLandblockId(destinationCellId),
            NormalizeLandblockId(currentStreamingCenterLandblockId));
    }

    private static uint NormalizeLandblockId(uint cellOrLandblockId)
        => (cellOrLandblockId & 0xFFFF0000u) | 0xFFFFu;
}
