using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

public sealed class RetailFixtureConformanceTests
{
    [Theory]
    [MemberData(nameof(LayoutCases))]
    public void ProductionFixture_PreservesRootAndCanonicalStates(
        Func<ElementInfo> load,
        uint rootId,
        uint rootType,
        float width,
        float height,
        int minimumNodeCount)
    {
        var root = load();

        Assert.Equal(rootId, root.Id);
        Assert.Equal(rootType, root.Type);
        Assert.Equal(width, root.Width);
        Assert.Equal(height, root.Height);
        Assert.True(CountNodes(root) >= minimumNodeCount);
        Assert.Contains(UiStateInfo.DirectStateId, root.States.Keys);
        Assert.Contains(Enumerate(root), element => element.States.Count > 1);
        Assert.Contains(Enumerate(root), element =>
            element.States.Values.Any(state => state.Properties.Values.Count > 0));
    }

    [Fact]
    public void ToolbarFixture_ContainsObjectButtonsAndRetailButtonStates()
    {
        var root = FixtureLoader.LoadToolbarInfos();

        var character = Find(root, 0x10000199u);
        var inventory = Find(root, 0x100001B1u);

        Assert.NotNull(character);
        Assert.NotNull(inventory);
        Assert.Contains(1u, character.States.Keys); // Normal
        Assert.Contains(3u, character.States.Keys); // Normal_pressed
        Assert.Contains(6u, character.States.Keys); // Highlight
    }

    [Fact]
    public void InventoryFixture_ContainsMountedBackpackAndPaperdollContent()
    {
        var root = FixtureLoader.LoadInventoryInfos();

        Assert.NotNull(Find(root, InventoryController.ContentsGridId));
        Assert.NotNull(Find(root, InventoryController.ContainerListId));
        Assert.NotNull(Find(root, InventoryController.BurdenMeterId));
        Assert.NotNull(Find(root, PaperdollController.DollViewportId));
        Assert.NotNull(Find(root, PaperdollController.DollDragMaskId));

        var layout = FixtureLoader.LoadInventory();
        Assert.IsType<UiButton>(layout.FindElement(PaperdollController.DollDragMaskId));
    }

    [Fact]
    public void CharacterFixture_ContainsAllThreeRuntimePages()
    {
        var root = FixtureLoader.LoadCharacterInfos();

        Assert.NotNull(Find(root, CharacterStatController.AttributesPageId));
        Assert.NotNull(Find(root, CharacterStatController.SkillsPageId));
        Assert.NotNull(Find(root, CharacterStatController.TitlesPageId));
    }

    [Fact]
    public void ExaminationFixture_ContainsRetailChromeSubviewsAndScrollbars()
    {
        ElementInfo root = FixtureLoader.LoadExaminationInfos();

        Assert.Equal(AppraisalUiController.RootId, root.Id);
        Assert.Equal(310f, root.Width);
        Assert.Equal(400f, root.Height);
        Assert.NotNull(Find(root, AppraisalUiController.CloseId));
        Assert.NotNull(Find(root, AppraisalUiController.TitleId));
        Assert.NotNull(Find(root, AppraisalUiController.ItemPanelId));
        Assert.NotNull(Find(root, AppraisalUiController.ItemTextId));
        Assert.NotNull(Find(root, AppraisalUiController.ItemScrollbarId));
        Assert.NotNull(Find(root, AppraisalUiController.CreaturePanelId));
        Assert.NotNull(Find(root, AppraisalUiController.SpellPanelId));

        ImportedLayout layout = FixtureLoader.LoadExamination();
        Assert.IsType<UiButton>(layout.FindElement(AppraisalUiController.CloseId));
        Assert.IsType<UiText>(layout.FindElement(AppraisalUiController.TitleId));
        Assert.IsType<UiScrollbar>(
            layout.FindElement(AppraisalUiController.ItemScrollbarId));
    }

    public static TheoryData<Func<ElementInfo>, uint, uint, float, float, int> LayoutCases
        => new()
        {
            { FixtureLoader.LoadToolbarInfos, 0x10000191u, 0x10000007u, 300f, 122f, 20 },
            { FixtureLoader.LoadInventoryInfos, 0x100001CCu, 0x10000023u, 300f, 362f, 40 },
            { FixtureLoader.LoadPaperdollInfos, 0x100001D4u, 0x10000024u, 224f, 214f, 20 },
            { FixtureLoader.LoadCharacterInfos, 0x10000227u, 8u, 300f, 600f, 60 },
        };

    private static int CountNodes(ElementInfo root)
        => Enumerate(root).Count();

    private static IEnumerable<ElementInfo> Enumerate(ElementInfo root)
    {
        yield return root;
        foreach (var child in root.Children)
        foreach (var descendant in Enumerate(child))
            yield return descendant;
    }

    private static ElementInfo? Find(ElementInfo root, uint id)
        => Enumerate(root).FirstOrDefault(element => element.Id == id);
}
