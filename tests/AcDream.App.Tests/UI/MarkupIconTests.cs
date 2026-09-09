using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using AcDream.Plugin.Abstractions;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.App.Tests.UI;

public sealed class MarkupIconTests
{
    private sealed class FakeIconResolver : IMarkupIconResolver
    {
        public readonly List<(string Method, uint Id)> Calls = new();
        public uint DidTexture = 5u;
        public uint SpellTexture = 6u;
        public uint ItemTexture = 7u;

        public (uint tex, int w, int h) ResolveDid(uint did)
        {
            Calls.Add(("did", did));
            return did == 0u ? (0u, 0, 0) : (DidTexture, 32, 32);
        }

        public (uint tex, int w, int h) ResolveSpell(uint spellId)
        {
            Calls.Add(("spell", spellId));
            return spellId == 0u ? (0u, 0, 0) : (SpellTexture, 32, 32);
        }

        public (uint tex, int w, int h) ResolveItem(uint objectId)
        {
            Calls.Add(("item", objectId));
            return objectId == 0u ? (0u, 0, 0) : (ItemTexture, 32, 32);
        }
    }

    private sealed class IconBinding
    {
        public uint IconDid { get; set; } = 0x06001234u;
        public uint SpellId { get; set; } = 42u;
        public uint ObjectId { get; set; } = 0x50000001u;
        public Action Go => () => { };
    }

    private static (uint, int, int) Sprite(uint id) => (1u, 32, 32);


    [Fact]
    public void Icon_LiteralBareIndex_NormalizesBeforeResolving()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">" +
            "<icon x=\"0\" y=\"0\" did=\"7735\"/>" +
            "</panel>";
        var resolver = new FakeIconResolver();

        var panel = MarkupDocument.Build(xml, new object(), Sprite, icons: resolver);
        var icon = Assert.IsType<UiMarkupIcon>(panel.Children[0]);
        (uint tex, int w, int h) = icon.IconSource();

        Assert.Equal(resolver.DidTexture, tex);
        Assert.Equal(32, w);
        Assert.Equal(32, h);
        Assert.Equal(("did", 0x06001E37u), resolver.Calls[^1]);
    }

    [Fact]
    public void Icon_LiteralHexDid_AlreadyInRangeIsUnchangedByNormalize()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">" +
            "<icon x=\"0\" y=\"0\" did=\"0x06002D14\"/>" +
            "</panel>";
        var resolver = new FakeIconResolver();

        var panel = MarkupDocument.Build(xml, new object(), Sprite, icons: resolver);
        var icon = Assert.IsType<UiMarkupIcon>(panel.Children[0]);
        icon.IconSource();

        Assert.Equal(("did", 0x06002D14u), resolver.Calls[^1]);
    }

    [Fact]
    public void Icon_BoundDid_ReReadsTheBindingEveryFrame()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">" +
            "<icon x=\"0\" y=\"0\" did=\"{IconDid}\"/>" +
            "</panel>";
        var resolver = new FakeIconResolver();
        var binding = new IconBinding();

        var panel = MarkupDocument.Build(xml, binding, Sprite, icons: resolver);
        var icon = Assert.IsType<UiMarkupIcon>(panel.Children[0]);

        icon.IconSource();
        Assert.Equal(("did", binding.IconDid), resolver.Calls[^1]);

        binding.IconDid = 0x06005678u;
        icon.IconSource();
        Assert.Equal(("did", 0x06005678u), resolver.Calls[^1]);
    }

    [Fact]
    public void Icon_Spell_RoutesToResolveSpellWithTheBoundIdUnnormalized()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">" +
            "<icon x=\"0\" y=\"0\" spell=\"{SpellId}\"/>" +
            "</panel>";
        var resolver = new FakeIconResolver();
        var binding = new IconBinding();

        var panel = MarkupDocument.Build(xml, binding, Sprite, icons: resolver);
        var icon = Assert.IsType<UiMarkupIcon>(panel.Children[0]);
        (uint tex, _, _) = icon.IconSource();

        Assert.Equal(resolver.SpellTexture, tex);
        Assert.Equal(("spell", binding.SpellId), resolver.Calls[^1]);
    }

    [Fact]
    public void Icon_Item_RoutesToResolveItemWithTheBoundId()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">" +
            "<icon x=\"0\" y=\"0\" item=\"{ObjectId}\"/>" +
            "</panel>";
        var resolver = new FakeIconResolver();
        var binding = new IconBinding();

        var panel = MarkupDocument.Build(xml, binding, Sprite, icons: resolver);
        var icon = Assert.IsType<UiMarkupIcon>(panel.Children[0]);
        (uint tex, _, _) = icon.IconSource();

        Assert.Equal(resolver.ItemTexture, tex);
        Assert.Equal(("item", binding.ObjectId), resolver.Calls[^1]);
    }

    [Fact]
    public void Icon_ZeroId_ResolvesToNothing()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">" +
            "<icon x=\"0\" y=\"0\" did=\"0\"/>" +
            "</panel>";
        var resolver = new FakeIconResolver();

        var panel = MarkupDocument.Build(xml, new object(), Sprite, icons: resolver);
        var icon = Assert.IsType<UiMarkupIcon>(panel.Children[0]);
        (uint tex, int w, int h) = icon.IconSource();

        Assert.Equal(0u, tex);
    }

    [Fact]
    public void Icon_NoResolverWired_ResolvesToNothingRatherThanThrowing()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">" +
            "<icon x=\"0\" y=\"0\" did=\"7735\"/>" +
            "</panel>";

        var panel = MarkupDocument.Build(xml, new object(), Sprite);
        var icon = Assert.IsType<UiMarkupIcon>(panel.Children[0]);
        (uint tex, int w, int h) = icon.IconSource();

        Assert.Equal(0u, tex);
    }

    [Fact]
    public void Icon_TwoSources_ThrowsFormatExceptionAtBuild()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">" +
            "<icon x=\"0\" y=\"0\" did=\"7735\" spell=\"{SpellId}\"/>" +
            "</panel>";

        Assert.Throws<FormatException>(
            () => MarkupDocument.Build(xml, new IconBinding(), Sprite, icons: new FakeIconResolver()));
    }

    [Fact]
    public void Icon_NoSources_ThrowsFormatExceptionAtBuild()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">" +
            "<icon x=\"0\" y=\"0\"/>" +
            "</panel>";

        Assert.Throws<FormatException>(
            () => MarkupDocument.Build(xml, new object(), Sprite));
    }

    [Fact]
    public void Icon_IconKindAttribute_ThrowsAtBuild()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">" +
            "<icon x=\"0\" y=\"0\" did=\"1\" iconkind=\"did\"/>" +
            "</panel>";

        Assert.Throws<FormatException>(
            () => MarkupDocument.Build(xml, new object(), Sprite));
    }

    [Fact]
    public void Icon_WidthHeightDefaultTo32WhenOmitted()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">" +
            "<icon x=\"0\" y=\"0\" did=\"1\"/>" +
            "</panel>";

        var panel = MarkupDocument.Build(xml, new object(), Sprite);
        var icon = Assert.IsType<UiMarkupIcon>(panel.Children[0]);

        Assert.Equal(32f, icon.Width);
        Assert.Equal(32f, icon.Height);
    }

    // ── <button icon>: resolver dispatch + draw-level "text shifts right" ────

    [Fact]
    public void ButtonIcon_DefaultsToDidKind_AndDrawsThroughTheResolver()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">" +
            "<button x=\"0\" y=\"0\" w=\"60\" h=\"20\" text=\"Go\" icon=\"7735\"/>" +
            "</panel>";
        var resolver = new FakeIconResolver();

        var panel = MarkupDocument.Build(xml, new IconBinding(), Sprite, icons: resolver);
        var button = Assert.IsType<UiSimpleButton>(panel.Children[0]);

        Assert.NotNull(button.IconSource);
        (uint tex, _, _) = button.IconSource!();
        Assert.Equal(resolver.DidTexture, tex);
        Assert.Equal(("did", 0x06001E37u), resolver.Calls[^1]);
    }

    [Fact]
    public void ButtonIcon_KindSpell_RoutesToResolveSpell()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">" +
            "<button x=\"0\" y=\"0\" w=\"60\" h=\"20\" text=\"Buff\" " +
            "icon=\"{SpellId}\" iconkind=\"spell\"/>" +
            "</panel>";
        var resolver = new FakeIconResolver();
        var binding = new IconBinding();

        var panel = MarkupDocument.Build(xml, binding, Sprite, icons: resolver);
        var button = Assert.IsType<UiSimpleButton>(panel.Children[0]);
        button.IconSource!();

        Assert.Equal(("spell", binding.SpellId), resolver.Calls[^1]);
    }

    [Fact]
    public void ButtonWithoutIconAttribute_HasNoIconSource()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">" +
            "<button x=\"0\" y=\"0\" w=\"60\" h=\"20\" text=\"Go\"/>" +
            "</panel>";

        var panel = MarkupDocument.Build(xml, new object(), Sprite);
        var button = Assert.IsType<UiSimpleButton>(panel.Children[0]);

        Assert.Null(button.IconSource);
    }

    [Fact]
    public void ButtonIcon_UnknownIconKind_ThrowsAtBuild_EvenWithNoResolverWired()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">" +
            "<button x=\"0\" y=\"0\" w=\"60\" h=\"20\" text=\"Go\" " +
            "icon=\"1\" iconkind=\"spel\"/>" +
            "</panel>";

        Assert.Throws<FormatException>(
            () => MarkupDocument.Build(xml, new object(), Sprite));
    }

    [Fact]
    public void ButtonIcon_MalformedBinding_ThrowsAtBuild_EvenWithNoResolverWired()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">" +
            "<button x=\"0\" y=\"0\" w=\"60\" h=\"20\" text=\"Go\" icon=\"{Typo}\"/>" +
            "</panel>";

        Assert.Throws<FormatException>(
            () => MarkupDocument.Build(xml, new object(), Sprite));
    }

    [Fact]
    public void ButtonIcon_NoResolverWired_IconSourceStaysNull()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">" +
            "<button x=\"0\" y=\"0\" w=\"60\" h=\"20\" text=\"Go\" icon=\"1\"/>" +
            "</panel>";

        var panel = MarkupDocument.Build(xml, new object(), Sprite);
        var button = Assert.IsType<UiSimpleButton>(panel.Children[0]);

        Assert.Null(button.IconSource);
    }

    private sealed class IntIconBinding
    {
        public int IconIdInt { get; set; } = 42;
    }

    [Fact]
    public void ButtonIcon_BindsToAnIntProperty_NotOnlyUint()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">" +
            "<button x=\"0\" y=\"0\" w=\"60\" h=\"20\" text=\"Go\" icon=\"{IconIdInt}\"/>" +
            "</panel>";
        var resolver = new FakeIconResolver();
        var binding = new IntIconBinding();

        var panel = MarkupDocument.Build(xml, binding, Sprite, icons: resolver);
        var button = Assert.IsType<UiSimpleButton>(panel.Children[0]);

        button.IconSource!();
        Assert.Equal(("did", PluginIcons.Normalize(42u)), resolver.Calls[^1]);
    }

    private sealed class NegativeIntIconBinding
    {
        public int IconIdInt { get; set; } = -1;
    }

    [Fact]
    public void ButtonIcon_NegativeIntProperty_MapsToZero_NotOverflowException()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">" +
            "<button x=\"0\" y=\"0\" w=\"60\" h=\"20\" text=\"Go\" icon=\"{IconIdInt}\"/>" +
            "</panel>";
        var resolver = new FakeIconResolver();
        var binding = new NegativeIntIconBinding();

        var panel = MarkupDocument.Build(xml, binding, Sprite, icons: resolver);
        var button = Assert.IsType<UiSimpleButton>(panel.Children[0]);

        (uint tex, _, _) = button.IconSource!();
        Assert.Equal(0u, tex);
        Assert.Equal(("did", 0u), resolver.Calls[^1]);
    }


    private sealed class ListIconBinding
    {
        public IReadOnlyList<string> Items { get; } = new[] { "First", "Second" };
        // Row 0 resolves; row 1's id is 0 (unresolvable) — "missing entries
        // draw no icon" per the plan.
        public IReadOnlyList<uint> IconIds { get; } = new[] { 7735u, 0u };
        public int Selected { get; set; } = -1;
    }

    [Fact]
    public void ListIcons_ReservesTheColumn_AndResolvesEachRowThroughTheSharedResolver()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" rowheight=\"18\" " +
            "items=\"{Items}\" icons=\"{IconIds}\" iconkind=\"did\" selected=\"{Selected}\"/>" +
            "</panel>";
        var resolver = new FakeIconResolver();
        var binding = new ListIconBinding();

        var panel = MarkupDocument.Build(xml, binding, Sprite, icons: resolver);
        var list = Assert.IsType<UiMarkupList>(panel.Children[0]);

        Assert.NotNull(list.IconIdsSource);
        Assert.Equal(binding.IconIds, list.IconIdsSource!());
        Assert.NotNull(list.IconResolve);

        (uint tex, _, _) = list.IconResolve!(7735u);
        Assert.Equal(resolver.DidTexture, tex);
        Assert.Equal(("did", 0x06001E37u), resolver.Calls[^1]);

        (uint missTex, _, _) = list.IconResolve!(0u);
        Assert.Equal(0u, missTex);
    }

    [Fact]
    public void ListWithoutIconsAttribute_HasNoIconColumn()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" items=\"{Items}\" selected=\"{Selected}\"/>" +
            "</panel>";

        var panel = MarkupDocument.Build(xml, new ListIconBinding(), Sprite);
        var list = Assert.IsType<UiMarkupList>(panel.Children[0]);

        Assert.Null(list.IconIdsSource);
    }

    [Fact]
    public void ListIcons_UnknownIconKind_ThrowsAtBuild_EvenWithNoResolverWired()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" items=\"{Items}\" " +
            "icons=\"{IconIds}\" iconkind=\"spel\" selected=\"{Selected}\"/>" +
            "</panel>";

        Assert.Throws<FormatException>(
            () => MarkupDocument.Build(xml, new ListIconBinding(), Sprite));
    }

    [Fact]
    public void ListIcons_NoResolverWired_IconIdsSourceStaysNull()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" items=\"{Items}\" " +
            "icons=\"{IconIds}\" selected=\"{Selected}\"/>" +
            "</panel>";

        var panel = MarkupDocument.Build(xml, new ListIconBinding(), Sprite);
        var list = Assert.IsType<UiMarkupList>(panel.Children[0]);

        Assert.Null(list.IconIdsSource);
        Assert.Null(list.IconResolve);
    }

    [Fact]
    public void ListIcons_MalformedBinding_ThrowsAtBuild_EvenWithNoResolverWired()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" items=\"{Items}\" " +
            "icons=\"notabinding\" selected=\"{Selected}\"/>" +
            "</panel>";

        Assert.Throws<FormatException>(
            () => MarkupDocument.Build(xml, new ListIconBinding(), Sprite));
    }

    private sealed class IntListIconBinding
    {
        public IReadOnlyList<string> Items { get; } = new[] { "First", "Second" };
        public IEnumerable<int> IconIds { get; } = new[] { 7735, 0 };
        public int Selected { get; set; } = -1;
    }

    private sealed class NegativeIntListIconBinding
    {
        public IReadOnlyList<string> Items { get; } = new[] { "First" };
        public IEnumerable<int> IconIds { get; } = new[] { -1 };
        public int Selected { get; set; } = -1;
    }

    [Fact]
    public void ListIcons_NegativeIntElement_MapsToZero_NotWraparound()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" items=\"{Items}\" " +
            "icons=\"{IconIds}\" iconkind=\"did\" selected=\"{Selected}\"/>" +
            "</panel>";
        var resolver = new FakeIconResolver();
        var binding = new NegativeIntListIconBinding();

        var panel = MarkupDocument.Build(xml, binding, Sprite, icons: resolver);
        var list = Assert.IsType<UiMarkupList>(panel.Children[0]);

        Assert.NotNull(list.IconIdsSource);
        Assert.Equal(new uint[] { 0u }, list.IconIdsSource!());
    }

    [Fact]
    public void ListIcons_BindsToAnIEnumerableOfInt_NotOnlyIEnumerableOfUint()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" items=\"{Items}\" " +
            "icons=\"{IconIds}\" iconkind=\"did\" selected=\"{Selected}\"/>" +
            "</panel>";
        var resolver = new FakeIconResolver();
        var binding = new IntListIconBinding();

        var panel = MarkupDocument.Build(xml, binding, Sprite, icons: resolver);
        var list = Assert.IsType<UiMarkupList>(panel.Children[0]);

        Assert.NotNull(list.IconIdsSource);
        Assert.Equal(new uint[] { 7735u, 0u }, list.IconIdsSource!());
    }


    [Fact]
    public void Icon_EmptyTooltip_StaysClickThrough()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">" +
            "<icon x=\"0\" y=\"0\" did=\"1\" tooltip=\"\"/>" +
            "</panel>";

        var panel = MarkupDocument.Build(xml, new object(), Sprite);
        var icon = Assert.IsType<UiMarkupIcon>(panel.Children[0]);

        Assert.True(icon.ClickThrough);
    }

    [Fact]
    public void Icon_NonEmptyTooltip_BecomesARealHitTestTarget()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">" +
            "<icon x=\"0\" y=\"0\" did=\"1\" tooltip=\"real tooltip\"/>" +
            "</panel>";

        var panel = MarkupDocument.Build(xml, new object(), Sprite);
        var icon = Assert.IsType<UiMarkupIcon>(panel.Children[0]);

        Assert.False(icon.ClickThrough);
    }

    // ── Unknown element names throw at Build (finding 11) ─────────────────────

    [Fact]
    public void UnknownElementName_ThrowsFormatExceptionAtBuild()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">" +
            "<butotn x=\"0\" y=\"0\" w=\"60\" h=\"20\" text=\"Go\"/>" +
            "</panel>";

        Assert.Throws<FormatException>(
            () => MarkupDocument.Build(xml, new object(), Sprite));
    }

    // ── Draw-level: "draws nothing" / "shifts text" pinned against real quads ─

    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }

    private static (TextRenderer renderer, UiRenderContext ctx) MakeContext(float w, float h)
    {
        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(w, h));
        var ctx = new UiRenderContext(renderer, new Vector2(w, h));
        return (renderer, ctx);
    }

    [Fact]
    public void UiMarkupIcon_ResolvedTexture_DrawsASpriteAspectPreservedAndCentered()
    {
        var icon = new UiMarkupIcon
        {
            Width = 32f,
            Height = 32f,
            IconSource = () => (9u, 32, 16),
        };
        var (renderer, ctx) = MakeContext(200f, 200f);

        icon.DrawSelfAndChildren(ctx);

        var seg = Assert.Single(renderer.DebugSpriteSegmentVerts, s => s.Texture == 9u);
        // First vertex = top-left (x,y); AppendQuad's V0..V5 layout, 8 floats/vertex.
        Assert.Equal(0f, seg.Verts[0], 3);
        Assert.Equal(8f, seg.Verts[1], 3);
    }

    [Fact]
    public void UiMarkupIcon_UnresolvedTexture_DrawsNothing()
    {
        var icon = new UiMarkupIcon
        {
            Width = 32f,
            Height = 32f,
            IconSource = () => (0u, 0, 0),
        };
        var (renderer, ctx) = MakeContext(200f, 200f);

        icon.DrawSelfAndChildren(ctx);

        Assert.Empty(renderer.DebugSpriteSegmentVerts);
    }

    [Fact]
    public void UiSimpleButton_WithIcon_DrawsTheIconSprite_AndShiftsTheCaptionRight()
    {
        var glyphs = new Dictionary<char, FontCharDesc>
        {
            ['G'] = new FontCharDesc { Unicode = 'G', Width = 8, Height = 8 },
        };
        var font = new UiDatFont(
            fgTex: 1u, fgW: 32, fgH: 32,
            bgTex: 0, bgW: 0, bgH: 0,
            lineHeight: 16f, baselineOffset: 12f,
            glyphs);

        var withoutIcon = new UiSimpleButton
        {
            Width = 60f, Height = 20f, Text = "G", DatFont = font, Outline = false,
            BackgroundColor = default, BorderColor = default,
        };
        var withIcon = new UiSimpleButton
        {
            Width = 60f, Height = 20f, Text = "G", DatFont = font, Outline = false,
            IconSource = () => (9u, 32, 32),
            BackgroundColor = default, BorderColor = default,
        };

        var (rendererWithout, ctxWithout) = MakeContext(200f, 200f);
        withoutIcon.DrawSelfAndChildren(ctxWithout);
        var (rendererWith, ctxWith) = MakeContext(200f, 200f);
        withIcon.DrawSelfAndChildren(ctxWith);

        // The icon sprite itself drew.
        Assert.Contains(rendererWith.DebugSpriteSegmentVerts, s => s.Texture == 9u);
        Assert.DoesNotContain(rendererWithout.DebugSpriteSegmentVerts, s => s.Texture == 9u);

        var textWithout = Assert.Single(rendererWithout.DebugSpriteSegmentVerts, s => s.Texture == 1u);
        var textWith = Assert.Single(rendererWith.DebugSpriteSegmentVerts, s => s.Texture == 1u);
        Assert.True(
            textWith.Verts[0] > textWithout.Verts[0],
            $"expected the icon-bearing button's caption ({textWith.Verts[0]}) to start right of the icon-less caption ({textWithout.Verts[0]})");
    }

    [Fact]
    public void UiMarkupList_IconColumn_DrawsResolvedRowIcon_AndSkipsAMissingOne()
    {
        var list = new UiMarkupList
        {
            Width = 180f, Height = 60f, RowHeight = 18f,
            ItemsSource = () => new[] { "First", "Second" },
            IconIdsSource = () => new uint[] { 9u, 0u },
            IconResolve = id => id == 0u ? (0u, 0, 0) : (id, 16, 16),
            BackgroundColor = default, BorderColor = default,
        };
        var (renderer, ctx) = MakeContext(200f, 200f);

        list.DrawSelfAndChildren(ctx);

        Assert.Contains(renderer.DebugSpriteSegmentVerts, s => s.Texture == 9u);
        var iconQuad = Assert.Single(renderer.DebugSpriteSegmentVerts, s => s.Texture == 9u);
        Assert.True(iconQuad.Verts[8] <= list.RowHeight - 2f + 0.01f);
    }

    [Fact]
    public void UiMarkupList_WithIconColumn_TextStartsRightOfWithoutColumn_AndIconHasNonZeroWidth()
    {
        var glyphs = new Dictionary<char, FontCharDesc>
        {
            ['G'] = new FontCharDesc { Unicode = 'G', Width = 8, Height = 8 },
        };
        var font = new UiDatFont(
            fgTex: 1u, fgW: 32, fgH: 32,
            bgTex: 0, bgW: 0, bgH: 0,
            lineHeight: 16f, baselineOffset: 12f,
            glyphs);

        var withoutIcons = new UiMarkupList
        {
            Width = 180f, Height = 60f, RowHeight = 18f, DatFont = font,
            ItemsSource = () => new[] { "G" },
            BackgroundColor = default, BorderColor = default,
        };
        var withIcons = new UiMarkupList
        {
            Width = 180f, Height = 60f, RowHeight = 18f, DatFont = font,
            ItemsSource = () => new[] { "G" },
            IconIdsSource = () => new uint[] { 9u },
            IconResolve = id => (id, 16, 16),
            BackgroundColor = default, BorderColor = default,
        };

        var (rendererWithout, ctxWithout) = MakeContext(200f, 200f);
        withoutIcons.DrawSelfAndChildren(ctxWithout);
        var (rendererWith, ctxWith) = MakeContext(200f, 200f);
        withIcons.DrawSelfAndChildren(ctxWith);

        // The icon sprite itself drew, with a real (non-zero) width.
        var iconQuad = Assert.Single(rendererWith.DebugSpriteSegmentVerts, s => s.Texture == 9u);
        float iconWidth = iconQuad.Verts[8] - iconQuad.Verts[0];
        Assert.True(iconWidth > 0f, $"expected a non-zero icon quad width, got {iconWidth}");

        var textWithout = Assert.Single(rendererWithout.DebugSpriteSegmentVerts, s => s.Texture == 1u);
        var textWith = Assert.Single(rendererWith.DebugSpriteSegmentVerts, s => s.Texture == 1u);
        Assert.True(
            textWith.Verts[0] > textWithout.Verts[0],
            $"expected the icon-bearing list's text ({textWith.Verts[0]}) to start right of the icon-less list's ({textWithout.Verts[0]})");
    }
}
