using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AcDream.Core.Journal;

public readonly record struct JournalReadResult(
    IReadOnlyList<JournalPage> Pages,
    string? Error);

public static class JournalFile
{
    private const string NewPage = "<NEWP>";
    private const string PageNumber = "<PNUM>";
    private const string Label = "<LABE>";
    private const string Title = "<TITL>";
    private const string Notes = "<NOTE>";
    private const string Days = "<DAYS>";
    private const string Hours = "<HOUR>";
    private const string Minutes = "<MINU>";
    private const string LocationX = "<LOCX>";
    private const string LocationY = "<LOCY>";
    private const string RunningTime = "<TIME>";

    public const string MalformedFileMessage =
        "Problem loading journal: Your journal file does not create a new page!";

    public static string FileNameFor(string serverName, string characterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentException.ThrowIfNullOrWhiteSpace(characterName);
        return $"Journal-{Sanitize(serverName)}-{Sanitize(characterName)}.txt";
    }

    private static readonly char[] InvalidFileNameChars =
        ['"', '<', '>', '|', ':', '*', '?', '\\', '/'];

    private static string Sanitize(string value)
    {
        var text = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            bool invalid = c < ' '
                || Array.IndexOf(InvalidFileNameChars, c) >= 0;
            text.Append(invalid ? '_' : c);
        }
        return text.ToString();
    }

    public static JournalReadResult Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var pages = new List<JournalPage>();
        JournalPage? current = null;
        bool sawAnyContent = false;

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
                continue;

            if (!line.StartsWith('<'))
            {
                sawAnyContent = true;
                continue;
            }

            string tag = line.Length >= 6 ? line[..6] : line;
            string value = line.Length > 7 ? line[7..] : string.Empty;

            if (tag == NewPage)
            {
                if (current is not null)
                    pages.Add(current);
                current = JournalPage.Empty;
                sawAnyContent = true;
                continue;
            }

            if (current is null)
            {
                return new JournalReadResult([], MalformedFileMessage);
            }

            sawAnyContent = true;
            current = tag switch
            {
                Label => current with { Label = value },
                Title => current with { Title = value },
                Notes => current with { Notes = value },
                Days => current with { TimerDays = ParseInt(value) },
                Hours => current with { TimerHours = ParseInt(value) },
                Minutes => current with { TimerMinutes = ParseInt(value) },
                LocationX => current with
                {
                    LocationX = ParseFloat(value),
                    HasLocation = true,
                },
                LocationY => current with
                {
                    LocationY = ParseFloat(value),
                    HasLocation = true,
                },
                RunningTime => current with { RunningTimerSeconds = ParseFloat(value) },
                _ => current,
            };
        }

        if (current is not null)
            pages.Add(current);

        if (pages.Count == 0 && sawAnyContent)
            return new JournalReadResult([], MalformedFileMessage);

        for (int i = 0; i < pages.Count; i++)
            pages[i] = pages[i].Clipped();

        return new JournalReadResult(pages, null);
    }

    public static string Write(IReadOnlyList<JournalPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);

        var text = new StringBuilder();
        for (int i = 0; i < pages.Count; i++)
        {
            JournalPage page = pages[i];
            text.Append(NewPage).Append('\n');
            Append(text, PageNumber, (i + 1).ToString(CultureInfo.InvariantCulture));
            Append(text, Label, page.Label);
            Append(text, Title, page.Title);
            Append(text, Notes, page.Notes);
            Append(text, Days, page.TimerDays.ToString(CultureInfo.InvariantCulture));
            Append(text, Hours, page.TimerHours.ToString(CultureInfo.InvariantCulture));
            Append(text, Minutes, page.TimerMinutes.ToString(CultureInfo.InvariantCulture));
            if (page.HasLocation)
            {
                Append(text, LocationX, Format(page.LocationX));
                Append(text, LocationY, Format(page.LocationY));
            }

            Append(text, RunningTime, Format(page.RunningTimerSeconds));
        }

        return text.ToString();

        static void Append(StringBuilder text, string tag, string value)
        {
            text.Append(tag).Append(' ')
                .Append(value.Replace('\n', ' ').Replace('\r', ' '))
                .Append('\n');
        }
    }

    private static string Format(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    private static int ParseInt(string value) =>
        int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result
            : 0;

    private static float ParseFloat(string value) =>
        float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float result)
            ? result
            : 0f;
}
