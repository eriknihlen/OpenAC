using System.Globalization;
using System.Numerics;

namespace AcDream.App.World;

internal sealed class LiveWorldOriginState
{
    public int CenterX { get; private set; }
    public int CenterY { get; private set; }
    public bool IsKnown { get; private set; }

    public void SetPlaceholder(int centerX, int centerY)
    {
        CenterX = centerX;
        CenterY = centerY;
        IsKnown = false;
    }

    public bool TryInitialize(int centerX, int centerY)
    {
        if (IsKnown)
            return false;

        CenterX = centerX;
        CenterY = centerY;
        IsKnown = true;
        return true;
    }

    public void Recenter(int centerX, int centerY)
    {
        CenterX = centerX;
        CenterY = centerY;
    }

    public (int X, int Y) GetCenter() => (CenterX, CenterY);

    public void EnsureAgreesWithRuntimeFrame(
        uint runtimeCenterLandblockId,
        uint projectingLandblockId) =>
        TryEnsureAgreesWithRuntimeFrame(
            runtimeCenterLandblockId,
            projectingLandblockId,
            transitInFlight: false);

    public bool TryEnsureAgreesWithRuntimeFrame(
        uint runtimeCenterLandblockId,
        uint projectingLandblockId,
        bool transitInFlight)
    {
        if (!IsKnown || runtimeCenterLandblockId == 0u)
            return true;

        int runtimeCenterX = (int)((runtimeCenterLandblockId >> 24) & 0xFFu);
        int runtimeCenterY = (int)((runtimeCenterLandblockId >> 16) & 0xFFu);
        if (runtimeCenterX == CenterX && runtimeCenterY == CenterY)
            return true;

        if (transitInFlight)
            return false;

        throw new InvalidOperationException(string.Create(
            CultureInfo.InvariantCulture,
            $"World-frame owners disagree: Runtime centre "
            + $"({runtimeCenterX},{runtimeCenterY}) vs streamed origin "
            + $"({CenterX},{CenterY}) while projecting landblock "
            + $"0x{projectingLandblockId:X8}. That offsets the entity by "
            + $"({(runtimeCenterX - CenterX) * 192f:F0}m,"
            + $"{(runtimeCenterY - CenterY) * 192f:F0}m) from its geometry."));
    }

    public Vector3 CellLocalForSeed(Vector3 worldPosition, uint cellId)
    {
        int landblockX = (int)((cellId >> 24) & 0xFFu);
        int landblockY = (int)((cellId >> 16) & 0xFFu);
        var origin = new Vector3(
            (landblockX - CenterX) * 192f,
            (landblockY - CenterY) * 192f,
            0f);
        return worldPosition - origin;
    }

    public void Reset() => IsKnown = false;
}
