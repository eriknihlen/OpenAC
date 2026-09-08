using AcDream.App.Streaming;

namespace AcDream.App.Tests.Streaming;

public sealed class TeleportLandblockTransitionTests
{
    [Fact]
    public void SameDungeonRespawn_WithNegativeLocalCoordinate_DoesNotCrossLandblock()
    {
        const uint cell = 0x8C0401ADu;

        int legacyInferredLandblockY = 4 + (int)Math.Floor(-30.412f / 192f);
        Assert.Equal(3, legacyInferredLandblockY);

        var transition = TeleportLandblockTransition.Classify(
            sourceCellId: cell,
            destinationCellId: cell,
            currentStreamingCenterLandblockId: 0x8C04FFFFu);

        Assert.False(transition.CrossesLandblock);
        Assert.Equal(0x8C04FFFFu, transition.SourceLandblockId);
        Assert.Equal(0x8C04FFFFu, transition.DestinationLandblockId);
    }

    [Fact]
    public void DifferentEnvCells_InSameLandblock_DoNotCrossLandblock()
    {
        var transition = TeleportLandblockTransition.Classify(
            sourceCellId: 0x8C0401ADu,
            destinationCellId: 0x8C040145u,
            currentStreamingCenterLandblockId: 0x8C04FFFFu);

        Assert.False(transition.CrossesLandblock);
    }

    [Fact]
    public void DifferentPositionCellLandblocks_CrossLandblock()
    {
        var transition = TeleportLandblockTransition.Classify(
            sourceCellId: 0x8C0401ADu,
            destinationCellId: 0xA9B40022u,
            currentStreamingCenterLandblockId: 0x8C04FFFFu);

        Assert.True(transition.CrossesLandblock);
        Assert.Equal(0x8C04FFFFu, transition.SourceLandblockId);
        Assert.Equal(0xA9B4FFFFu, transition.DestinationLandblockId);
    }

    [Fact]
    public void UnplacedSource_UsesStreamingCenterWithoutCoordinateInference()
    {
        var transition = TeleportLandblockTransition.Classify(
            sourceCellId: 0,
            destinationCellId: 0x8C0401ADu,
            currentStreamingCenterLandblockId: 0x8C04FFFFu);

        Assert.False(transition.CrossesLandblock);
        Assert.Equal(0x8C04FFFFu, transition.SourceLandblockId);
    }

    [Fact]
    public void ReplacementBackToPlayerLandblock_StillRecentersAbandonedStreamingOrigin()
    {
        var transition = TeleportLandblockTransition.Classify(
            sourceCellId: 0x8C0401ADu,
            destinationCellId: 0x8C040145u,
            currentStreamingCenterLandblockId: 0xA9B4FFFFu);

        Assert.False(transition.CrossesLandblock);
        Assert.True(transition.ChangesStreamingCenter);
        Assert.Equal(0x8C04FFFFu, transition.DestinationLandblockId);
        Assert.Equal(0xA9B4FFFFu, transition.StreamingCenterLandblockId);
    }
}
