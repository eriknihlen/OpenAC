using System;

namespace AcDream.Core.World;

public static class DerethDateTime
{
    public const int HoursInADay = 16;
    public const int DaysInAMonth = 30;
    public const int MonthsInAYear = 12;

    public const double DayTicks = 7620.0;

    /// <summary>Ticks per Derethian hour (DayTicks / 16).</summary>
    public const double HourTicks = DayTicks / HoursInADay;     // 476.25

    /// <summary>Ticks per Derethian month.</summary>
    public const double MonthTicks = DayTicks * DaysInAMonth;    // 228,600

    /// <summary>Ticks per Derethian year.</summary>
    public const double YearTicks = MonthTicks * MonthsInAYear;  // 2,743,200

    public const double MaxTicks = 1_073_741_828.0;

    public const int ZeroYear = 10;

    /// <summary>
    /// The 16 named hour slots (r12 §1.2). Each one is half a Derethian
    /// hour; the "-and-Half" variants are the second half.
    /// </summary>
    public enum HourName
    {
        Darktide = 0,
        DarktideAndHalf,
        Foredawn,
        ForedawnAndHalf,
        Dawnsong,           // day starts here (hour 5)
        DawnsongAndHalf,
        Morntide,
        MorntideAndHalf,
        Midsong,
        MidsongAndHalf,
        Warmtide,
        WarmtideAndHalf,    // day ends here (hour 12)
        Evensong,
        EvensongAndHalf,
        Gloaming,
        GloamingAndHalf,
    }

    public enum MonthName
    {
        Morningthaw = 0,
        Solclaim,
        Seedsow,
        Leafdawning,
        Verdantine,
        Thistledown,
        Harvestgain,
        Leafcull,
        Frostfell,
        Snowreap,
        Coldeve,
        Wintersebb,
    }

    public const double DayFractionOriginOffsetTicks = (7.0 / 16.0) * DayTicks; // 3333.75

    public const double OriginOffsetTicks = DayFractionOriginOffsetTicks;

    public static double DayFraction(double ticks)
    {
        if (ticks < 0) ticks = 0;
        double shifted = ticks + OriginOffsetTicks;
        double rem = shifted - Math.Floor(shifted / DayTicks) * DayTicks;
        return rem / DayTicks;
    }

    public static HourName CurrentHour(double ticks)
    {
        double f = DayFraction(ticks);
        int slot = (int)Math.Floor(f * HoursInADay);
        if (slot < 0) slot = 0;
        if (slot > 15) slot = 15;
        return (HourName)slot;
    }

    public static bool IsDaytime(double ticks)
    {
        int h = (int)CurrentHour(ticks);
        return h >= (int)HourName.Dawnsong && h <= (int)HourName.WarmtideAndHalf;
    }

    public readonly record struct Calendar(int Year, MonthName Month, int Day, HourName Hour);

    public static Calendar ToCalendar(double ticks)
    {
        if (ticks < 0) ticks = 0;
        double shifted = ticks + OriginOffsetTicks;
        int relativeYear = (int)(shifted / YearTicks);
        double tYear = shifted - relativeYear * YearTicks;
        int monthIdx = (int)(tYear / MonthTicks);
        if (monthIdx > 11) monthIdx = 11;
        double tMonth = tYear - monthIdx * MonthTicks;
        int day = (int)(tMonth / DayTicks) + 1;
        if (day > DaysInAMonth) day = DaysInAMonth;

        return new Calendar(relativeYear + ZeroYear, (MonthName)monthIdx, day, CurrentHour(ticks));
    }

    public static int Year(double ticks)
    {
        if (ticks < 0) ticks = 0;
        double shifted = ticks + OriginOffsetTicks;
        return (int)(shifted / YearTicks);
    }

    public static int AbsoluteYear(double ticks) => Year(ticks) + ZeroYear;

    public static int DayOfYear(double ticks)
    {
        if (ticks < 0) ticks = 0;
        double shifted = ticks + OriginOffsetTicks;
        int year = (int)(shifted / YearTicks);
        double tYear = shifted - year * YearTicks;
        int d = (int)(tYear / DayTicks);
        if (d < 0) d = 0;
        if (d > 359) d = 359;
        return d;
    }
}
