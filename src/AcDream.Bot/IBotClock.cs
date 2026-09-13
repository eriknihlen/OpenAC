namespace AcDream.Bot;

/// <summary>Monotonic seconds used for cooldowns, timeouts and stuck detection.</summary>
public interface IBotClock
{
    double Now { get; }
}

/// <summary>Advances by the elapsed time each engine tick reports.</summary>
public sealed class TickClock : IBotClock
{
    public double Now { get; private set; }

    public void Advance(double elapsedSeconds)
    {
        if (elapsedSeconds > 0d && double.IsFinite(elapsedSeconds))
            Now += elapsedSeconds;
    }
}
