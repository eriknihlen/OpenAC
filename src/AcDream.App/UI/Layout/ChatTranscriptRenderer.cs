using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Chat;
using AcDream.UI.Abstractions.Panels.Chat;

namespace AcDream.App.UI.Layout;

internal static class ChatTranscriptRenderer
{
    private static Vector4 TimestampColor =>
        RetailChatColorTable.TryGetColor(0x0Cu, out Vector4 grey)
            ? grey
            : new Vector4(0.5f, 0.5f, 0.5f, 1f);

    public const int MaxTranscriptCharacters = 0x2710;

    internal static int FirstLineWithinBudget(
        IReadOnlyList<FormattedLine> detailed,
        Func<uint, bool>? accept,
        int budget = MaxTranscriptCharacters)
        => FindBudgetStart(detailed, accept, budget).LineIndex;

    private readonly record struct BudgetStart(int LineIndex, int CharacterOffset);

    private static BudgetStart FindBudgetStart(
        IReadOnlyList<FormattedLine> detailed,
        Func<uint, bool>? accept,
        int budget = MaxTranscriptCharacters)
    {
        long used = 0;
        for (int i = detailed.Count - 1; i >= 0; i--)
        {
            if (accept is not null && !accept(detailed[i].LogTextType))
                continue;

            long cost = detailed[i].Text.Length + 1L;
            if (used + cost <= budget)
            {
                used += cost;
                continue;
            }

            int available = (int)Math.Max(0L, budget - used - 1L);
            if (available > 0)
            {
                string text = detailed[i].Text;
                int minimumOffset = Math.Max(0, text.Length - available);
                int offset = FirstCharacterAfterLineBreak(text, minimumOffset);
                if (offset < text.Length)
                    return new BudgetStart(i, offset);

                // A single newest unbroken message must still remain visible;
                // dropping it wholesale is what made large @acecommands
                // replies render as an empty transcript.
                if (used == 0 && text.Length > 0)
                    return new BudgetStart(i, minimumOffset);
            }
            return new BudgetStart(i + 1, 0);
        }
        return new BudgetStart(0, 0);
    }

    private static int FirstCharacterAfterLineBreak(string text, int start)
    {
        for (int i = Math.Clamp(start, 0, text.Length); i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n'))
                continue;
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                i++;
            return i + 1;
        }
        return text.Length;
    }

    private static FormattedLine SliceLine(FormattedLine line, int offset)
    {
        if (offset <= 0)
            return line;

        string text = line.Text[offset..];
        if (line.Spans is not { Count: > 0 } spans)
            return line with { Text = text };

        var sliced = new List<ChatTextSpan>();
        int at = 0;
        foreach (ChatTextSpan span in spans)
        {
            int end = at + span.Text.Length;
            if (end > offset)
            {
                int from = Math.Max(offset, at) - at;
                sliced.Add(span with { Text = span.Text[from..] });
            }
            at = end;
        }
        return line with { Text = text, Spans = sliced };
    }

    internal static IReadOnlyList<UiText.TextRun>? RunsForFragment(
        IReadOnlyList<ChatTextSpan> spans,
        int fragmentStart,
        int fragmentLength,
        Vector4 lineColor,
        Vector4 tagColor)
    {
        int fragmentEnd = fragmentStart + fragmentLength;
        var runs = new List<UiText.TextRun>();
        bool sawTag = false;
        int at = 0;

        foreach (ChatTextSpan span in spans)
        {
            int spanStart = at;
            int spanEnd = at + span.Text.Length;
            at = spanEnd;

            int from = Math.Max(spanStart, fragmentStart);
            int to = Math.Min(spanEnd, fragmentEnd);
            if (to <= from)
                continue;

            bool tagged = span.Tag is not null;
            bool stamped = span.Role == ChatSpanRole.Timestamp;
            sawTag |= tagged || stamped;

            Vector4 color = tagged
                ? tagColor
                : stamped
                    ? TimestampColor
                    : lineColor;

            runs.Add(new UiText.TextRun(
                span.Text.Substring(from - spanStart, to - from),
                color));
        }

        return sawTag ? runs : null;
    }

    internal static IReadOnlyList<(int Start, int Length, ChatTextTag Tag)>?
        TaggedRangesForFragment(
            IReadOnlyList<ChatTextSpan> spans,
            int fragmentStart,
            int fragmentLength)
    {
        int fragmentEnd = fragmentStart + fragmentLength;
        List<(int Start, int Length, ChatTextTag Tag)>? ranges = null;
        int at = 0;

        foreach (ChatTextSpan span in spans)
        {
            int spanStart = at;
            int spanEnd = at + span.Text.Length;
            at = spanEnd;

            if (span.Tag is not { } tag)
                continue;

            int from = Math.Max(spanStart, fragmentStart);
            int to = Math.Min(spanEnd, fragmentEnd);
            if (to <= from)
                continue;

            (ranges ??= new()).Add((from - fragmentStart, to - from, tag));
        }

        return ranges;
    }

    public static List<UiText.Line> BuildLines(
        IReadOnlyList<FormattedLine> detailed,
        float maxW,
        Func<string, float> measure,
        Func<uint, bool>? accept,
        Vector4 defaultColor,
        Vector4? tagColor = null,
        List<IReadOnlyList<UiText.TextRun>?>? runsPerLine = null,
        List<IReadOnlyList<(int Start, int Length, ChatTextTag Tag)>?>? tagsPerLine = null,
        List<UiText.LineKey>? keysPerLine = null)
    {
        var result = new List<UiText.Line>(detailed.Count);
        runsPerLine?.Clear();
        tagsPerLine?.Clear();
        keysPerLine?.Clear();
        if (detailed.Count == 0)
            return result;

        Vector4 currentColor = defaultColor;
        BudgetStart start = FindBudgetStart(detailed, accept);
        for (int lineIndex = start.LineIndex; lineIndex < detailed.Count; lineIndex++)
        {
            FormattedLine d = detailed[lineIndex];
            if (accept is not null && !accept(d.LogTextType))
                continue;
            if (lineIndex == start.LineIndex && start.CharacterOffset > 0)
                d = SliceLine(d, start.CharacterOffset);
            if (RetailChatColorTable.TryGetColor(d.LogTextType, out Vector4 resolved))
                currentColor = resolved;
            int searchFrom = 0;
            // Where this message's text begins relative to the message the log holds: the
            // oldest shown message can have its head cut off by the character budget, and a
            // line identity has to survive that cut moving.
            int messageOffset = lineIndex == start.LineIndex ? start.CharacterOffset : 0;
            foreach (string frag in WrapText(d.Text, maxW, measure))
            {
                result.Add(new UiText.Line(frag, currentColor));

                if (runsPerLine is null && tagsPerLine is null && keysPerLine is null)
                    continue;

                int at = frag.Length == 0
                    ? searchFrom
                    : d.Text.IndexOf(frag, searchFrom, StringComparison.Ordinal);
                bool located = at >= 0;
                if (located && frag.Length > 0)
                    searchFrom = at + frag.Length;

                keysPerLine?.Add(new UiText.LineKey(
                    d.Sequence,
                    messageOffset + (located ? at : searchFrom)));

                if (runsPerLine is null && tagsPerLine is null)
                    continue;

                if (d.Spans is not { Count: > 0 } spans || frag.Length == 0 || !located)
                {
                    runsPerLine?.Add(null);
                    tagsPerLine?.Add(null);
                    continue;
                }

                runsPerLine?.Add(RunsForFragment(
                    spans,
                    at,
                    frag.Length,
                    currentColor,
                    tagColor ?? currentColor));
                tagsPerLine?.Add(TaggedRangesForFragment(spans, at, frag.Length));
            }
        }
        return result;
    }

    public static IEnumerable<string> WrapText(string text, float maxW, Func<string, float> measure)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield return string.Empty;
            yield break;
        }

        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (string segment in normalized.Split('\n'))
        {
            foreach (string frag in WrapSingleLine(segment, maxW, measure))
                yield return frag;
        }
    }

    private static IEnumerable<string> WrapSingleLine(string text, float maxW, Func<string, float> measure)
    {
        if (text.Length == 0 || maxW <= 0f || measure(text) <= maxW)
        {
            yield return text;
            yield break;
        }

        var line = new System.Text.StringBuilder();
        foreach (var word in text.Split(' '))
        {
            string sep = line.Length > 0 ? " " : string.Empty;
            if (measure(line.ToString() + sep + word) <= maxW)
            {
                line.Append(sep).Append(word);
                continue;
            }
            if (line.Length > 0 && measure(word) <= maxW)
            {
                yield return line.ToString();            // word fits alone → push to a new line
                line.Clear();
                line.Append(word);
                continue;
            }
            if (line.Length > 0) line.Append(' ');
            foreach (char ch in word)
            {
                if (line.Length > 0 && measure(line.ToString() + ch) > maxW)
                {
                    yield return line.ToString();
                    line.Clear();
                }
                line.Append(ch);
            }
        }
        if (line.Length > 0) yield return line.ToString();
    }
}
