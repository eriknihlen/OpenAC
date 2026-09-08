using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.MossTank;
using Xunit;

namespace AcDream.App.Tests.UI;

public sealed class MossTankMarkupBuildOverRealFilesTests
{
    private static string MossTankMarkupDirectory =>
        Path.Combine(AppContext.BaseDirectory, "MossTank");

    public static IEnumerable<object[]> MossTankMarkupFiles() =>
        Directory.GetFiles(MossTankMarkupDirectory, "mosstank*.xml")
            .OrderBy(static path => path, StringComparer.Ordinal)
            .Select(static path => new object[] { path });

    [Theory]
    [MemberData(nameof(MossTankMarkupFiles))]
    public void EveryMossTankPanelFileBuildsAgainstARealPanelWithNoException(string path)
    {
        string xml = File.ReadAllText(path);
        var panel = new MossTankPanel(new StubHost());

        UiNineSlicePanel built = MarkupDocument.Build(xml, panel, static id => (id, 32, 32));

        Assert.NotNull(built);
        Assert.NotEmpty(built.Children);
    }

    [Fact]
    public void WideningTheRealMainPanelWidensTheRealMonstersList()
    {
        string xml = File.ReadAllText(
            Path.Combine(MossTankMarkupDirectory, "mosstank.xml"));
        var panel = new MossTankPanel(new StubHost());

        UiNineSlicePanel built = MarkupDocument.Build(xml, panel, static id => (id, 32, 32));

        UiPanel[] tabGroups = built.Children
            .Where(static child => child.GetType() == typeof(UiPanel))
            .Cast<UiPanel>()
            .ToArray();
        Assert.Equal(9, tabGroups.Length);
        foreach (UiPanel group in tabGroups)
            group.Visible = false;
        UiPanel monstersGroup = tabGroups[3];
        monstersGroup.Visible = true;
        UiMarkupList monstersList = Assert.Single(monstersGroup.Children.OfType<UiMarkupList>());

        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(1400f, 900f));
        var ctx = new UiRenderContext(renderer, new Vector2(1400f, 900f));

        built.DrawSelfAndChildren(ctx);
        float widthAtAuthoredDefault = monstersList.Width;

        built.Width += 100f;
        built.DrawSelfAndChildren(ctx);

        Assert.True(
            monstersList.Width > widthAtAuthoredDefault,
            $"Monsters list width did not grow: {widthAtAuthoredDefault} -> {monstersList.Width}");
    }

    [Fact]
    public void WideningTheRealAdvancedOptionsPopupGrowsTheOptionListWithoutOverlappingItsSibling()
    {
        string xml = File.ReadAllText(
            Path.Combine(MossTankMarkupDirectory, "mosstank-advanced.xml"));
        var panel = new MossTankPanel(new StubHost());

        UiNineSlicePanel built = MarkupDocument.Build(xml, panel, static id => (id, 32, 32));

        Assert.True(built.Resizable);
        Assert.Equal(392f, built.MinWidth);
        Assert.Equal(300f, built.MinHeight);

        // Bypass the VisibleSource binding (bound to AdvancedOptionsVisible,
        // false on the stub automation) the same way the Monsters test
        // bypasses tab visibility — DrawSelfAndChildren's own anchor pass
        // never runs for an invisible element.
        built.Visible = true;

        UiMarkupList optionList = Assert.Single(
            built.Children.OfType<UiMarkupList>(), static list => list.Width == 256f);
        UiMarkupList categoryList = Assert.Single(
            built.Children.OfType<UiMarkupList>(), static list => list.Width == 120f);

        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(1400f, 900f));
        var ctx = new UiRenderContext(renderer, new Vector2(1400f, 900f));

        built.DrawSelfAndChildren(ctx);
        float optionListWidthBefore = optionList.Width;
        float optionListHeightBefore = optionList.Height;
        float categoryListLeftBefore = categoryList.Left;
        float categoryListWidthBefore = categoryList.Width;

        built.Width += 100f;
        built.DrawSelfAndChildren(ctx);

        Assert.True(
            optionList.Width > optionListWidthBefore,
            $"Option list width did not grow: {optionListWidthBefore} -> {optionList.Width}");
        Assert.Equal(optionListHeightBefore, optionList.Height); // height fixed
        Assert.True(
            categoryList.Left > categoryListLeftBefore,
            $"Category list did not track the growing right edge: {categoryListLeftBefore} -> {categoryList.Left}");
        Assert.Equal(categoryListWidthBefore, categoryList.Width); // width fixed, only repositions
        Assert.True(
            optionList.Left + optionList.Width <= categoryList.Left,
            $"Widened option list (right edge {optionList.Left + optionList.Width}) overlaps "
            + $"the repositioned category list (left edge {categoryList.Left}).");
    }

    [Fact]
    public void AdvancedOptionsPopupHasNoElementWithATooltip()
    {
        string xml = File.ReadAllText(
            Path.Combine(MossTankMarkupDirectory, "mosstank-advanced.xml"));
        var panel = new MossTankPanel(new StubHost());

        UiNineSlicePanel built = MarkupDocument.Build(xml, panel, static id => (id, 32, 32));

        AssertNoElementHasATooltip(built);
    }

    private static void AssertNoElementHasATooltip(UiElement element)
    {
        Assert.True(
            element.RuntimeTooltipTextSource is null,
            $"{element.GetType().Name} carries a tooltip — the Advanced "
            + "Options popup must have none (owner's third live look).");
        foreach (UiElement child in element.Children)
            AssertNoElementHasATooltip(child);
    }

    [Theory]
    [InlineData(856f, 236f)] // the panel's own minw/minh floor
    [InlineData(1100f, 320f)] // one enlarged size past the 984x271 default
    public void ResolvedMainPanelHasNoOverlapOrOutOfBoundsChildAtThisSize(
        float width, float height)
    {
        string xml = File.ReadAllText(
            Path.Combine(MossTankMarkupDirectory, "mosstank.xml"));

        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(1400f, 900f));
        var ctx = new UiRenderContext(renderer, new Vector2(1400f, 900f));

        for (int tabIndex = 0; tabIndex < 9; tabIndex++)
        {
            var panel = new MossTankPanel(new StubHost());
            UiNineSlicePanel built = MarkupDocument.Build(xml, panel, static id => (id, 32, 32));
            built.Visible = true;

            UiPanel[] tabGroups = built.Children
                .Where(static child => child.GetType() == typeof(UiPanel))
                .Cast<UiPanel>()
                .ToArray();
            Assert.Equal(9, tabGroups.Length);
            foreach (UiPanel group in tabGroups)
                group.Visible = false;
            tabGroups[tabIndex].Visible = true;

            built.DrawSelfAndChildren(ctx);
            built.Width = width;
            built.Height = height;
            built.DrawSelfAndChildren(ctx);

            AssertResolvedWithinParent(built);
            AssertResolvedNoSiblingOverlap(built);
        }
    }

    private static void AssertResolvedWithinParent(UiElement parent)
    {
        foreach (UiElement child in parent.Children)
        {
            if (!child.Visible)
                continue;
            if (child is not UiLabel)
            {
                if (child.Width > 0f)
                {
                    Assert.True(
                        child.Left + child.Width <= parent.Width + 0.01f,
                        $"{child.GetType().Name} @ ({child.Left},{child.Top},{child.Width},"
                        + $"{child.Height}) crosses the right edge of {parent.GetType().Name} "
                        + $"(w={parent.Width}).");
                }
                if (child.Height > 0f)
                {
                    Assert.True(
                        child.Top + child.Height <= parent.Height + 0.01f,
                        $"{child.GetType().Name} @ ({child.Left},{child.Top},{child.Width},"
                        + $"{child.Height}) crosses the bottom edge of {parent.GetType().Name} "
                        + $"(h={parent.Height}).");
                }
            }
            AssertResolvedWithinParent(child);
        }
    }

    [Theory]
    [InlineData(984f)]
    [InlineData(1100f)]
    public void MonstersMoveUpAndMoveDownIconsStayAdjacentAtEveryWidth(float width)
    {
        string xml = File.ReadAllText(
            Path.Combine(MossTankMarkupDirectory, "mosstank.xml"));
        var panel = new MossTankPanel(new StubHost());

        UiNineSlicePanel built = MarkupDocument.Build(
            xml, panel, static id => (id, 32, 32), icons: new IdentityIconResolver());

        UiPanel[] tabGroups = built.Children
            .Where(static child => child.GetType() == typeof(UiPanel))
            .Cast<UiPanel>()
            .ToArray();
        Assert.Equal(9, tabGroups.Length);
        foreach (UiPanel group in tabGroups)
            group.Visible = false;
        UiPanel monstersGroup = tabGroups[3];
        monstersGroup.Visible = true;

        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(1400f, 900f));
        var ctx = new UiRenderContext(renderer, new Vector2(1400f, 900f));

        built.DrawSelfAndChildren(ctx);
        built.Width = width;
        built.DrawSelfAndChildren(ctx);

        var moveUpQuad = renderer.DebugSpriteSegmentVerts
            .Last(static s => s.Texture == 0x060028FCu);
        var moveDownQuad = renderer.DebugSpriteSegmentVerts
            .Last(static s => s.Texture == 0x060028FDu);

        float gap = moveDownQuad.Verts[0] - moveUpQuad.Verts[0];
        Assert.True(
            gap is >= 20f and <= 26f,
            $"MoveUp/MoveDown icons are {gap}px apart at width {width} — "
            + "expected VTank's fixed ~23px icon pitch, not the growing "
            + "gap a still-last, still-auto MoveDown column would produce.");
    }

    private static void AssertResolvedNoSiblingOverlap(UiElement container)
    {
        UiElement[] children = container.Children
            .Where(static child => child.Visible)
            .ToArray();
        for (int i = 0; i < children.Length; i++)
        {
            for (int j = i + 1; j < children.Length; j++)
            {
                UiElement a = children[i], b = children[j];
                if (a.GetType() == typeof(UiPanel) && b.GetType() == typeof(UiPanel))
                    continue;
                if (a is UiLabel || b is UiLabel)
                    continue;
                Assert.True(
                    !ResolvedRectanglesOverlap(a, b),
                    $"{a.GetType().Name} @ ({a.Left},{a.Top},{a.Width},{a.Height}) overlaps "
                    + $"sibling {b.GetType().Name} @ ({b.Left},{b.Top},{b.Width},{b.Height}).");
            }
        }
        foreach (UiElement child in children)
            AssertResolvedNoSiblingOverlap(child);
    }

    private static bool ResolvedRectanglesOverlap(UiElement a, UiElement b)
    {
        if (a.Width <= 0f || a.Height <= 0f || b.Width <= 0f || b.Height <= 0f)
            return false; // an element with no resolved size never "occupies" space
        return a.Left < b.Left + b.Width && b.Left < a.Left + a.Width
            && a.Top < b.Top + b.Height && b.Top < a.Top + a.Height;
    }

    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }

    private sealed class IdentityIconResolver : IMarkupIconResolver
    {
        public (uint tex, int w, int h) ResolveDid(uint did) =>
            did == 0u ? (0u, 0, 0) : (did, 16, 16);
        public (uint tex, int w, int h) ResolveSpell(uint spellId) =>
            spellId == 0u ? (0u, 0, 0) : (spellId, 16, 16);
        public (uint tex, int w, int h) ResolveItem(uint objectId) =>
            objectId == 0u ? (0u, 0, 0) : (objectId, 16, 16);
    }

    private sealed class StubHost : IPluginHost
    {
        public bool HasUi => false;
        public IPluginLogger Log { get; } = new StubLogger();
        public IGameState State { get; } = new StubState();
        public IEvents Events { get; } = new StubEvents();
        public ISelectionService Selection { get; } = new StubSelection();
        public IUiRegistry Ui => NoOpUiRegistry.Instance;
        public IAutomationSurface Automation => NoOpAutomationSurface.Instance;
    }

    private sealed class StubLogger : IPluginLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class StubState : IGameState
    {
        public IReadOnlyList<WorldEntitySnapshot> Entities => [];
    }

    private sealed class StubEvents : IEvents
    {
        public event Action<WorldEntitySnapshot> EntitySpawned
        {
            add { }
            remove { }
        }
        public event Action<double> Tick
        {
            add { }
            remove { }
        }
    }

    private sealed class StubSelection : ISelectionService
    {
        public uint? SelectedObjectId => null;
        public uint? PreviousObjectId => null;
        public event Action<SelectionChangedEvent> Changed
        {
            add { }
            remove { }
        }
        public bool Select(uint objectId) => false;
        public bool Clear() => false;
    }
}
