using AcDream.App.UI;
using Xunit;

namespace AcDream.App.Tests.UI;

/// <summary>Images a plugin ships in its folder, named from markup by path.</summary>
public sealed class MarkupFileIconTests
{
    private sealed class FakeFileResolver : IMarkupIconResolver
    {
        public readonly List<string> Files = [];

        public (uint tex, int w, int h) ResolveDid(uint did) => (5u, 32, 32);

        public (uint tex, int w, int h) ResolveSpell(uint spellId) => (6u, 32, 32);

        public (uint tex, int w, int h) ResolveItem(uint objectId) => (7u, 32, 32);

        public (uint tex, int w, int h) ResolveFile(string path)
        {
            Files.Add(path);
            return (40u + (uint)Files.Count, 24, 16);
        }
    }

    private sealed class FileBinding
    {
        public string? IconPath { get; set; } = "icons/bound.png";
        public uint NotAPath { get; set; } = 3u;
        public List<string> Rows { get; } = ["one", "two", "three"];
        public List<string?> RowIcons { get; set; } = ["icons/a.png", null, "icons/a.png"];
        public List<uint> RowIds { get; } = [1u, 2u, 3u];
        public Action<int> ClickIcon => _ => { };
        public int Selected { get; set; } = -1;
    }

    private static (uint, int, int) Sprite(uint id) => (1u, 32, 32);

    private static T Only<T>(string body, object binding, IMarkupIconResolver? icons) where T : UiElement =>
        Assert.IsType<T>(MarkupDocument.Build(
            $"<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">{body}</panel>", binding, Sprite, icons: icons).Children[0]);

    [Fact]
    public void Icon_FileLiteral_ResolvesThePathThroughTheResolver()
    {
        var resolver = new FakeFileResolver();

        var icon = Only<UiMarkupIcon>("<icon x=\"0\" y=\"0\" file=\"icons/sword.png\"/>", new object(), resolver);

        Assert.Equal((41u, 24, 16), icon.IconSource());
        Assert.Equal(["icons/sword.png"], resolver.Files);
    }

    [Fact]
    public void Icon_FileBinding_ReadsTheStringPropertyEachDraw()
    {
        var resolver = new FakeFileResolver();
        var binding = new FileBinding();
        var icon = Only<UiMarkupIcon>("<icon x=\"0\" y=\"0\" file=\"{IconPath}\"/>", binding, resolver);

        icon.IconSource();
        binding.IconPath = "icons/other.jpg";
        icon.IconSource();

        Assert.Equal(["icons/bound.png", "icons/other.jpg"], resolver.Files);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Icon_FileBindingWithNoPath_DrawsNothingWithoutAsking(string? path)
    {
        var resolver = new FakeFileResolver();
        var binding = new FileBinding { IconPath = path };
        var icon = Only<UiMarkupIcon>("<icon x=\"0\" y=\"0\" file=\"{IconPath}\"/>", binding, resolver);

        Assert.Equal((0u, 0, 0), icon.IconSource());
        Assert.Empty(resolver.Files);
    }

    [Fact]
    public void Icon_FileWithAnotherSource_ThrowsNamingFile()
    {
        var ex = Assert.Throws<FormatException>(() => Only<UiMarkupIcon>(
            "<icon x=\"0\" y=\"0\" did=\"7735\" file=\"icons/a.png\"/>", new object(), null));

        Assert.Contains("did/spell/item/file", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{Typo}")]
    [InlineData("{NotAPath}")]
    public void Icon_FileBindingToAMissingOrNonStringProperty_ThrowsAtBuild(string expression)
    {
        Assert.Throws<FormatException>(() => Only<UiMarkupIcon>(
            $"<icon x=\"0\" y=\"0\" file=\"{expression}\"/>", new FileBinding(), null));
    }

    [Fact]
    public void Icon_FileWithNoResolverWired_DrawsNothing()
    {
        var icon = Only<UiMarkupIcon>("<icon x=\"0\" y=\"0\" file=\"icons/a.png\"/>", new object(), null);

        Assert.Equal((0u, 0, 0), icon.IconSource());
    }

    [Fact]
    public void Icon_FileOnAResolverWithoutFileSupport_DrawsNothing()
    {
        var icon = Only<UiMarkupIcon>(
            "<icon x=\"0\" y=\"0\" file=\"icons/a.png\"/>", new object(), new DidOnlyResolver());

        Assert.Equal((0u, 0, 0), icon.IconSource());
    }

    [Fact]
    public void ButtonIcon_KindFile_ResolvesTheIconAsAPath()
    {
        var resolver = new FakeFileResolver();

        var button = Only<UiSimpleButton>(
            "<button x=\"0\" y=\"0\" w=\"60\" h=\"20\" text=\"Go\" icon=\"{IconPath}\" iconkind=\"file\"/>",
            new FileBinding(), resolver);

        Assert.Equal(41u, button.IconSource!().tex);
        Assert.Equal(["icons/bound.png"], resolver.Files);
    }

    [Fact]
    public void TabIcon_KindFile_ResolvesTheIconAsAPath()
    {
        var resolver = new FakeFileResolver();

        var tab = Only<UiMarkupTabButton>(
            "<tab x=\"0\" y=\"0\" w=\"60\" h=\"20\" text=\"Notes\" selected=\"{Selected}\" "
            + "icon=\"icons/notes.png\" iconkind=\"file\"/>",
            new TabBinding(), resolver);

        Assert.Equal(41u, tab.IconSource!().tex);
        Assert.Equal(["icons/notes.png"], resolver.Files);
    }

    private sealed class TabBinding
    {
        public bool Selected { get; set; }
    }

    [Fact]
    public void List_KindFile_GivesEachDistinctPathOneIdAndResolvesItBack()
    {
        var resolver = new FakeFileResolver();
        var list = Only<UiMarkupList>(
            "<list x=\"0\" y=\"0\" w=\"100\" h=\"60\" selected=\"{Selected}\" items=\"{Rows}\" icons=\"{RowIcons}\" iconkind=\"file\"/>",
            new FileBinding(), resolver);

        IReadOnlyList<uint> ids = list.IconIdsSource!();

        Assert.Equal(3, ids.Count);
        Assert.NotEqual(0u, ids[0]);
        Assert.Equal(0u, ids[1]);
        Assert.Equal(ids[0], ids[2]);
        list.IconResolve!(ids[0]);
        Assert.Equal(["icons/a.png"], resolver.Files);
    }

    [Fact]
    public void List_KindFile_NeedsAStringList()
    {
        var ex = Assert.Throws<FormatException>(() => Only<UiMarkupList>(
            "<list x=\"0\" y=\"0\" w=\"100\" h=\"60\" selected=\"{Selected}\" items=\"{Rows}\" icons=\"{RowIds}\" iconkind=\"file\"/>",
            new FileBinding(), new FakeFileResolver()));

        Assert.Contains("IEnumerable<string>", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Column_KindFile_ResolvesEachRowsPath()
    {
        var resolver = new FakeFileResolver();
        var list = Only<UiMarkupList>(
            "<list x=\"0\" y=\"0\" w=\"100\" h=\"60\" selected=\"{Selected}\">" +
            "<column type=\"icon\" width=\"20\" iconkind=\"file\" values=\"{RowIcons}\" onclick=\"{ClickIcon}\"/>" +
            "</list>",
            new FileBinding(), resolver);
        UiMarkupListColumn column = list.Columns![0];

        IReadOnlyList<uint> ids = column.IconValuesSource!();
        column.IconResolve!(ids[2]);

        Assert.Equal(0u, ids[1]);
        Assert.Equal(["icons/a.png"], resolver.Files);
    }

    [Fact]
    public void IconKind_Unknown_NamesFileAmongTheChoices()
    {
        var ex = Assert.Throws<FormatException>(() => Only<UiSimpleButton>(
            "<button x=\"0\" y=\"0\" w=\"60\" h=\"20\" text=\"Go\" icon=\"1\" iconkind=\"png\"/>",
            new object(), null));

        Assert.Contains("did, spell, item, or file", ex.Message, StringComparison.Ordinal);
    }

    private sealed class DidOnlyResolver : IMarkupIconResolver
    {
        public (uint tex, int w, int h) ResolveDid(uint did) => (5u, 32, 32);

        public (uint tex, int w, int h) ResolveSpell(uint spellId) => (6u, 32, 32);

        public (uint tex, int w, int h) ResolveItem(uint objectId) => (7u, 32, 32);
    }
}
