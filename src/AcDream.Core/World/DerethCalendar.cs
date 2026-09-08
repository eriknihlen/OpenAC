namespace AcDream.Core.World;

public sealed class DerethCalendar
{
    public DerethCalendar(
        double originOffsetTicks = DerethDateTime.DayFractionOriginOffsetTicks)
    {
        SetOriginOffset(originOffsetTicks);
    }

    public double OriginOffsetTicks { get; private set; }

    /// <summary>
    /// Installs the Region DAT origin for this world lifetime.
    /// </summary>
    public void SetOriginOffset(double originOffsetTicks)
    {
        if (!double.IsFinite(originOffsetTicks))
            throw new ArgumentOutOfRangeException(nameof(originOffsetTicks));

        OriginOffsetTicks = originOffsetTicks;
    }

    public double DayFraction(double ticks)
    {
        double shifted = Shift(ticks);
        double rem = shifted
            - Math.Floor(shifted / DerethDateTime.DayTicks)
            * DerethDateTime.DayTicks;
        return rem / DerethDateTime.DayTicks;
    }

    public DerethDateTime.HourName CurrentHour(double ticks)
    {
        int slot = (int)Math.Floor(
            DayFraction(ticks) * DerethDateTime.HoursInADay);
        slot = Math.Clamp(slot, 0, DerethDateTime.HoursInADay - 1);
        return (DerethDateTime.HourName)slot;
    }

    public bool IsDaytime(double ticks)
    {
        int hour = (int)CurrentHour(ticks);
        return hour >= (int)DerethDateTime.HourName.Dawnsong
            && hour <= (int)DerethDateTime.HourName.WarmtideAndHalf;
    }

    public DerethDateTime.Calendar ToCalendar(double ticks)
    {
        double shifted = Shift(ticks);
        int relativeYear = (int)(shifted / DerethDateTime.YearTicks);
        double withinYear =
            shifted - relativeYear * DerethDateTime.YearTicks;
        int month = Math.Min(
            DerethDateTime.MonthsInAYear - 1,
            (int)(withinYear / DerethDateTime.MonthTicks));
        double withinMonth =
            withinYear - month * DerethDateTime.MonthTicks;
        int day = Math.Min(
            DerethDateTime.DaysInAMonth,
            (int)(withinMonth / DerethDateTime.DayTicks) + 1);

        return new DerethDateTime.Calendar(
            relativeYear + DerethDateTime.ZeroYear,
            (DerethDateTime.MonthName)month,
            day,
            CurrentHour(ticks));
    }

    public int Year(double ticks) =>
        (int)(Shift(ticks) / DerethDateTime.YearTicks);

    public int AbsoluteYear(double ticks) =>
        Year(ticks) + DerethDateTime.ZeroYear;

    public int DayOfYear(double ticks)
    {
        double shifted = Shift(ticks);
        int year = (int)(shifted / DerethDateTime.YearTicks);
        double withinYear =
            shifted - year * DerethDateTime.YearTicks;
        return Math.Clamp(
            (int)(withinYear / DerethDateTime.DayTicks),
            0,
            DerethDateTime.DaysInAMonth
                * DerethDateTime.MonthsInAYear
                - 1);
    }

    private double Shift(double ticks)
    {
        if (!double.IsFinite(ticks))
            throw new ArgumentOutOfRangeException(nameof(ticks));
        return Math.Max(0d, ticks) + OriginOffsetTicks;
    }
}
