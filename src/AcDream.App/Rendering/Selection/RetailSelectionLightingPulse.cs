using System.Diagnostics;

namespace AcDream.App.Rendering.Selection;

internal readonly record struct RetailSelectionLighting(float Luminosity, float Diffuse)
{
    public static readonly RetailSelectionLighting Normal = new(0f, 1f);
    public static readonly RetailSelectionLighting Low = new(0f, 0.35f);
    public static readonly RetailSelectionLighting High = new(0.99f, 1f);
}

internal sealed class RetailSelectionLightingPulse
{
    internal const double FlipIntervalSeconds = 0.2;

    private readonly Func<double> _now;
    private uint _serverGuid;
    private uint _localEntityId;
    private int _flipCount;
    private double _nextFlip;
    private RetailSelectionLighting _lighting;

    public RetailSelectionLightingPulse(Func<double>? now = null)
        => _now = now ?? MonotonicSeconds;

    public void Start(uint serverGuid, uint localEntityId)
    {
        if (serverGuid == 0u || localEntityId == 0u)
        {
            Clear();
            return;
        }

        _serverGuid = serverGuid;
        _localEntityId = localEntityId;
        _flipCount = 1;
        _lighting = RetailSelectionLighting.High;
        _nextFlip = _now() + FlipIntervalSeconds;
    }

    public void Tick()
    {
        if (_flipCount == 0)
            return;

        double now = _now();
        if (now < _nextFlip)
            return;

        int nextCount = _flipCount + 1;
        if (nextCount >= 5)
        {
            Clear();
            return;
        }

        _flipCount = nextCount;
        _lighting = (nextCount & 1) != 0
            ? RetailSelectionLighting.High
            : RetailSelectionLighting.Low;
        _nextFlip = now + FlipIntervalSeconds;
    }

    public bool TryGet(
        uint serverGuid,
        uint localEntityId,
        out RetailSelectionLighting lighting)
    {
        if (_flipCount != 0
            && serverGuid != 0u
            && localEntityId != 0u
            && serverGuid == _serverGuid
            && localEntityId == _localEntityId)
        {
            lighting = _lighting;
            return true;
        }

        lighting = default;
        return false;
    }

    public void Clear()
    {
        _serverGuid = 0u;
        _localEntityId = 0u;
        _flipCount = 0;
        _nextFlip = 0d;
        _lighting = default;
    }

    private static double MonotonicSeconds()
        => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
