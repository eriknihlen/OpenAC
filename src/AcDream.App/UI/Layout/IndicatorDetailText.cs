using System;
using System.Collections.Generic;

namespace AcDream.App.UI.Layout;

internal static class IndicatorDetailText
{
    public static IReadOnlyList<UiText.Line> Shape(UiText target, string text)
    {
        ArgumentNullException.ThrowIfNull(target);
        text ??= string.Empty;

        float maxWidth = Math.Max(1f, target.Width - (2f * target.Padding));
        float Measure(string value)
            => target.DatFont?.MeasureWidth(value)
               ?? target.Font?.MeasureWidth(value)
               ?? value.Length * 8f;

        var lines = new List<UiText.Line>();
        foreach (string paragraph in text.Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                lines.Add(new UiText.Line(string.Empty, target.DefaultColor));
                continue;
            }

            foreach (string line in ChatTranscriptRenderer.WrapText(paragraph, maxWidth, Measure))
                lines.Add(new UiText.Line(line, target.DefaultColor));
        }
        return lines;
    }
}
