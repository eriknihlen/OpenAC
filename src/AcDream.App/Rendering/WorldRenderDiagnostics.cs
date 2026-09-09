using System.Diagnostics;

namespace AcDream.App.Rendering;

internal readonly record struct TerrainRenderDiagnosticFacts(
    int VisibleSlots,
    int Draws,
    int LoadedSlots,
    int CapacitySlots);

/// <summary>Owns permanent terrain timing and diagnostic publication.</summary>
internal sealed class WorldRenderDiagnostics
{
    private readonly IRenderFrameDiagnosticLog _log;
    private readonly Stopwatch _terrainStopwatch = new();
    private readonly RollingTimingSampleWindow _terrainSamples = new(256);

    public WorldRenderDiagnostics(IRenderFrameDiagnosticLog log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public void BeginTerrainDraw() => _terrainStopwatch.Restart();

    public void EndTerrainDraw()
    {
        _terrainStopwatch.Stop();
        _terrainSamples.PushHundredthsMicroseconds(
            (long)(_terrainStopwatch.Elapsed.TotalMicroseconds * 100.0));
    }

    public void PushTerrainSample(long elapsedHundredthsMicroseconds) =>
        _terrainSamples.PushHundredthsMicroseconds(elapsedHundredthsMicroseconds);

    public void PublishTerrainDiagnostics(TerrainRenderDiagnosticFacts facts)
    {
        RollingTimingPercentiles timing = _terrainSamples.Snapshot();
        double medianMicroseconds = timing.MedianHundredthsMicroseconds / 100.0;
        double p95Microseconds = timing.Percentile95HundredthsMicroseconds / 100.0;
        string budget = medianMicroseconds > 1000.0 ? " BUDGET_OVER" : string.Empty;
        _log.WriteLine(
            $"[TERRAIN-DIAG]{budget} cpu_us={medianMicroseconds:F2}m/"
            + $"{p95Microseconds:F2}p95  draws={facts.Draws}/frame  "
            + $"visible={facts.VisibleSlots}  loaded={facts.LoadedSlots}  "
            + $"capacity={facts.CapacitySlots}");
    }
}
