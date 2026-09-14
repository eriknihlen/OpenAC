using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Navigation;

/// <summary>
/// Notices when walking stops making progress and cycles through a short
/// list of recoveries. Progress is judged by distance covered over a window,
/// not by whether the host says it is moving, because a wall makes both true.
/// The moves are the ones a player makes when caught on a corner: back off,
/// then sidestep either way. Jumping is not among them; it rarely frees a
/// character and looks like a fault when it does not.
/// </summary>
public sealed class StuckDetector
{
    public const double WindowSeconds = 3d;
    public const double MinimumProgressMeters = 0.75;

    private PluginNavigationPosition _anchor;
    private double _anchorTime = double.NegativeInfinity;
    private int _escalation;

    public int Escalation => _escalation;

    /// <summary>The last observation saw the character cover the minimum: it is going somewhere.</summary>
    public bool Progressed { get; private set; }

    public void Reset()
    {
        _anchorTime = double.NegativeInfinity;
        _escalation = 0;
        Progressed = false;
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
        Progressed = moved >= MinimumProgressMeters;
        if (Progressed)
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
        StuckRecovery recovery = (_escalation % 3) switch
        {
            0 => StuckRecovery.BackUp,
            1 => StuckRecovery.StrafeLeft,
            _ => StuckRecovery.StrafeRight,
        };
        _escalation++;
        return recovery;
    }
}

public enum StuckRecovery
{
    BackUp,
    StrafeLeft,
    StrafeRight,
}
