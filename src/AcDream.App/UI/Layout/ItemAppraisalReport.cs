using System.Numerics;
using System.Text;

namespace AcDream.App.UI.Layout;

public enum ItemAppraisalFontStyle
{
    Normal = 0,
    Beneficial = 1,
    Detrimental = 2,
}

public enum ItemAppraisalSeparator
{
    None,
    Line,
    Paragraph,
}

public readonly record struct ItemAppraisalFragment(
    string Text,
    ItemAppraisalSeparator Separator,
    ItemAppraisalFontStyle Style);

public sealed class ItemAppraisalReport
{
    public static ItemAppraisalReport Empty { get; } = new([]);

    public ItemAppraisalReport(IReadOnlyList<ItemAppraisalFragment> fragments)
    {
        ArgumentNullException.ThrowIfNull(fragments);
        Fragments = fragments;
    }

    public IReadOnlyList<ItemAppraisalFragment> Fragments { get; }
    public bool IsEmpty => Fragments.Count == 0;

    public override string ToString()
    {
        var text = new StringBuilder();
        foreach (ItemAppraisalFragment fragment in Fragments)
        {
            if (text.Length != 0)
            {
                text.Append(fragment.Separator == ItemAppraisalSeparator.Paragraph
                    ? "\n\n"
                    : "\n");
            }
            text.Append(fragment.Text);
        }
        return text.ToString();
    }
}

internal sealed class ItemAppraisalReportBuilder
{
    private readonly List<ItemAppraisalFragment> _fragments = [];

    public void Line(
        string value,
        ItemAppraisalFontStyle style = ItemAppraisalFontStyle.Normal)
        => Add(value, sameParagraph: true, style);

    public void Paragraph(
        string value,
        ItemAppraisalFontStyle style = ItemAppraisalFontStyle.Normal)
        => Add(value, sameParagraph: false, style);

    public void BlankLine(
        ItemAppraisalFontStyle style = ItemAppraisalFontStyle.Normal)
    {
        if (_fragments.Count == 0)
            return;

        _fragments.Add(new ItemAppraisalFragment(
            string.Empty,
            ItemAppraisalSeparator.Line,
            style));
    }

    public ItemAppraisalReport Build()
        => _fragments.Count == 0
            ? ItemAppraisalReport.Empty
            : new ItemAppraisalReport(_fragments.ToArray());

    private void Add(
        string value,
        bool sameParagraph,
        ItemAppraisalFontStyle style)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        _fragments.Add(new ItemAppraisalFragment(
            value,
            _fragments.Count == 0
                ? ItemAppraisalSeparator.None
                : sameParagraph
                    ? ItemAppraisalSeparator.Line
                    : ItemAppraisalSeparator.Paragraph,
            style));
    }
}

internal static class ItemAppraisalTextLayout
{
    public static IReadOnlyList<UiText.Line> Shape(
        UiText target,
        ItemAppraisalReport report)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(report);

        float maxWidth = Math.Max(1f, target.Width - (2f * target.Padding));
        float Measure(string value)
            => target.DatFont?.MeasureWidth(value)
               ?? target.Font?.MeasureWidth(value)
               ?? value.Length * 8f;

        var lines = new List<UiText.Line>();
        foreach (ItemAppraisalFragment fragment in report.Fragments)
        {
            if (lines.Count != 0
                && fragment.Separator == ItemAppraisalSeparator.Paragraph)
            {
                lines.Add(new UiText.Line(string.Empty, ResolveColor(target, fragment.Style)));
            }

            Vector4 color = ResolveColor(target, fragment.Style);
            string normalized = fragment.Text.Replace(
                "\\n",
                "\n",
                StringComparison.Ordinal);
            foreach (string logicalLine in normalized.Split('\n'))
            {
                if (logicalLine.Length == 0)
                {
                    lines.Add(new UiText.Line(string.Empty, color));
                    continue;
                }

                foreach (string wrapped in ChatTranscriptRenderer.WrapText(
                             logicalLine,
                             maxWidth,
                             Measure))
                {
                    lines.Add(new UiText.Line(wrapped, color));
                }
            }
        }

        return lines;
    }

    private static Vector4 ResolveColor(
        UiText target,
        ItemAppraisalFontStyle style)
    {
        int index = (int)style;
        return index >= 0 && index < target.FontColorPalette.Count
            ? target.FontColorPalette[index]
            : target.DefaultColor;
    }
}
