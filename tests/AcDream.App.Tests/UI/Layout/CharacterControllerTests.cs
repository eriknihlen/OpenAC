using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

public sealed class CharacterControllerTests
{
    [Fact]
    public void AuthoredPanel_BindsTextScrollbarAndClose()
    {
        ImportedLayout layout = FixtureLoader.LoadCharacterInformation();
        int closes = 0;

        CharacterController.Bind(
            layout, SampleData.SampleCharacter, close: () => closes++);

        UiText text = Assert.IsType<UiText>(
            layout.FindElement(CharacterController.MainTextId));
        UiScrollbar scrollbar = Assert.IsType<UiScrollbar>(
            layout.FindElement(CharacterController.ScrollbarId));
        Assert.Same(text.Scroll, scrollbar.Model);
        Assert.False(text.PreserveEndOnLayout);
        Assert.Contains("You were born on", VisibleText(text));

        UiButton close = Assert.IsType<UiButton>(
            layout.FindElement(CharacterController.CloseId));
        close.OnEvent(new UiEvent(0, close, UiEventType.Click));
        Assert.Equal(1, closes);
    }

    [Fact]
    public void Report_UsesRetailContentInsteadOfInventedCharacterSheetSummary()
    {
        string report = Report(SampleData.SampleCharacter());

        Assert.DoesNotContain("Studio Player", report);
        Assert.DoesNotContain("Level 126", report);
        Assert.DoesNotContain("Health:", report);
        Assert.DoesNotContain("Augmentations", report);
        Assert.DoesNotContain("Encumbrance", report);

        Assert.Contains("You have died 42 times.", report);
        Assert.Contains("Natural Resistances:", report);
        Assert.Contains("Drain Resistances:", report);
        Assert.Contains("Regeneration Bonus:", report);
        Assert.Contains("Innate Strength: 200", report);
        Assert.Contains("Innate Coordination: 10", report);
        Assert.Contains("Chess Rank: 12", report);
        Assert.Contains("Fishing Skill: 4", report);
        Assert.Contains("Your melee mastery is Swords.", report);
        Assert.Contains("You are not overburdened at this time.", report);
    }

    [Theory]
    [InlineData(200, "None")]
    [InlineData(201, "Poor")]
    [InlineData(260, "Poor")]
    [InlineData(261, "Mediocre")]
    [InlineData(321, "Hardy")]
    [InlineData(381, "Resilient")]
    [InlineData(441, "Indomitable")]
    public void ResistanceGrade_UsesRetailInclusiveBoundaries(
        int value, string expected)
        => Assert.Equal(expected, CharacterController.ResistanceGrade(
            value, 200, 260, 320, 380, 440));

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "1 second")]
    [InlineData(61, "1 minute 1 second")]
    [InlineData(3_196_800, "1 month 1 week")]
    [InlineData(31_536_000, "1 year")]
    public void Duration_UsesRetailUnitDecomposition(int seconds, string expected)
        => Assert.Equal(expected, CharacterController.FormatRetailDuration(seconds));

    [Fact]
    public void DeathCount_UsesRetailSpecialCases()
    {
        Assert.Contains("never died", Report(new CharacterSheet { Deaths = 0 }));
        Assert.Contains("only once", Report(new CharacterSheet { Deaths = 1 }));
        Assert.Contains("died twice", Report(new CharacterSheet { Deaths = 2 }));
        Assert.Contains("died 3 times", Report(new CharacterSheet { Deaths = 3 }));
    }

    [Fact]
    public void EmptyLayout_GetsFallbackMainText()
    {
        ImportedLayout layout = FakeLayout();
        CharacterController.Bind(layout, SampleData.SampleCharacter);
        Assert.Contains(layout.Root.Children.OfType<UiText>(),
            text => text.EventId == CharacterController.MainTextId);
    }

    [Fact]
    public void Stable_draws_reuse_report_until_source_or_layout_changes()
    {
        ImportedLayout layout = FixtureLoader.LoadCharacterInformation();
        int builds = 0;
        int deaths = 1;
        Action? changed = null;
        using CharacterInformationUiController controller =
            CharacterController.Bind(
                layout,
                () =>
                {
                    builds++;
                    return new CharacterSheet { Deaths = deaths };
                },
                subscribeChanged: handler =>
                {
                    changed = handler;
                    return new TestSubscription();
                });
        UiText text = Assert.IsType<UiText>(
            layout.FindElement(CharacterController.MainTextId));

        IReadOnlyList<UiText.Line> first = text.LinesProvider();
        IReadOnlyList<UiText.Line> second = text.LinesProvider();
        Assert.Same(first, second);
        Assert.Equal(1, builds);

        deaths = 2;
        changed!();
        IReadOnlyList<UiText.Line> changedLines = text.LinesProvider();
        Assert.NotSame(first, changedLines);
        Assert.Equal(2, builds);

        text.Width -= 24f;
        IReadOnlyList<UiText.Line> resized = text.LinesProvider();
        Assert.NotSame(changedLines, resized);
        Assert.Equal(2, builds);
    }

    private static string Report(CharacterSheet sheet)
        => CharacterController.BuildReport(sheet, CharacterInfoStrings.English);

    private static string VisibleText(UiText text)
        => string.Join('\n', text.LinesProvider().Select(line => line.Text));

    private static ImportedLayout FakeLayout()
        => new(new UiPanel { Width = 300f, Height = 362f },
            new Dictionary<uint, UiElement>());

    private sealed class TestSubscription : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
