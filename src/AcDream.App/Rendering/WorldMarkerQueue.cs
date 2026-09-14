using System.Numerics;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.Rendering;

/// <summary>
/// The rings and strips plugins ask to have drawn in the world, in map
/// coordinates, held until the next world pass draws them and starts
/// again. Plugins submit from their draw callbacks (after this frame's
/// world has been drawn), so a submission shows one frame later; they
/// submit every frame, as RynthCore's Nav3D buffer expects. Bounded so a
/// runaway plugin cannot flood the pass.
/// </summary>
internal sealed class WorldMarkerQueue
{
    public const int MaxRings = 512;
    public const int MaxLines = 1024;

    internal readonly record struct Ring(
        PluginNavigationPosition Center,
        float RadiusMeters,
        float ThicknessMeters,
        float HeightMeters,
        Vector4 Color);

    internal readonly record struct Line(
        PluginNavigationPosition From,
        PluginNavigationPosition To,
        float ThicknessMeters,
        Vector4 Color);

    private readonly object _gate = new();
    private readonly List<Ring> _rings = [];
    private readonly List<Line> _lines = [];

    /// <summary>True once a world pass renderer has attached: until then nothing submitted is drawn.</summary>
    public bool HasRenderer { get; internal set; }

    public void AddRing(in PluginNavigationPosition center, float radiusMeters, float thicknessMeters, float heightMeters, Vector4 color)
    {
        if (!float.IsFinite(radiusMeters) || radiusMeters <= 0f)
            return;
        lock (_gate)
        {
            if (_rings.Count < MaxRings)
                _rings.Add(new Ring(center, radiusMeters, Math.Max(0.01f, thicknessMeters), Math.Max(0f, heightMeters), color));
        }
    }

    public void AddLine(in PluginNavigationPosition from, in PluginNavigationPosition to, float thicknessMeters, Vector4 color)
    {
        lock (_gate)
        {
            if (_lines.Count < MaxLines)
                _lines.Add(new Line(from, to, Math.Max(0.01f, thicknessMeters), color));
        }
    }

    /// <summary>Hands the pending geometry to the caller's lists and empties the queue.</summary>
    public void Drain(List<Ring> rings, List<Line> lines)
    {
        lock (_gate)
        {
            rings.AddRange(_rings);
            lines.AddRange(_lines);
            _rings.Clear();
            _lines.Clear();
        }
    }
}
