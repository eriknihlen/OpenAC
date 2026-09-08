namespace AcDream.Runtime;

public readonly record struct RuntimeFrameTime(
    ulong FrameNumber,
    double DeltaSeconds,
    double SimulationTimeSeconds);

public interface IGameRuntimeClock
{
    ulong FrameNumber { get; }

    double SimulationTimeSeconds { get; }
}

public sealed class GameRuntimeClock : IGameRuntimeClock
{
    public ulong FrameNumber { get; private set; }

    public double SimulationTimeSeconds { get; private set; }

    public RuntimeFrameTime Advance(
        double hostDeltaSeconds,
        bool advanceSimulationTime = true)
    {
        double deltaSeconds = NormalizeDeltaSeconds(hostDeltaSeconds);
        FrameNumber = checked(FrameNumber + 1UL);
        if (advanceSimulationTime)
            SimulationTimeSeconds += deltaSeconds;
        return new RuntimeFrameTime(
            FrameNumber,
            deltaSeconds,
            SimulationTimeSeconds);
    }

    public static double NormalizeDeltaSeconds(double deltaSeconds) =>
        double.IsFinite(deltaSeconds)
        && deltaSeconds > 0.0
        && deltaSeconds <= float.MaxValue
            ? deltaSeconds
            : 0.0;
}
