using AcDream.App.Rendering;
using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.App.Tests.Rendering;

public sealed class RenderFrameTitleSubjectTests
{
    private const uint Aldric = 0x50000001u;
    private const uint Brienne = 0x50000002u;

    [Fact]
    public void InTheWorld_TheNameIsTheCharacterListEntryForThePlayer()
    {
        // The server may name an admin's object in the world without the "+"
        // the account's character list gives it; the title uses the list.
        using var selection = Selection();
        selection.CompleteEnter(Aldric);

        RenderFrameTitleSubject subject = RenderFrameTitleSubject.Of(selection.View, Aldric);

        Assert.True(subject.InWorld);
        Assert.Equal("+Aldric", subject.CharacterName);
    }

    [Fact]
    public void BeforeTheWorld_ThereIsNoSubjectEvenWithAListedPlayer()
    {
        using var selection = Selection();

        RenderFrameTitleSubject subject = RenderFrameTitleSubject.Of(selection.View, Aldric);

        Assert.False(subject.InWorld);
        Assert.Null(subject.CharacterName);
    }

    [Fact]
    public void InTheWorld_APlayerMissingFromTheListHasNoName()
    {
        using var selection = Selection();
        selection.CompleteEnter(Aldric);

        RenderFrameTitleSubject subject = RenderFrameTitleSubject.Of(selection.View, 0x50000009u);

        Assert.True(subject.InWorld);
        Assert.Null(subject.CharacterName);
    }

    private static RuntimeCharacterSelectionState Selection()
    {
        var selection = new RuntimeCharacterSelectionState();
        selection.Begin(new RuntimeGenerationToken(1));
        selection.ApplyRoster(new LiveSessionRosterReport(
            "account",
            5,
            [
                new LiveSessionRosterEntry(Brienne, "Brienne", 0u),
                new LiveSessionRosterEntry(Aldric, "+Aldric", 0u),
            ]));
        return selection;
    }
}
