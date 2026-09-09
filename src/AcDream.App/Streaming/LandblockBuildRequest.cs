namespace AcDream.App.Streaming;

public readonly record struct LandblockBuildOrigin
{
    public LandblockBuildOrigin(int centerX, int centerY)
    {
        CenterX = centerX;
        CenterY = centerY;
        IsSpecified = true;
    }

    public int CenterX { get; }
    public int CenterY { get; }

    public bool IsSpecified { get; }
}

public readonly record struct LandblockBuildRequest(
    uint LandblockId,
    LandblockStreamJobKind Kind,
    ulong Generation,
    LandblockBuildOrigin Origin);
