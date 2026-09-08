namespace AcDream.App.Streaming;

public enum LandblockStreamTier
{
    Far,
    Near,
}

public enum LandblockStreamJobKind
{
    /// <summary>Read LandBlock heightmap, build mesh, no entity layer.</summary>
    LoadFar,
    /// <summary>Read LandBlock + LandBlockInfo, generate scenery, build mesh, full entity layer.</summary>
    LoadNear,
    /// <summary>Read LandBlockInfo + scenery only — terrain already loaded for this LB.</summary>
    PromoteToNear,
}
