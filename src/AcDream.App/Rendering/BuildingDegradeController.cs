using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.Rendering;

internal sealed class BuildingDegradeController : IBuildingDegradeFrameTick
{
    internal const int FpsHistoryLength = 20;
    internal const int CandidateHistoryLength = 30;
    internal const float MinimumFps = 8f;
    internal const float IdealFps = 10f;
    internal const float MaximumFps = 20f;

    private readonly Func<DisplaySettings> _settings;
    private readonly float[] _frameSeconds = new float[FpsHistoryLength];
    private readonly float[] _candidateHistory = new float[CandidateHistoryLength];
    private float _automaticMultiplier;
    private float _fps;

    internal BuildingDegradeController(Func<DisplaySettings> settings)
        => _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    internal float Fps => _fps;
    internal float AutomaticMultiplier => _automaticMultiplier;
    internal float DegradeDistance => _settings().DegradeDistance;
    internal float ActiveMultiplier
    {
        get
        {
            DisplaySettings settings = _settings();
            return settings.AutomaticDegrades
                ? _automaticMultiplier
                : settings.GraphicsPerformance;
        }
    }

    internal void Tick(double elapsedSeconds)
    {
        double total = 0d;
        for (int i = 0; i < _frameSeconds.Length; i++)
            total += _frameSeconds[i];
        _fps = total > 0.000199999995f
            ? (float)(FpsHistoryLength / total)
            : 0f;

        AdvanceFrameHistory(_frameSeconds, (float)elapsedSeconds);
        _automaticMultiplier = AdvanceAutomaticMultiplier(
            _candidateHistory,
            _settings().AutomaticDegrades,
            _fps,
            _automaticMultiplier);
    }

    void IBuildingDegradeFrameTick.Tick(double elapsedSeconds) => Tick(elapsedSeconds);

    internal static void AdvanceFrameHistory(Span<float> history, float frameSeconds)
    {
        if (history.Length != FpsHistoryLength)
            throw new ArgumentException(
                $"Retail FPS history must contain exactly {FpsHistoryLength} slots.",
                nameof(history));

        history[..^1].CopyTo(history[1..]);
        history[0] = frameSeconds;
    }

    internal static float AdvanceAutomaticMultiplier(
        Span<float> history,
        bool automatic,
        float fps,
        float current)
    {
        if (history.Length != CandidateHistoryLength)
            throw new ArgumentException(
                $"Retail degrade history must contain exactly {CandidateHistoryLength} slots.",
                nameof(history));

        history[1..].CopyTo(history);
        if (!automatic)
        {
            history[^1] = current;
            return current;
        }

        float candidate = CalculateCandidate(fps, current);
        bool stable = IsCandidateStable(history, candidate);

        if (stable)
            current = candidate;
        history[^1] = current;
        return current;
    }

    internal static bool IsCandidateStable(ReadOnlySpan<float> history, float candidate)
    {
        if (history.Length != CandidateHistoryLength)
            throw new ArgumentException(
                $"Retail degrade history must contain exactly {CandidateHistoryLength} slots.",
                nameof(history));

        for (int i = 0; i < CandidateHistoryLength; i++)
        {
            if (!(Math.Abs((double)history[i] - candidate) < 0.01))
                return false;
        }

        return true;
    }

    internal static float CalculateCandidate(float fps, float current)
    {
        double fpsWide = fps;
        double minimumFps = MinimumFps;
        double idealFps = IdealFps;
        double maximumFps = MaximumFps;
        double w0 = LowShoulder(fpsWide, minimumFps * 0.75,
            (minimumFps + idealFps) * 0.5);
        double w1 = Triangle(fpsWide, minimumFps,
            minimumFps * 0.25 + idealFps * 0.75);
        double w2 = Triangle(fpsWide,
            (minimumFps + idealFps) * 0.5,
            (idealFps + maximumFps) * 0.5);
        double w3 = Triangle(fpsWide,
            idealFps * 0.75 + maximumFps * 0.25,
            maximumFps);
        double w4 = HighShoulder(fpsWide,
            (idealFps + maximumFps) * 0.5,
            maximumFps * 1.25);
        double weight = w0 + w1 + w2 + w3 + w4;
        float numeratorAfterW0 = (float)((double)-0.150000006f * w0);
        float numeratorAfterW1 = (float)(
            (double)numeratorAfterW0 - (double)0.02f * w1);
        float numeratorAfterW2 = (float)(
            (double)numeratorAfterW1 + (double)0f * w2);
        float numeratorAfterW3 = (float)(
            (double)numeratorAfterW2 + (double)0.01f * w3);
        double numerator = (double)numeratorAfterW3 + (double)0.1f * w4;
        double adjustment = weight > 0d
            ? numerator / weight
            : 0d;
        double candidate = (double)current + adjustment;
        if (candidate > 1d)
            candidate = 1d;
        else if (candidate < -1d)
            candidate = -1d;
        return (float)candidate;
    }

    private static double Triangle(double value, double low, double high)
        => Math.Max(0d, 1d - Math.Abs(2d * value - (high + low)) / (high - low));

    private static double LowShoulder(double value, double peak, double zero)
        => value < peak ? 1d : Math.Max(0d, 1d - Math.Abs(value - peak) / (zero - peak));

    private static double HighShoulder(double value, double zero, double peak)
        => value > peak ? 1d : Math.Max(0d, 1d - Math.Abs(value - peak) / (peak - zero));
}
