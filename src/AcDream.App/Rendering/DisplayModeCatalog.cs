using System.Globalization;
using System;
using System.Collections.Generic;
using System.Linq;
using AcDream.UI.Abstractions.Panels.Settings;
using Silk.NET.Windowing;

namespace AcDream.App.Rendering;

internal static class DisplayModeCatalog
{
    private static IReadOnlyList<string>? _resolutions;
    private static IReadOnlyList<string>? _windowedResolutions;
    private static string? _desktopResolution;

    public static IReadOnlyList<string>? Resolutions => _resolutions;

    public static IReadOnlyList<string>? WindowedResolutions => _windowedResolutions;

    public static string? DesktopResolution => _desktopResolution;

    public static void InstallFromWindow(IWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        IMonitor? monitor = window.Monitor;
        if (monitor is null)
            return;

        VideoMode current = monitor.VideoMode;
        if (current.Resolution is not { } desktop || desktop.X <= 0 || desktop.Y <= 0)
            return;

        IEnumerable<(int W, int H)> modes = monitor
            .GetAllVideoModes()
            .Select(m => m.Resolution)
            .Where(r => r.HasValue)
            .Select(r => (r!.Value.X, r.Value.Y));

        IReadOnlyList<string> curated = Curate(modes, (desktop.X, desktop.Y));
        if (curated.Count == 0)
            return;

        _resolutions = curated;
        _windowedResolutions = BuildWindowedOffering(curated, (desktop.X, desktop.Y));
        _desktopResolution = $"{desktop.X}x{desktop.Y}";
    }

    internal static void ResetForTests()
    {
        _resolutions = null;
        _windowedResolutions = null;
        _desktopResolution = null;
    }

    internal static IReadOnlyList<string> BuildWindowedOffering(
        IReadOnlyList<string> curated,
        (int W, int H) desktop)
    {
        var keep = new SortedSet<(int W, int H)>(
            Comparer<(int W, int H)>.Create(static (a, b) =>
                a.W != b.W ? a.W.CompareTo(b.W) : a.H.CompareTo(b.H)));

        foreach (string spec in curated)
        {
            if (TryParse(spec, out (int W, int H) mode))
                keep.Add(mode);
        }
        foreach (string spec in DisplaySettings.AvailableResolutions)
        {
            if (TryParse(spec, out (int W, int H) mode)
                && mode.W <= desktop.W
                && mode.H <= desktop.H)
            {
                keep.Add(mode);
            }
        }

        return keep.Select(static m => $"{m.W}x{m.H}").ToArray();

        static bool TryParse(string spec, out (int W, int H) mode)
        {
            mode = default;
            string[] parts = spec.Split('x', 2);
            if (parts.Length == 2
                && int.TryParse(
                    parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
                && int.TryParse(
                    parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)
                && w > 0
                && h > 0)
            {
                mode = (w, h);
                return true;
            }
            return false;
        }
    }

    internal static IReadOnlyList<string> Curate(
        IEnumerable<(int W, int H)> modes,
        (int W, int H) desktop)
    {
        // The modern aspect families, as width/height ratios.
        ReadOnlySpan<float> modernAspects =
        [
            16f / 9f,
            16f / 10f,
            21f / 9f,
            32f / 9f,
        ];

        var keep = new SortedSet<(int W, int H)>(
            Comparer<(int W, int H)>.Create(static (a, b) =>
                a.W != b.W ? a.W.CompareTo(b.W) : a.H.CompareTo(b.H)));

        foreach ((int w, int h) in modes)
        {
            if (w <= 0 || h <= 0)
                continue;
            if (w > desktop.W || h > desktop.H)
                continue;
            if ((w, h) == desktop)
            {
                keep.Add((w, h));
                continue;
            }
            if (w < 1280)
                continue;

            float aspect = w / (float)h;
            bool modern = false;
            foreach (float family in modernAspects)
            {
                if (MathF.Abs(aspect - family) <= family * 0.025f)
                {
                    modern = true;
                    break;
                }
            }
            if (modern)
                keep.Add((w, h));
        }

        return keep.Select(static m => $"{m.W}x{m.H}").ToArray();
    }
}
