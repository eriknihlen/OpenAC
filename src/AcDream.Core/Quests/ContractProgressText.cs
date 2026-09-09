using System;
using System.Globalization;
using AcDream.Core.Ui;

namespace AcDream.Core.Quests;

public static class ContractProgressText
{
    public static string Build(
        uint stage,
        double timeWhenRepeats,
        DateTime receivedAt,
        ContractEntry entry,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (stage == 1u) return "Available";
        if (stage == 2u) return "In Progress";

        if (stage == 3u)
        {
            if (timeWhenRepeats <= 0d)
            {
                return entry.QuestflagRepeatTime.Length == 0 ? "Done" : "Available";
            }

            double elapsed = (now - receivedAt).TotalSeconds;
            double remaining = timeWhenRepeats - elapsed;
            if (remaining <= 0d) return "Available";

            return $"Done ({RetailDurationText.Format(remaining)} to Repeat)";
        }

        if (stage >= 4u)
        {
            if (entry.DescriptionProgress.Length == 0) return "In Progress";
            return FormatProgress(entry.DescriptionProgress, stage - 4u);
        }

        return string.Empty;
    }

    private static string FormatProgress(string format, uint value)
    {
        int at = format.IndexOf("%d", StringComparison.Ordinal);
        if (at < 0) return format;

        return string.Concat(
            format.AsSpan(0, at),
            value.ToString(CultureInfo.InvariantCulture),
            format.AsSpan(at + 2));
    }
}
