namespace AcDream.Plugin.Abstractions;

public readonly record struct PluginWorldTimeSnapshot(
    bool IsAvailable,
    double GameTicks,
    int Year,
    int Month,
    int Day,
    int Hour,
    string MonthName,
    string HourName,
    bool IsDay,
    double MinutesUntilDay,
    double MinutesUntilNight);

public interface IWorldTimeAutomation
{
    PluginWorldTimeSnapshot Snapshot => default;
}
