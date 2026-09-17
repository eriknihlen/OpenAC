namespace AcDream.Plugin.Abstractions;

/// <summary>
/// The in-world date and time of day, as the client's server-synchronized world
/// clock reports it. A world day is much shorter than a real day: sixteen world
/// hours pass in a little over two real hours.
/// </summary>
/// <param name="IsAvailable">
/// True when a session is in the world and the remaining fields carry real
/// values. When it is false every other field is at its default.
/// </param>
/// <param name="GameTicks">
/// The world clock in seconds since the calendar's origin, including the
/// calendar offset the server supplied.
/// </param>
/// <param name="Year">The world year.</param>
/// <param name="Month">The month number within the year, starting at zero.</param>
/// <param name="Day">The day number within the month, starting at one.</param>
/// <param name="Hour">
/// The hour of the world day, from 0 to 15; there are sixteen named hours in a
/// world day rather than twenty-four.
/// </param>
/// <param name="MonthName">The month's display name.</param>
/// <param name="HourName">The hour's display name.</param>
/// <param name="IsDay">
/// True during daylight, which runs from hour 4 up to but not including
/// hour 12.
/// </param>
/// <param name="MinutesUntilDay">
/// Real-time minutes until daylight begins, or zero when it is already day.
/// </param>
/// <param name="MinutesUntilNight">
/// Real-time minutes until night falls, or zero when it is already night.
/// </param>
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

/// <summary>Reading the in-world date, time of day, and day-or-night state.</summary>
public interface IWorldTimeAutomation
{
    /// <summary>
    /// The current world time. Its <c>IsAvailable</c> is false when no session
    /// is in the world, and the default implementation always returns such an
    /// empty snapshot.
    /// </summary>
    PluginWorldTimeSnapshot Snapshot => default;
}
