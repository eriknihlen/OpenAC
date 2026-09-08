namespace AcDream.App.Streaming;

public readonly record struct DungeonGateResult(bool InsideDungeon, uint? ObserverLandblockKey);

public static class DungeonStreamingGate
{
    public static DungeonGateResult Compute(
        bool isTeleportHold, bool currCellIsSealedDungeon, uint currCellId)
    {
        if (isTeleportHold)
            return new DungeonGateResult(false, null);

        if (currCellIsSealedDungeon)
            return new DungeonGateResult(true, currCellId >> 16);
        return new DungeonGateResult(false, null);
    }
}
