using AcDream.App.UI.Layout;
using DatReaderWriter;
using DatReaderWriter.Options;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Lane", "InstalledDat")]
public sealed class SpellbookRowStyleTests
{
    [Fact]
    public void InstalledDat_ResolvesPinnedRetailSpellShortcutPrototype()
    {
        string datDir = System.Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                "Documents", "Asheron's Call");
        if (!Directory.Exists(datDir)) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        SpellbookRowStyle style = Assert.IsType<SpellbookRowStyle>(
            SpellbookRowStyle.TryLoad(dats));

        Assert.Equal(280f, style.Width);
        Assert.Equal(32f, style.Height);
        Assert.Equal(0f, style.IconLeft);
        Assert.Equal(32f, style.IconWidth);
        Assert.Equal(42f, style.LabelLeft);
        Assert.Equal(230f, style.LabelWidth);
        Assert.Equal(0x40000001u, style.FontDid);
        Assert.Equal(System.Numerics.Vector4.One, style.LabelColor);
        Assert.Equal(0x06001396u, style.BackgroundSprite);
        Assert.Equal(0x06001397u, style.SelectedSprite);
    }

    [Fact]
    public void InstalledDat_ResolvesAuthoredSpellbookTabCaptions()
    {
        string datDir = System.Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                "Documents", "Asheron's Call");
        if (!Directory.Exists(datDir)) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        ElementInfo root = Assert.IsType<ElementInfo>(LayoutImporter.ImportInfos(
            dats, SpellbookWindowController.LayoutId, SpellbookWindowController.RootId));
        var strings = new DatStringResolver(dats);

        Assert.Equal("Spellbook", ResolveCaption(SpellbookWindowController.SpellTabId));
        Assert.Equal("Components", ResolveCaption(SpellbookWindowController.ComponentTabId));

        string? ResolveCaption(uint id)
        {
            ElementInfo node = Assert.IsType<ElementInfo>(Find(root, id));
            Assert.True(node.TryGetEffectiveProperty(0x17u, out UiPropertyValue label));
            return strings.Resolve(label.StringInfoValue);
        }
    }

    private static ElementInfo? Find(ElementInfo root, uint id)
    {
        if (root.Id == id) return root;
        foreach (ElementInfo child in root.Children)
            if (Find(child, id) is { } found) return found;
        return null;
    }
}
