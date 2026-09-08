using AcDream.Core.World;
using Xunit;

namespace AcDream.Core.Tests.World;

[Collection(DerethDateTimeCollection.Name)]
public sealed class DerethDateTimeTests
{

    [Fact]
    public void DayFraction_AtTick0_IsMorntideAndHalf()
    {
        // Tick 0 = Morntide-and-Half = slot 7 = 7/16 of the day.
        Assert.Equal(7.0 / 16.0, DerethDateTime.DayFraction(0), 4);
    }

    [Fact]
    public void DayFraction_AtHalfDayFromTick0_IsHalf()
    {
        Assert.Equal(15.0 / 16.0, DerethDateTime.DayFraction(DerethDateTime.DayTicks / 2), 4);
    }

    [Fact]
    public void DayFraction_WrapsAfterOneDay()
    {
        // Full day from tick 0 returns to the same slot (Morntide-and-Half = 7/16).
        Assert.Equal(7.0 / 16.0, DerethDateTime.DayFraction(DerethDateTime.DayTicks), 4);
    }

    [Fact]
    public void CurrentHour_AtTick0_IsMorntideAndHalf()
    {
        Assert.Equal(DerethDateTime.HourName.MorntideAndHalf, DerethDateTime.CurrentHour(0));
    }

    [Fact]
    public void CurrentHour_AtMidnight_IsDarktide()
    {
        double ticks = DerethDateTime.DayTicks * (9.0 / 16.0);
        Assert.Equal(DerethDateTime.HourName.Darktide, DerethDateTime.CurrentHour(ticks));
    }

    [Fact]
    public void CurrentHour_AtNoon_IsMidsong()
    {
        // Midsong is slot 8 on the 16-slot scale. From tick 0 (slot 7) advance by 1 slot.
        double ticks = DerethDateTime.DayTicks * (1.0 / 16.0);
        Assert.Equal(DerethDateTime.HourName.Midsong, DerethDateTime.CurrentHour(ticks));
    }

    [Fact]
    public void IsDaytime_Tick0_True()
    {
        // Morntide-and-Half (slot 7) falls in the daytime band (slots 4..11).
        Assert.True(DerethDateTime.IsDaytime(0));
    }

    [Fact]
    public void IsDaytime_Darktide_False()
    {
        // Darktide = slot 0. Need tick offset of 9/16 from tick 0 to reach it.
        double ticks = DerethDateTime.DayTicks * (9.0 / 16.0);
        Assert.False(DerethDateTime.IsDaytime(ticks));
    }

    [Fact]
    public void ToCalendar_PY10Day1_Morningthaw()
    {
        var cal = DerethDateTime.ToCalendar(0);
        Assert.Equal(DerethDateTime.ZeroYear, cal.Year);
        Assert.Equal(DerethDateTime.MonthName.Morningthaw, cal.Month);
        Assert.Equal(1, cal.Day);
    }

    [Fact]
    public void ToCalendar_AdvancesCorrectly()
    {
        // One year from start → PY (10 + 1) = 11, Morningthaw 1.
        var cal = DerethDateTime.ToCalendar(DerethDateTime.YearTicks);
        Assert.Equal(DerethDateTime.ZeroYear + 1, cal.Year);
        Assert.Equal(DerethDateTime.MonthName.Morningthaw, cal.Month);
        Assert.Equal(1, cal.Day);

        // One month into year 11 → Solclaim (next month after Morningthaw).
        var cal2 = DerethDateTime.ToCalendar(DerethDateTime.YearTicks + DerethDateTime.MonthTicks);
        Assert.Equal(DerethDateTime.ZeroYear + 1, cal2.Year);
        Assert.Equal(DerethDateTime.MonthName.Solclaim, cal2.Month);
    }

    [Fact]
    public void ToCalendar_TickAtSeedsow24Year106_MatchesRetailFormat()
    {
        var calendar = new DerethCalendar(3600.0);
        var cal = calendar.ToCalendar(291_408_060.0);
        Assert.Equal(DerethDateTime.ZeroYear + 106, cal.Year);
        Assert.Equal(DerethDateTime.MonthName.Seedsow, cal.Month);
        Assert.Equal(24, cal.Day);
    }
}
