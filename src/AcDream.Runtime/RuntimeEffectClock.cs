namespace AcDream.Runtime;

/// <summary>
/// The clock timed effects are stamped with when they arrive and measured
/// against when their time left is read: seconds since the runtime was made.
/// Unlike the simulation clock it keeps running while there is no world to
/// simulate, because the server keeps counting an effect down while the
/// character is in portal space or loading into the world.
/// </summary>
public sealed class RuntimeEffectClock
{
    private readonly TimeProvider _time;
    private readonly long _start;

    public RuntimeEffectClock(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _start = _time.GetTimestamp();
    }

    public double NowSeconds => _time.GetElapsedTime(_start).TotalSeconds;
}
