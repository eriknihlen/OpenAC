namespace AcDream.Core.World;

public static class StreamingReadinessGate
{
    public static bool ShouldStream(
        bool liveModeEnabled, bool chaseModeEverEntered, bool liveInWorld, bool liveCenterKnown)
    {
        bool isWaitingForLogin = liveModeEnabled && !chaseModeEverEntered;
        return !isWaitingForLogin || (liveInWorld && liveCenterKnown);
    }
}
