using System.Globalization;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Navigation;

/// <summary>
/// A UtilityBelt-style jump: <c>jump[w|x|z|c|s]* [heading] [ms]</c> faces
/// the heading first when one is given, then holds the jump key with the
/// named movement keys for the given time (the jump's power, 0-1000 ms),
/// and lets go. Metas written for UB use it to hop gaps and ledges; the
/// route is paused while it runs so the walker does not fight it.
/// </summary>
public sealed class Jumper
{
    private const double TurnToleranceDegrees = 3d;
    private const double TurnTimeoutSeconds = 2.5d;
    private const double SettleSeconds = 0.15d;

    private enum Phase
    {
        Idle,
        Turning,
        Settling,
        Charging,
        Released,
    }

    private Phase _phase;
    private double _phaseStartedAt;
    private PluginMovementIntent _keys;
    private double _holdSeconds;
    private float _heading = float.NaN;
    private bool _faced;

    public bool IsBusy => _phase != Phase.Idle;

    /// <summary>The character is mid-jump: the walker should hold off until it lands.</summary>
    public bool IsAirborneExpected => _phase is Phase.Charging or Phase.Released;

    /// <summary>
    /// Parses a UB jump verb and its arguments: the letters after "jump"
    /// pick held keys (w forward, x back, z strafe left, c strafe right,
    /// s walk instead of run), then an optional heading and hold time.
    /// </summary>
    public static bool TryParse(string verb, IReadOnlyList<string> arguments, out PluginMovementIntent keys, out float heading, out int milliseconds, out string error)
    {
        keys = default;
        heading = float.NaN;
        milliseconds = 0;
        error = string.Empty;
        if (!verb.StartsWith("jump", StringComparison.OrdinalIgnoreCase))
        {
            error = "not a jump";
            return false;
        }
        bool forward = false, backward = false, left = false, right = false, walk = false;
        foreach (char letter in verb[4..].ToLowerInvariant())
        {
            switch (letter)
            {
                case 'w': forward = true; break;
                case 'x': backward = true; break;
                case 'z': left = true; break;
                case 'c': right = true; break;
                case 's': walk = true; break;
                default:
                    error = $"unknown jump key '{letter}' (w, x, z, c, s)";
                    return false;
            }
        }
        keys = new PluginMovementIntent(Forward: forward, Backward: backward, StrafeLeft: left, StrafeRight: right, Run: !walk, Jump: true);
        var numbers = new List<double>();
        foreach (string argument in arguments)
        {
            if (!double.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            {
                error = $"'{argument}' is not a number";
                return false;
            }
            numbers.Add(number);
        }
        if (numbers.Count >= 2)
        {
            heading = (float)numbers[0];
            milliseconds = (int)numbers[1];
        }
        else if (numbers.Count == 1)
        {
            milliseconds = (int)numbers[0];
        }
        milliseconds = Math.Clamp(milliseconds, 0, 1000);
        return true;
    }

    public void Start(in PluginMovementIntent keys, float heading, int milliseconds, double now)
    {
        _keys = keys with { Jump = true };
        _heading = heading;
        _holdSeconds = milliseconds / 1000d;
        _phase = float.IsNaN(heading) ? Phase.Settling : Phase.Turning;
        _phaseStartedAt = now;
        _faced = false;
    }

    public void Cancel(INavigationAutomation nav)
    {
        if (_phase is Phase.Charging)
            nav.ClearMovementIntent();
        _phase = Phase.Idle;
    }

    /// <summary>One tick; true while the jump is still in progress.</summary>
    public bool Tick(INavigationAutomation nav, in PluginNavigationSnapshot snapshot, double now)
    {
        switch (_phase)
        {
            case Phase.Idle:
                return false;

            case Phase.Turning:
                if (!_faced)
                {
                    nav.FaceHeading(_heading);
                    _faced = true;
                }
                float error = RouteFollower.HeadingDelta(snapshot.Position.HeadingDegrees, _heading);
                if (Math.Abs(error) <= TurnToleranceDegrees || now - _phaseStartedAt > TurnTimeoutSeconds)
                {
                    _phase = Phase.Settling;
                    _phaseStartedAt = now;
                }
                return true;

            case Phase.Settling:
                if (now - _phaseStartedAt < SettleSeconds)
                    return true;
                nav.SetMovementIntent(_keys);
                _phase = Phase.Charging;
                _phaseStartedAt = now;
                return true;

            case Phase.Charging:
                if (now - _phaseStartedAt < _holdSeconds)
                    return true;
                nav.ClearMovementIntent();
                _phase = Phase.Released;
                _phaseStartedAt = now;
                return true;

            default:
                // Airborne until the host says the character is back on the ground, or a moment has passed.
                if (snapshot.IsAirborne && now - _phaseStartedAt < 3d)
                    return true;
                if (!snapshot.IsAirborne && now - _phaseStartedAt < 0.3d)
                    return true;
                _phase = Phase.Idle;
                return false;
        }
    }
}
