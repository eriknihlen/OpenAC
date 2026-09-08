using System;
using System.Collections.Generic;
using System.Numerics;

namespace AcDream.App.UI.Layout;

internal static class DatRichText
{
    public readonly record struct Segment(string? Text, Vector4 Color);

    public static IReadOnlyList<UiText.Line> Compose(
        UiText target,
        IReadOnlyList<Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(segments);

        var lines = new List<UiText.Line>();
        float maximumWidth = MathF.Max(
            1f,
            target.Width - (target.Padding + target.MarginLeft) - (target.Padding + target.MarginRight));
        Func<string, float> measure = target.DatFont is { } font
            ? font.MeasureWidth
            : static value => value.Length * 8f;

        foreach (Segment segment in segments)
        {
            if (string.IsNullOrEmpty(segment.Text))
                continue;

            foreach (string wrapped in UiText.WrapWords(segment.Text, measure, maximumWidth))
                lines.Add(new UiText.Line(wrapped, segment.Color));
        }

        return lines;
    }

    public static Vector4 PaletteColor(UiText target, int index, Vector4 fallback) =>
        index >= 0 && index < target.FontColorPalette.Count
            ? target.FontColorPalette[index]
            : fallback;
}
