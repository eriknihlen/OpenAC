using System;
using System.IO;
using System.Linq;
using AcDream.App.UI;
using AcDream.Core.Journal;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Tests.UI;

public sealed class JournalPersistenceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "acdream-journal-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* the test already said what it needed to */ }
    }

    private (RuntimeJournalState Journal, JournalPersistence File, List<string> Reports) New()
    {
        var journal = new RuntimeJournalState();
        var reports = new List<string>();
        return (journal, new JournalPersistence(journal, _directory, reports.Add), reports);
    }

    [Fact]
    public void APageWrittenInOneSessionIsThereInTheNext()
    {
        // The whole point of the feature.
        (RuntimeJournalState first, JournalPersistence firstFile, _) = New();
        firstFile.Load("Acdream");
        first.NewPage();
        first.UpdateCurrent("label", "A title", "Some notes.");
        Assert.True(firstFile.Save(Now));
        first.Dispose();

        (RuntimeJournalState second, JournalPersistence secondFile, _) = New();
        secondFile.Load("Acdream");

        JournalPage page = Assert.Single(second.View.Pages);
        Assert.Equal("A title", page.Title);
        Assert.Equal("Some notes.", page.Notes);
        second.Dispose();
    }

    [Fact]
    public void EachCharacterGetsItsOwnFile()
    {
        (RuntimeJournalState journal, JournalPersistence file, _) = New();

        file.Load("Acdream");
        journal.NewPage();
        journal.UpdateCurrent("a", "Acdream's page", string.Empty);
        file.Save(Now);

        file.Load("Someone Else");

        Assert.Empty(journal.View.Pages);
        journal.Dispose();
    }

    [Fact]
    public void ACharacterWithNoFileLoadsAnEmptyJournalWithoutComplaining()
    {
        (RuntimeJournalState journal, JournalPersistence file, List<string> reports) = New();

        file.Load("Newcomer");

        Assert.Empty(journal.View.Pages);
        Assert.Empty(reports);
        journal.Dispose();
    }

    [Fact]
    public void AMalformedFileReportsAndLeavesTheJournalEmpty()
    {
        // Half-reading a notebook loses pages, and the next save would then
        // write that loss back over the original.
        (RuntimeJournalState journal, JournalPersistence file, List<string> reports) = New();
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            Path.Combine(_directory, JournalFile.FileNameFor("acdream", "Acdream")),
            "<TITL> no page marker first\n");

        file.Load("Acdream");

        Assert.Equal(JournalFile.MalformedFileMessage, Assert.Single(reports));
        Assert.Empty(journal.View.Pages);
        journal.Dispose();
    }

    [Fact]
    public void SavingIsSkippedWhenNothingChanged()
    {
        (RuntimeJournalState journal, JournalPersistence file, _) = New();
        file.Load("Acdream");
        journal.NewPage();
        Assert.True(file.Save(Now));

        Assert.False(file.Save(Now));
        journal.Dispose();
    }

    [Fact]
    public void SavingBeforeAnyCharacterIsLoadedIsANoOp()
    {
        (RuntimeJournalState journal, JournalPersistence file, _) = New();

        Assert.False(file.Save(Now));
        Assert.False(Directory.Exists(_directory));
        journal.Dispose();
    }

    [Fact]
    public void ARunningTimerPersistsItsREMAININGTime()
    {
        // Saving the value it started at would resurrect the full duration on
        // every reload.
        (RuntimeJournalState journal, JournalPersistence file, _) = New();
        file.Load("Acdream");
        journal.NewPage();
        journal.SetTimer(0, 1, 0);
        journal.StartTimer(Now);

        file.Save(Now.AddMinutes(30));
        journal.Dispose();

        (RuntimeJournalState reloaded, JournalPersistence reloadedFile, _) = New();
        reloadedFile.Load("Acdream");

        Assert.Equal(1800d, Assert.Single(reloaded.View.Pages).RunningTimerSeconds);
        reloaded.Dispose();
    }

    [Fact]
    public void CloseSavesAndForgetsTheCharacter()
    {
        (RuntimeJournalState journal, JournalPersistence file, _) = New();
        file.Load("Acdream");
        journal.NewPage();

        file.Close(Now);

        Assert.Null(file.CurrentPath);
        Assert.False(file.Save(Now));
        journal.Dispose();
    }

    [Fact]
    public void AnUnwritableDirectoryReportsRatherThanThrowing()
    {
        var journal = new RuntimeJournalState();
        var reports = new List<string>();
        string blocked = Path.Combine(_directory, "blocked");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(blocked, "not a directory");

        var file = new JournalPersistence(journal, blocked, reports.Add);
        file.Load("Acdream");
        journal.NewPage();

        Assert.False(file.Save(Now));
        Assert.NotEmpty(reports);
        journal.Dispose();
    }
}
