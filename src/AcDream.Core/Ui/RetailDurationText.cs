using System;
using System.Globalization;
using System.Text;

namespace AcDream.Core.Ui;

public static class RetailDurationText
{
    private const int SecondsPerMonth = 0x278D00;   // 2,592,000 — a 30-day month
    private const int SecondsPerDay = 0x15180;      // 86,400
    private const int SecondsPerHour = 0xE10;       // 3,600
    private const int SecondsPerMinute = 0x3C;      // 60

    public static string Format(double seconds)
    {
        long total = (long)seconds;
        if (total < 0) total = 0;

        long months = total / SecondsPerMonth;
        long rest = total % SecondsPerMonth;
        long days = rest / SecondsPerDay;
        rest %= SecondsPerDay;
        long hours = rest / SecondsPerHour;
        rest %= SecondsPerHour;
        long minutes = rest / SecondsPerMinute;
        long secs = rest % SecondsPerMinute;

        var text = new StringBuilder();
        if (months != 0) Append(text, months, "mo");
        if (days != 0) Append(text, days, "d");
        if (hours != 0) Append(text, hours, "h");
        if (minutes != 0) Append(text, minutes, "m");
        Append(text, secs, "s");

        // The trailing space the last part just wrote.
        return text.ToString(0, text.Length - 1);

        static void Append(StringBuilder text, long value, string unit)
        {
            text.Append(value.ToString(CultureInfo.InvariantCulture));
            text.Append(unit);
            text.Append(' ');
        }
    }

}
