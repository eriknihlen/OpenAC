namespace AcDream.App.Streaming;

internal readonly record struct StreamingRevealWindow(
    int NearRadius,
    int FarRadius);

internal readonly record struct WorldRevealReadinessSnapshot(
    uint DestinationCell,
    bool IsIndoor,
    bool IsUnhydratable,
    int RequiredRenderRadius,
    int RequiredNearRadius,
    bool IsRenderNeighborhoodReady,
    bool AreCompositeTexturesReady,
    bool IsCollisionReady)
{
    public bool HasDestination => DestinationCell != 0u;

    public bool IsReady => HasDestination
        && (IsUnhydratable
            || (IsRenderNeighborhoodReady
                && AreCompositeTexturesReady
                && IsCollisionReady));
}

internal sealed class WorldRevealReadinessBarrier
{
    private readonly Func<StreamingRevealWindow> _revealWindow;
    private readonly Func<uint, int, int, bool> _isRenderNeighborhoodReady;
    private readonly Func<uint, bool> _isSpawnCellReady;
    private readonly Func<uint, int, bool> _isTerrainNeighborhoodReady;
    private readonly Func<bool> _areCompositeTexturesReady;
    private readonly Action<uint, int> _prepareCompositeTextures;
    private readonly Action _invalidateCompositeTextures;
    private readonly Func<uint, bool> _isSpawnClaimUnhydratable;

    public WorldRevealReadinessBarrier(
        Func<StreamingRevealWindow> revealWindow,
        Func<uint, int, int, bool> isRenderNeighborhoodReady,
        Func<uint, bool> isSpawnCellReady,
        Func<uint, int, bool> isTerrainNeighborhoodReady,
        Func<bool> areCompositeTexturesReady,
        Action<uint, int> prepareCompositeTextures,
        Action invalidateCompositeTextures,
        Func<uint, bool> isSpawnClaimUnhydratable)
    {
        _revealWindow = revealWindow
            ?? throw new ArgumentNullException(nameof(revealWindow));
        _isRenderNeighborhoodReady = isRenderNeighborhoodReady
            ?? throw new ArgumentNullException(nameof(isRenderNeighborhoodReady));
        _isSpawnCellReady = isSpawnCellReady
            ?? throw new ArgumentNullException(nameof(isSpawnCellReady));
        _isTerrainNeighborhoodReady = isTerrainNeighborhoodReady
            ?? throw new ArgumentNullException(nameof(isTerrainNeighborhoodReady));
        _areCompositeTexturesReady = areCompositeTexturesReady
            ?? throw new ArgumentNullException(nameof(areCompositeTexturesReady));
        _prepareCompositeTextures = prepareCompositeTextures
            ?? throw new ArgumentNullException(nameof(prepareCompositeTextures));
        _invalidateCompositeTextures = invalidateCompositeTextures
            ?? throw new ArgumentNullException(nameof(invalidateCompositeTextures));
        _isSpawnClaimUnhydratable = isSpawnClaimUnhydratable
            ?? throw new ArgumentNullException(nameof(isSpawnClaimUnhydratable));
    }

    public void Begin() => _invalidateCompositeTextures();

    public void Prepare(uint destinationCell)
    {
        if (destinationCell == 0 || _isSpawnClaimUnhydratable(destinationCell))
            return;

        StreamingRevealWindow required = RequiredWindow(destinationCell);
        if (_isRenderNeighborhoodReady(
                destinationCell,
                required.NearRadius,
                required.NearRadius))
        {
            _prepareCompositeTextures(destinationCell, required.NearRadius);
        }
    }

    public bool IsReady(uint destinationCell)
        => Evaluate(destinationCell).IsReady;

    public WorldRevealReadinessSnapshot Evaluate(uint destinationCell)
    {
        if (destinationCell == 0)
            return default;

        bool isIndoor = IsIndoor(destinationCell);
        StreamingRevealWindow required = RequiredWindow(destinationCell);
        if (_isSpawnClaimUnhydratable(destinationCell))
        {
            return new WorldRevealReadinessSnapshot(
                destinationCell,
                isIndoor,
                IsUnhydratable: true,
                required.FarRadius,
                required.NearRadius,
                IsRenderNeighborhoodReady: false,
                AreCompositeTexturesReady: false,
                IsCollisionReady: false);
        }

        bool renderReady = _isRenderNeighborhoodReady(
            destinationCell,
            required.NearRadius,
            required.FarRadius);
        bool compositesReady = renderReady && _areCompositeTexturesReady();
        bool collisionReady = renderReady && compositesReady
            && (isIndoor
                ? _isSpawnCellReady(destinationCell)
                : _isTerrainNeighborhoodReady(
                    destinationCell,
                    required.FarRadius));

        return new WorldRevealReadinessSnapshot(
            destinationCell,
            isIndoor,
            IsUnhydratable: false,
            required.FarRadius,
            required.NearRadius,
            renderReady,
            compositesReady,
            collisionReady);
    }

    internal StreamingRevealWindow RequiredWindow(uint destinationCell)
    {
        if (IsIndoor(destinationCell))
            return new StreamingRevealWindow(0, 0);

        StreamingRevealWindow window = _revealWindow();
        int far = Math.Max(0, window.FarRadius);
        int near = Math.Clamp(window.NearRadius, 0, far);
        return new StreamingRevealWindow(near, far);
    }

    internal int RequiredRenderRadius(uint destinationCell) =>
        RequiredWindow(destinationCell).FarRadius;

    private static bool IsIndoor(uint cellId) => (cellId & 0xFFFFu) >= 0x0100u;
}
