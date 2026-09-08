using AcDream.App.Configuration;

namespace AcDream.App.Tests.Configuration;

public sealed class SessionConfigArgumentParsingTests
{
    private const string Flag = "--session-config";

    [Fact]
    public void FlagWithAFollowingValueReturnsThatValueAndIsPresent()
    {
        string? value = SessionConfigArgumentParsing.ExtractFlagValue(
            ["D:\\dats", Flag, "session.json"],
            Flag,
            out bool present);

        Assert.True(present);
        Assert.Equal("session.json", value);
    }

    [Fact]
    public void FlagAbsentReturnsNullAndIsNotPresent()
    {
        string? value = SessionConfigArgumentParsing.ExtractFlagValue(
            ["D:\\dats"],
            Flag,
            out bool present);

        Assert.False(present);
        Assert.Null(value);
    }

    [Fact]
    public void TrailingFlagWithNoValueIsPresentWithANullValue()
    {
        string? value = SessionConfigArgumentParsing.ExtractFlagValue(
            ["D:\\dats", Flag],
            Flag,
            out bool present);

        Assert.True(present);
        Assert.Null(value);
    }

    [Fact]
    public void FlagAloneAsTheOnlyArgumentIsPresentWithANullValue()
    {
        string? value = SessionConfigArgumentParsing.ExtractFlagValue(
            [Flag],
            Flag,
            out bool present);

        Assert.True(present);
        Assert.Null(value);
    }

    [Fact]
    public void WithoutFlagAndValueDropsTheFlagAndItsValue()
    {
        string[] positional = SessionConfigArgumentParsing.WithoutFlagAndValue(
            ["D:\\dats", Flag, "session.json", "extra"],
            Flag);

        Assert.Equal(["D:\\dats", "extra"], positional);
    }

    [Fact]
    public void WithoutFlagAndValueTrailingFlagDropsOnlyTheFlagItself()
    {
        string[] positional = SessionConfigArgumentParsing.WithoutFlagAndValue(
            ["D:\\dats", Flag],
            Flag);

        Assert.Equal(["D:\\dats"], positional);
    }

    [Fact]
    public void WithoutFlagAndValueLeavesArgumentsUnchangedWhenFlagIsAbsent()
    {
        string[] positional = SessionConfigArgumentParsing.WithoutFlagAndValue(
            ["D:\\dats"],
            Flag);

        Assert.Equal(["D:\\dats"], positional);
    }
}
