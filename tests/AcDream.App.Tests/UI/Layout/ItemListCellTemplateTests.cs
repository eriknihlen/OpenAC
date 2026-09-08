using System;
using System.IO;
using AcDream.App.UI.Layout;
using DatReaderWriter;
using DatReaderWriter.Options;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Lane", "InstalledDat")]
public class ItemListCellTemplateTests
{
    private const uint Generic = 0x060074CFu;   // generic toolbar-shortcut empty square (the bug)

    private const uint ContentsLayout = 0x21000021u; private const uint ContentsGrid = 0x100001C6u;
    private const uint BackpackLayout = 0x21000022u; private const uint SideBag = 0x100001CAu;
    private const uint MainPack = 0x100001C9u;

    private readonly ITestOutputHelper _out;
    public ItemListCellTemplateTests(ITestOutputHelper o) => _out = o;

    private static string? DatDir()
    {
        var d = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            "Documents", "Asheron's Call");
        return Directory.Exists(d) ? d : null;
    }

    [Fact]
    public void Inventory_lists_resolve_a_real_nongeneric_empty_sprite()
    {
        var datDir = DatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");   // CI: no live dat — skip (smoke test)

        using var dats = new DatCollection(datDir, DatAccessType.Read);

        foreach (var (name, layout, elem) in new[]
        {
            ("contents",  ContentsLayout, ContentsGrid),
            ("side-bag",  BackpackLayout, SideBag),
            ("main-pack", BackpackLayout, MainPack),
        })
        {
            uint sprite = ItemListCellTemplate.ResolveEmptySprite(dats, layout, elem);
            _out.WriteLine($"{name}: list 0x{elem:X8} (layout 0x{layout:X8}) -> empty sprite 0x{sprite:X8}");
            Assert.True(sprite != 0u, $"{name}: resolved 0 (no 0x1000000e attr / prototype / media)");
            Assert.True(sprite != Generic, $"{name}: resolved the generic 0x060074CF — the bug, not the fix");
            Assert.True((sprite & 0xFF000000u) == 0x06000000u, $"{name}: 0x{sprite:X8} is not a 0x06 RenderSurface id");
        }
    }

    [Fact]
    public void Inventory_lists_resolve_the_pinned_retail_sprites()
    {
        var datDir = DatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");   // CI: no live dat — skip

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        Assert.Equal(0x06004D20u, ItemListCellTemplate.ResolveEmptySprite(dats, ContentsLayout, ContentsGrid));
        Assert.Equal(0x06000F6Eu, ItemListCellTemplate.ResolveEmptySprite(dats, BackpackLayout, SideBag));
        Assert.Equal(0x06000F6Eu, ItemListCellTemplate.ResolveEmptySprite(dats, BackpackLayout, MainPack));
    }

    [Fact]
    public void Spell_favorite_list_resolves_its_cross_layout_pinned_empty_sprite()
    {
        var datDir = DatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");   // CI: no live dat — skip

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        ElementInfo infos = Assert.IsType<ElementInfo>(
            LayoutImporter.ImportInfos(dats, CombatUiController.LayoutId));

        uint sprite = ItemListCellTemplate.ResolveEmptySprite(
            dats, infos, SpellcastingUiController.FavoriteListId);
        _out.WriteLine($"spell favorites -> empty sprite 0x{sprite:X8}");

        Assert.Equal(0x06001A97u, sprite);
    }

    [Fact]
    public void Shared_UIItem_resolves_all_ten_cooldown_overlays()
    {
        var datDir = DatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        ItemCooldownAssets? assets = ItemCooldownAssets.TryLoad(dats);

        Assert.NotNull(assets);
        Assert.Equal(ItemCooldownAssets.OverlayCount, assets.Value.Sprites.Count);
        Assert.Equal(
            new uint[]
            {
                0x060067CFu, 0x060067D0u, 0x060067D1u, 0x060067D2u, 0x060067D3u,
                0x060067D4u, 0x060067D5u, 0x060067D6u, 0x060067D7u, 0x060067D8u,
            },
            assets.Value.Sprites);
        _out.WriteLine(
            string.Join(", ", assets.Value.Sprites.Select(id => $"0x{id:X8}")));
    }

}
