using System;
using System.IO;
using AcDream.Core.Chat;

namespace AcDream.Core.Tests.Chat;

public sealed class ChatSessionLogTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "acdream-chatlog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* the test already told us what it needed to */ }
    }

    [Theory]
    [InlineData("aclog", "aclog.txt")]
    [InlineData("aclog.txt", "aclog.txt")]
    [InlineData("aclog.log", "aclog.log")]
    [InlineData("chat.old", "chat.old")]
    public void AnExtensionlessNameGainsDotTxt(string given, string expected)
        => Assert.Equal(expected, ChatSessionLog.EnsureExtension(given));

    [Fact]
    public void LinesLandInTheFileWithTheirTimestampAndANewline()
    {
        using var log = new ChatSessionLog(_directory);

        Assert.True(log.Open("session", out string resolved));
        Assert.Equal("session.txt", resolved);
        log.Write("13:05:09 ", "Dww tells you, \"hello\"");
        log.Write(null, "Welcome to Dereth.");
        log.Close();

        Assert.Equal(
            "13:05:09 Dww tells you, \"hello\"\nWelcome to Dereth.\n",
            File.ReadAllText(Path.Combine(_directory, "session.txt")));
    }

    [Fact]
    public void ReopeningTheSameNameAppendsRatherThanTruncating()
    {
        using var log = new ChatSessionLog(_directory);

        log.Open("session", out _);
        log.Write(null, "first");
        log.Close();

        log.Open("session", out _);
        log.Write(null, "second");
        log.Close();

        Assert.Equal(
            "first\nsecond\n",
            File.ReadAllText(Path.Combine(_directory, "session.txt")));
    }

    [Fact]
    public void OpeningASecondLogClosesTheFirst()
    {
        using var log = new ChatSessionLog(_directory);

        log.Open("one", out _);
        log.Write(null, "to one");
        Assert.True(log.Open("two", out _));
        log.Write(null, "to two");
        log.Close();

        Assert.Equal("to one\n", File.ReadAllText(Path.Combine(_directory, "one.txt")));
        Assert.Equal("to two\n", File.ReadAllText(Path.Combine(_directory, "two.txt")));
    }

    [Fact]
    public void CloseReportsWhetherOneWasOpen()
    {
        using var log = new ChatSessionLog(_directory);

        Assert.False(log.Close());
        log.Open("session", out _);
        Assert.True(log.Close());
        Assert.False(log.Close());
    }

    [Fact]
    public void WritingWithNoLogOpenIsANoOp()
    {
        using var log = new ChatSessionLog(_directory);

        log.Write("13:05:09 ", "nobody is listening");

        Assert.False(log.IsOpen);
        Assert.Null(log.CurrentName);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void AnUnopenableNameReportsFailureInsteadOfThrowing()
    {
        using var log = new ChatSessionLog(_directory);

        Directory.CreateDirectory(Path.Combine(_directory, "taken.txt"));

        Assert.False(log.Open("taken.txt", out string resolved));
        Assert.Equal("taken.txt", resolved);
        Assert.False(log.IsOpen);
    }

    [Fact]
    public void ARootedNameIsHonouredVerbatim()
    {
        using var log = new ChatSessionLog(_directory);
        string rooted = Path.Combine(_directory, "nested", "elsewhere.txt");

        Assert.True(log.Open(rooted, out string resolved));
        Assert.Equal(rooted, resolved);
        log.Write(null, "here");
        log.Close();

        Assert.Equal("here\n", File.ReadAllText(rooted));
    }
}
