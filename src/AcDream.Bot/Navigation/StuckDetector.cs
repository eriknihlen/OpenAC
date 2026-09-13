using AcDream.Plugin.Abstractions;

namespace AcDream.Bot.Navigation;

/// <summary>
/// Notices when walking stops making progress and escalates through a short
/// list of recoveries. Progress is judged by distance covered over a window,
/// not by whether the host says it is moving, because a wall makes both true.
/// </summary>
public sealed class StuckDetector
{
    public const double WindowSeconds = 3d;
    public const double MinimumProgressMeters = 0.75;

    private PluginNavigationPosition _anchor;
    private double _anchorTime = double.NegativeInfinity;
    private int _escalation;

    public int Escalation => _escalation;

    public void Reset()
    {
        _anchorTime = double.NegativeInfinity;
        _escalation = 0;
    }

    /// <summary>
    /// Feeds one walking tick. Returns a recovery to attempt when the window
    /// elapsed without progress; null otherwise.
    /// </summary>
    public StuckRecovery? Observe(in PluginNavigationPosition position, double now)
    {
        if (double.IsNegativeInfinity(_anchorTime))
        {
            _anchor = position;
            _anchorTime = now;
            return null;
        }

        double moved = position.HorizontalDistanceMeters(_anchor);
        if (moved >= MinimumProgressMeters)
        {
            _anchor = position;
            _anchorTime = now;
            _escalation = 0;
            return null;
        }

        if (now - _anchorTime < WindowSeconds)
            return null;

        _anchor = position;
        _anchorTime = now;
        StuckRecovery recovery = (_escalation % 4) switch
        {
            0 => StuckRecovery.Jump,
            1 => StuckRecovery.StrafeLeft,
            2 => StuckRecovery.StrafeRight,
            _ => StuckRecovery.BackUp,
        };
        _escalation++;
        return recovery;
    }
}

public enum StuckRecovery
{
    Jump,
    StrafeLeft,
    StrafeRight,
    BackUp,
}
