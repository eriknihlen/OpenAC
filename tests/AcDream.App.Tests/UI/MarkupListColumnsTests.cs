using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.App.Tests.UI;

public sealed class MarkupListColumnsTests
{
    private static (uint, int, int) Sprite(uint id) => (1u, 32, 32);


    private sealed class ThreeColumnBinding
    {
        public IReadOnlyList<string> Names { get; } = new[] { "Mosswart", "Drudge", "Rat" };
        public IReadOnlyList<bool> Fester { get; } = new[] { true, false };
        public IReadOnlyList<uint> Icons { get; } = new[] { 7735u, 0u, 42u };
        public int Selected { get; set; } = -1;
        public int FesterToggled { get; private set; } = -1;
        public int IconClickedRow { get; private set; } = -1;
        public Action<int> ToggleFester => row => FesterToggled = row;
        public Action<int> ClickIcon => row => IconClickedRow = row;
    }

    private const string ThreeColumnXml =
        "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
        "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" rowheight=\"18\" selected=\"{Selected}\">" +
        "  <column type=\"text\" width=\"80\" items=\"{Names}\"/>" +
        "  <column type=\"check\" width=\"20\" values=\"{Fester}\" onchange=\"{ToggleFester}\"/>" +
        "  <column type=\"icon\" width=\"20\" iconkind=\"did\" values=\"{Icons}\" onclick=\"{ClickIcon}\"/>" +
        "</list>" +
        "</panel>";

    private sealed class FakeIconResolver : IMarkupIconResolver
    {
        public readonly List<(string Method, uint Id)> Calls = new();
        public (uint tex, int w, int h) ResolveDid(uint did)
        {
            Calls.Add(("did", did));
            return did == 0u ? (0u, 0, 0) : (did, 16, 16);
        }
        public (uint tex, int w, int h) ResolveSpell(uint spellId)
        {
            Calls.Add(("spell", spellId));
            return spellId == 0u ? (0u, 0, 0) : (spellId, 16, 16);
        }
        public (uint tex, int w, int h) ResolveItem(uint objectId)
        {
            Calls.Add(("item", objectId));
            return objectId == 0u ? (0u, 0, 0) : (objectId, 16, 16);
        }
    }

    [Fact]
    public void Columns_TextCheckIcon_BindEachColumnsOwnPerRowSource()
    {
        var resolver = new FakeIconResolver();
        var binding = new ThreeColumnBinding();

        var panel = MarkupDocument.Build(ThreeColumnXml, binding, Sprite, icons: resolver);
        var list = Assert.IsType<UiMarkupList>(panel.Children[0]);

        Assert.NotNull(list.Columns);
        Assert.Equal(3, list.Columns!.Count);
        Assert.Equal(UiMarkupListColumnKind.Text, list.Columns[0].Kind);
        Assert.Equal(UiMarkupListColumnKind.Check, list.Columns[1].Kind);
        Assert.Equal(UiMarkupListColumnKind.Icon, list.Columns[2].Kind);

        Assert.Equal(binding.Names, list.Columns[0].TextSource!());
        Assert.Equal(binding.Fester, list.Columns[1].CheckSource!());
        Assert.Equal(binding.Icons, list.Columns[2].IconValuesSource!());

        Assert.Equal(3, list.Columns.Max(c => c.RowCount()));

        Assert.Empty(list.ItemsSource());
        Assert.Null(list.IconIdsSource);

        list.Columns[1].CheckChanged!(1);
        Assert.Equal(1, binding.FesterToggled);
        list.Columns[2].IconClicked!(0);
        Assert.Equal(0, binding.IconClickedRow);
    }

    [Fact]
    public void ColumnLessList_ColumnsPropertyStaysNull_LegacyPathUntouched()
    {
        var binding = new ThreeColumnBinding();
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" items=\"{Names}\" selected=\"{Selected}\"/>" +
            "</panel>";

        var panel = MarkupDocument.Build(xml, binding, Sprite);
        var list = Assert.IsType<UiMarkupList>(panel.Children[0]);

        Assert.Null(list.Columns);
        Assert.Equal(binding.Names, list.ItemsSource());
    }

    [Fact]
    public void Column_UnknownType_ThrowsAtBuild()
    {
        var binding = new ThreeColumnBinding();
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" selected=\"{Selected}\">" +
            "  <column type=\"bogus\" width=\"80\" items=\"{Names}\"/>" +
            "</list></panel>";

        var ex = Assert.Throws<FormatException>(
            () => MarkupDocument.Build(xml, binding, Sprite));
        Assert.Contains("bogus", ex.Message);
        Assert.Contains("column[0]", ex.Message);
    }

    [Fact]
    public void TextColumn_MissingItems_ThrowsAtBuild()
    {
        var binding = new ThreeColumnBinding();
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" selected=\"{Selected}\">" +
            "  <column type=\"text\" width=\"80\"/>" +
            "</list></panel>";

        var ex = Assert.Throws<FormatException>(() => MarkupDocument.Build(xml, binding, Sprite));
        Assert.Contains("column[0] type=\"text\" items", ex.Message);
    }

    [Fact]
    public void CheckColumn_MissingValues_ThrowsAtBuild()
    {
        var binding = new ThreeColumnBinding();
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" selected=\"{Selected}\">" +
            "  <column type=\"check\" width=\"20\" onchange=\"{ToggleFester}\"/>" +
            "</list></panel>";

        var ex = Assert.Throws<FormatException>(() => MarkupDocument.Build(xml, binding, Sprite));
        Assert.Contains("column[0] type=\"check\" values", ex.Message);
    }

    [Fact]
    public void CheckColumn_MissingOnchange_ThrowsAtBuild()
    {
        var binding = new ThreeColumnBinding();
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" selected=\"{Selected}\">" +
            "  <column type=\"check\" width=\"20\" values=\"{Fester}\"/>" +
            "</list></panel>";

        var ex = Assert.Throws<FormatException>(() => MarkupDocument.Build(xml, binding, Sprite));
        Assert.Contains("column[0] type=\"check\" onchange", ex.Message);
    }

    [Fact]
    public void IconColumn_MissingValues_ThrowsAtBuild()
    {
        var binding = new ThreeColumnBinding();
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" selected=\"{Selected}\">" +
            "  <column type=\"icon\" width=\"20\" onclick=\"{ClickIcon}\"/>" +
            "</list></panel>";

        var ex = Assert.Throws<FormatException>(() => MarkupDocument.Build(xml, binding, Sprite));
        Assert.Contains("column[0] type=\"icon\" values", ex.Message);
    }

    [Fact]
    public void IconColumn_MissingOnclick_ThrowsAtBuild()
    {
        var binding = new ThreeColumnBinding();
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" selected=\"{Selected}\">" +
            "  <column type=\"icon\" width=\"20\" values=\"{Icons}\"/>" +
            "</list></panel>";

        var ex = Assert.Throws<FormatException>(() => MarkupDocument.Build(xml, binding, Sprite));
        Assert.Contains("column[0] type=\"icon\" onclick", ex.Message);
    }

    [Fact]
    public void IconColumn_UnknownIconKind_ThrowsAtBuild_EvenWithNoResolverWired()
    {
        var binding = new ThreeColumnBinding();
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" selected=\"{Selected}\">" +
            "  <column type=\"icon\" width=\"20\" iconkind=\"spel\" values=\"{Icons}\" onclick=\"{ClickIcon}\"/>" +
            "</list></panel>";

        var ex = Assert.Throws<FormatException>(() => MarkupDocument.Build(xml, binding, Sprite));
        Assert.Contains("column[0] type=\"icon\" iconkind", ex.Message);
    }

    [Fact]
    public void IconColumn_NoResolverWired_IconResolveStaysNull_ButValuesStillBind()
    {
        var binding = new ThreeColumnBinding();
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" selected=\"{Selected}\">" +
            "  <column type=\"icon\" width=\"20\" values=\"{Icons}\" onclick=\"{ClickIcon}\"/>" +
            "</list></panel>";

        var panel = MarkupDocument.Build(xml, binding, Sprite); // no `icons:` resolver
        var list = Assert.IsType<UiMarkupList>(panel.Children[0]);

        Assert.NotNull(list.Columns);
        Assert.Null(list.Columns![0].IconResolve);
        Assert.Equal(binding.Icons, list.Columns[0].IconValuesSource!());
    }

    [Fact]
    public void Columns_CombinedWithLegacyItemsAttribute_ThrowsAtBuild()
    {
        var binding = new ThreeColumnBinding();
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" items=\"{Names}\" selected=\"{Selected}\">" +
            "  <column type=\"text\" width=\"80\" items=\"{Names}\"/>" +
            "</list></panel>";

        var ex = Assert.Throws<FormatException>(() => MarkupDocument.Build(xml, binding, Sprite));
        Assert.Contains("items/icons/colors", ex.Message);
    }

    [Fact]
    public void Columns_CombinedWithLegacyColorsAttribute_ThrowsAtBuild()
    {
        var binding = new ThreeColumnBinding();
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" colors=\"{Icons}\" selected=\"{Selected}\">" +
            "  <column type=\"text\" width=\"80\" items=\"{Names}\"/>" +
            "</list></panel>";

        var ex = Assert.Throws<FormatException>(() => MarkupDocument.Build(xml, binding, Sprite));
        Assert.Contains("items/icons/colors", ex.Message);
    }

    [Fact]
    public void Columns_CombinedWithLegacyIconsAttribute_ThrowsAtBuild()
    {
        var binding = new ThreeColumnBinding();
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" icons=\"{Icons}\" selected=\"{Selected}\">" +
            "  <column type=\"text\" width=\"80\" items=\"{Names}\"/>" +
            "</list></panel>";

        var ex = Assert.Throws<FormatException>(() => MarkupDocument.Build(xml, binding, Sprite));
        Assert.Contains("items/icons/colors", ex.Message);
    }

    [Fact]
    public void NonColumnChildOfList_ThrowsAtBuild()
    {
        var binding = new ThreeColumnBinding();
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" selected=\"{Selected}\">" +
            "  <label x=\"0\" y=\"0\" text=\"stray\"/>" +
            "</list></panel>";

        var ex = Assert.Throws<FormatException>(
            () => MarkupDocument.Build(xml, binding, Sprite));
        Assert.Contains("label", ex.Message);
    }

    [Fact]
    public void TextColumn_OptionalColorsAttribute_BindsWhenPresent_NullWhenAbsent()
    {
        var binding = new ThreeColumnBinding();
        const string withColors =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" selected=\"{Selected}\">" +
            "  <column type=\"text\" width=\"80\" items=\"{Names}\" colors=\"{Icons}\"/>" +
            "</list></panel>";
        var panelWith = MarkupDocument.Build(withColors, binding, Sprite);
        var listWith = Assert.IsType<UiMarkupList>(panelWith.Children[0]);
        Assert.NotNull(listWith.Columns![0].ColorsSource);
        Assert.Equal(binding.Icons, listWith.Columns[0].ColorsSource!());

        const string withoutColors =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" selected=\"{Selected}\">" +
            "  <column type=\"text\" width=\"80\" items=\"{Names}\"/>" +
            "</list></panel>";
        var panelWithout = MarkupDocument.Build(withoutColors, binding, Sprite);
        var listWithout = Assert.IsType<UiMarkupList>(panelWithout.Children[0]);
        Assert.Null(listWithout.Columns![0].ColorsSource);
    }

    [Fact]
    public void TextColumn_MalformedColorsAttribute_ThrowsAtBuild()
    {
        var binding = new ThreeColumnBinding();
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" selected=\"{Selected}\">" +
            "  <column type=\"text\" width=\"80\" items=\"{Names}\" colors=\"notabinding\"/>" +
            "</list></panel>";

        var ex = Assert.Throws<FormatException>(() => MarkupDocument.Build(xml, binding, Sprite));
        Assert.Contains("column[0] type=\"text\" colors", ex.Message);
    }


    [Fact]
    public void TextColumn_OptionalOnclickAttribute_BindsAndDoesNotBreakWithoutIt()
    {
        var binding = new ThreeColumnBinding();
        const string withOnclick =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" selected=\"{Selected}\">" +
            "  <column type=\"text\" width=\"80\" items=\"{Names}\" onclick=\"{ClickIcon}\"/>" +
            "</list></panel>";
        var panelWith = MarkupDocument.Build(withOnclick, binding, Sprite);
        var listWith = Assert.IsType<UiMarkupList>(panelWith.Children[0]);
        Assert.NotNull(listWith.Columns![0].TextClicked);

        const string withoutOnclick =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" selected=\"{Selected}\">" +
            "  <column type=\"text\" width=\"80\" items=\"{Names}\"/>" +
            "</list></panel>";
        var panelWithout = MarkupDocument.Build(withoutOnclick, binding, Sprite);
        var listWithout = Assert.IsType<UiMarkupList>(panelWithout.Children[0]);
        Assert.Null(listWithout.Columns![0].TextClicked);
    }

    [Fact]
    public void TextColumn_MalformedOnclickAttribute_ThrowsAtBuild()
    {
        var binding = new ThreeColumnBinding();
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" selected=\"{Selected}\">" +
            "  <column type=\"text\" width=\"80\" items=\"{Names}\" onclick=\"notabinding\"/>" +
            "</list></panel>";

        var ex = Assert.Throws<FormatException>(() => MarkupDocument.Build(xml, binding, Sprite));
        Assert.Contains("column[0] type=\"text\" onclick", ex.Message);
    }

    [Fact]
    public void ClickInTextColumn_WithOnclick_FiresItInsteadOfSelecting()
    {
        var clicked = new List<int>();
        var selections = new List<int>();
        var list = new UiMarkupList
        {
            Width = 60f, Height = 200f, RowHeight = 20f,
            SelectedIndexSource = () => -1,
            SelectionChanged = row => selections.Add(row),
            Columns = new[]
            {
                UiMarkupListColumn.Text(
                    60f, () => new[] { "a", "b", "c" }, null, row => clicked.Add(row)),
            },
        };
        var (_, ctx) = MakeContext(200f, 200f);
        list.DrawSelfAndChildren(ctx);

        // Row 1 (y = 20..40).
        list.OnEvent(new UiEvent { Type = UiEventType.MouseDown, Data1 = 10, Data2 = 25 });

        Assert.Equal(new[] { 1 }, clicked);
        Assert.Empty(selections);
    }

    // ── Width semantics ──────────────────────────────

    [Fact]
    public void NonLastColumn_MissingWidth_ThrowsAtBuild_NamingColumnIndexAndType()
    {
        var binding = new ThreeColumnBinding();
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" selected=\"{Selected}\">" +
            "  <column type=\"text\" items=\"{Names}\"/>" +
            "  <column type=\"text\" width=\"40\" items=\"{Names}\"/>" +
            "</list></panel>";

        var ex = Assert.Throws<FormatException>(() => MarkupDocument.Build(xml, binding, Sprite));
        Assert.Contains("column[0] type=\"text\" width", ex.Message);
    }

    [Fact]
    public void NonLastColumn_UnparseableWidth_ThrowsAtBuild()
    {
        var binding = new ThreeColumnBinding();
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" selected=\"{Selected}\">" +
            "  <column type=\"text\" width=\"wide\" items=\"{Names}\"/>" +
            "  <column type=\"text\" width=\"40\" items=\"{Names}\"/>" +
            "</list></panel>";

        var ex = Assert.Throws<FormatException>(() => MarkupDocument.Build(xml, binding, Sprite));
        Assert.Contains("column[0] type=\"text\" width", ex.Message);
    }

    [Fact]
    public void NonLastColumn_NonPositiveWidth_ThrowsAtBuild()
    {
        var binding = new ThreeColumnBinding();
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" selected=\"{Selected}\">" +
            "  <column type=\"check\" width=\"0\" values=\"{Fester}\" onchange=\"{ToggleFester}\"/>" +
            "  <column type=\"text\" width=\"40\" items=\"{Names}\"/>" +
            "</list></panel>";

        var ex = Assert.Throws<FormatException>(() => MarkupDocument.Build(xml, binding, Sprite));
        Assert.Contains("column[0] type=\"check\" width", ex.Message);
    }

    [Fact]
    public void NonLastColumn_WidthStar_DoesNotThrow_ParsesAsAutoWidth()
    {
        var binding = new ThreeColumnBinding();
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" selected=\"{Selected}\">" +
            "  <column type=\"text\" width=\"*\" items=\"{Names}\"/>" +
            "  <column type=\"text\" width=\"40\" items=\"{Names}\"/>" +
            "</list></panel>";

        var panel = MarkupDocument.Build(xml, binding, Sprite);
        var list = Assert.IsType<UiMarkupList>(panel.Children[0]);
        Assert.NotNull(list.Columns);
    }

    [Fact]
    public void LastColumn_MissingOrInvalidWidth_DoesNotThrow_StillAbsorbsRemainder()
    {
        var binding = new ThreeColumnBinding();
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" selected=\"{Selected}\">" +
            "  <column type=\"text\" width=\"40\" items=\"{Names}\"/>" +
            "  <column type=\"text\" items=\"{Names}\"/>" +
            "</list></panel>";

        var panel = MarkupDocument.Build(xml, binding, Sprite);
        var list = Assert.IsType<UiMarkupList>(panel.Children[0]);
        Assert.NotNull(list.Columns);
    }

    [Fact]
    public void AutoWidthColumns_ShareRemainingWidthEqually_LastAbsorbsRoundingSlack()
    {
        var list = new UiMarkupList
        {
            Width = 101f, Height = 40f, RowHeight = 18f,
            SelectedIndexSource = () => -1,
            BackgroundColor = default, BorderColor = default,
            Columns = new[]
            {
                UiMarkupListColumn.Text(10f, () => new[] { "" }, null),
                UiMarkupListColumn.Icon(
                    0f, () => new uint[] { 20u }, id => (id, 16, 16), _ => { }, isAutoWidth: true),
                UiMarkupListColumn.Icon(
                    0f, () => new uint[] { 21u }, id => (id, 16, 16), _ => { }, isAutoWidth: true),
            },
        };
        var (renderer, ctx) = MakeContext(200f, 200f);

        list.DrawSelfAndChildren(ctx);

        var col1Quad = Assert.Single(renderer.DebugSpriteSegmentVerts, s => s.Texture == 20u);
        var col2Quad = Assert.Single(renderer.DebugSpriteSegmentVerts, s => s.Texture == 21u);
        Assert.True(col1Quad.Verts[0] >= 10f - 0.01f && col1Quad.Verts[8] <= 55f + 0.01f,
            $"expected col1's icon inside [10,55), got x=[{col1Quad.Verts[0]},{col1Quad.Verts[8]}]");
        Assert.True(col2Quad.Verts[0] >= 55f - 0.01f,
            $"expected col2's icon at/after x=55 (10 + 45), got x={col2Quad.Verts[0]}");
    }

    [Fact]
    public void DeclaredWidthsExceedingListWidth_ClampTheOverflowingColumn_LaterColumnsGetZero()
    {
        var list = new UiMarkupList
        {
            Width = 50f, Height = 40f, RowHeight = 18f,
            SelectedIndexSource = () => -1,
            BackgroundColor = default, BorderColor = default,
            Columns = new[]
            {
                UiMarkupListColumn.Icon(
                    80f, () => new uint[] { 7u }, id => (id, 16, 16), _ => { }),
                UiMarkupListColumn.Icon(
                    20f, () => new uint[] { 8u }, id => (id, 16, 16), _ => { }),
                UiMarkupListColumn.Icon(
                    20f, () => new uint[] { 9u }, id => (id, 16, 16), _ => { }),
            },
        };
        var (renderer, ctx) = MakeContext(200f, 200f);

        list.DrawSelfAndChildren(ctx);

        Assert.Contains(renderer.DebugSpriteSegmentVerts, s => s.Texture == 7u);
        Assert.DoesNotContain(renderer.DebugSpriteSegmentVerts, s => s.Texture == 8u);
        Assert.DoesNotContain(renderer.DebugSpriteSegmentVerts, s => s.Texture == 9u);
        var clampedQuad = Assert.Single(renderer.DebugSpriteSegmentVerts, s => s.Texture == 7u);
        Assert.True(clampedQuad.Verts[8] - clampedQuad.Verts[0] <= 50f + 0.01f,
            "expected col0's icon clamped inside the 50px list width");
    }

    [Fact]
    public void ColumnGrids_NeverDrawARowSelectionBand()
    {
        var list = new UiMarkupList
        {
            Width = 100f, Height = 60f, RowHeight = 18f,
            SelectedIndexSource = () => 1,
            BackgroundColor = new Vector4(0f, 0f, 0f, 1f),
            BorderColor = default,
            SelectedColor = new Vector4(1f, 0f, 0f, 1f), // distinct, unmistakable
            Columns = new[]
            {
                UiMarkupListColumn.Text(100f, () => new[] { "Row0", "Row1", "Row2" }, null),
            },
        };
        var (renderer, ctx) = MakeContext(200f, 200f);

        list.DrawSelfAndChildren(ctx);

        bool anySelectedFill = renderer.DebugSpriteSegmentVerts.Any(s =>
        {
            if (s.Texture != 0u)
                return false;
            for (int q = 0; q + 48 <= s.Verts.Count; q += 48)
            {
                float r = s.Verts[q + 4], g = s.Verts[q + 5], b = s.Verts[q + 6], a = s.Verts[q + 7];
                if (MathF.Abs(r - list.SelectedColor.X) < 0.01f
                    && MathF.Abs(g - list.SelectedColor.Y) < 0.01f
                    && MathF.Abs(b - list.SelectedColor.Z) < 0.01f
                    && MathF.Abs(a - list.SelectedColor.W) < 0.01f)
                {
                    return true;
                }
            }
            return false;
        });
        Assert.False(anySelectedFill, "expected no SelectedColor fill in a column-based grid");
    }


    private static (TextRenderer renderer, UiRenderContext ctx) MakeContext(float w, float h)
    {
        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(w, h));
        var ctx = new UiRenderContext(renderer, new Vector2(w, h));
        return (renderer, ctx);
    }

    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }


    [Fact]
    public void PerCellDrawException_StillBalancesTheClipStack()
    {
        var list = new UiMarkupList
        {
            Width = 60f, Height = 40f, RowHeight = 18f,
            SelectedIndexSource = () => -1,
            Columns = new[]
            {
                UiMarkupListColumn.Icon(
                    60f,
                    () => new uint[] { 99u },
                    _ => throw new InvalidOperationException("boom"),
                    _ => { }),
            },
        };
        var (_, ctx) = MakeContext(200f, 200f);
        int depthBefore = ctx.ClipStackDepth;

        Assert.Throws<InvalidOperationException>(() => list.DrawSelfAndChildren(ctx));

        Assert.Equal(depthBefore, ctx.ClipStackDepth);
    }

    [Fact]
    public void Columns_IconThenCheck_EachCellDrawsInsideItsOwnColumnBounds()
    {
        var list = new UiMarkupList
        {
            Width = 100f, Height = 40f, RowHeight = 18f,
            SelectedIndexSource = () => -1,
            BackgroundColor = default, BorderColor = default,
            Columns = new[]
            {
                UiMarkupListColumn.Icon(
                    20f, () => new uint[] { 9u }, id => (id, 16, 16), _ => { }),
                UiMarkupListColumn.Check(20f, () => new[] { true }, _ => { }),
            },
        };
        var (renderer, ctx) = MakeContext(200f, 200f);

        list.DrawSelfAndChildren(ctx);

        var checkVertex = renderer.DebugSpriteSegmentVerts
            .SelectMany(s => Chunk(s.Verts))
            .First(v => ColorMatches(v, UiCheckLamp.CheckedInner));
        Assert.True(checkVertex[0] >= 20f - 0.01f && checkVertex[0] < 100f,
            $"expected the check glyph inside [20,100), got x={checkVertex[0]}");

        var iconQuad = Assert.Single(renderer.DebugSpriteSegmentVerts, s => s.Texture == 9u);
        Assert.True(iconQuad.Verts[0] < 20f,
            $"expected the icon column's sprite inside [0,20), got x={iconQuad.Verts[0]}");
        float iconWidth = iconQuad.Verts[8] - iconQuad.Verts[0];
        Assert.True(iconWidth > 0f, $"expected a non-zero icon quad width, got {iconWidth}");
    }

    [Fact]
    public void CheckColumn_DrawsCheckedAndUncheckedGlyphsPerRow()
    {
        var list = new UiMarkupList
        {
            Width = 60f, Height = 60f, RowHeight = 18f,
            SelectedIndexSource = () => -1,
            BackgroundColor = default, BorderColor = default,
            Columns = new[]
            {
                UiMarkupListColumn.Check(60f, () => new[] { true, false }, _ => { }),
            },
        };
        var (renderer, ctx) = MakeContext(200f, 200f);

        list.DrawSelfAndChildren(ctx);

        var quads = renderer.DebugSpriteSegmentVerts.SelectMany(s => Chunk(s.Verts)).ToList();
        Assert.Contains(quads, v => ColorMatches(v, UiCheckLamp.CheckedInner));
        Assert.Contains(quads, v => ColorMatches(v, UiCheckLamp.UncheckedInner));
        var checkedY = quads.First(v => ColorMatches(v, UiCheckLamp.CheckedInner))[1];
        var uncheckedY = quads.First(v => ColorMatches(v, UiCheckLamp.UncheckedInner))[1];
        Assert.True(uncheckedY > checkedY,
            $"expected row 1's glyph ({uncheckedY}) below row 0's ({checkedY})");
    }


    [Fact]
    public void CheckColumn_DrawsUncheckedLamp_ForRowsPastItsOwnRowCount()
    {
        var list = new UiMarkupList
        {
            Width = 60f, Height = 60f, RowHeight = 18f,
            SelectedIndexSource = () => -1,
            BackgroundColor = default, BorderColor = default,
            Columns = new[]
            {
                UiMarkupListColumn.Text(20f, () => new[] { "a", "b", "c" }, null),
                UiMarkupListColumn.Check(20f, () => new[] { true }, _ => { }),
            },
        };
        var (renderer, ctx) = MakeContext(200f, 200f);

        list.DrawSelfAndChildren(ctx);

        var uncheckedQuads = renderer.DebugSpriteSegmentVerts
            .SelectMany(s => Chunk(s.Verts))
            .Where(v => ColorMatches(v, UiCheckLamp.UncheckedInner))
            .ToList();
        Assert.True(uncheckedQuads.Count >= 2,
            $"expected an unchecked lamp for both row 1 and row 2 (past col1's own "
            + $"1-row data), got {uncheckedQuads.Count} unchecked lamp quad(s)");
    }


    [Fact]
    public void CheckColumn_GlyphIsHorizontallyCenteredInItsCell_LikeTheIconCellAlreadyIs()
    {
        var list = new UiMarkupList
        {
            Width = 50f, Height = 20f, RowHeight = 18f,
            SelectedIndexSource = () => -1,
            BackgroundColor = default, BorderColor = default,
            Columns = new[]
            {
                UiMarkupListColumn.Check(50f, () => new[] { true }, _ => { }),
            },
        };
        var (renderer, ctx) = MakeContext(200f, 200f);

        list.DrawSelfAndChildren(ctx);

        var innerVertex = renderer.DebugSpriteSegmentVerts
            .SelectMany(s => Chunk(s.Verts))
            .First(v => ColorMatches(v, UiCheckLamp.CheckedInner));
        Assert.True(innerVertex[0] > 15f,
            $"expected the check glyph centered in its 50px cell (~22.5), got x={innerVertex[0]}");
    }

    [Fact]
    public void IconColumn_DrawsResolvedRowIcon_AndSkipsAMissingOne()
    {
        var list = new UiMarkupList
        {
            Width = 60f, Height = 40f, RowHeight = 18f,
            SelectedIndexSource = () => -1,
            BackgroundColor = default, BorderColor = default,
            Columns = new[]
            {
                UiMarkupListColumn.Icon(
                    60f, () => new uint[] { 9u, 0u }, id => id == 0u ? (0u, 0, 0) : (id, 16, 16),
                    _ => { }),
            },
        };
        var (renderer, ctx) = MakeContext(200f, 200f);

        list.DrawSelfAndChildren(ctx);

        Assert.Contains(renderer.DebugSpriteSegmentVerts, s => s.Texture == 9u);
        // Row 1's id (0u) resolves to nothing — there is exactly one drawn
        // icon quad total (6 vertices; AppendQuad's two-triangle layout).
        int iconVertexCount = renderer.DebugSpriteSegmentVerts
            .Where(s => s.Texture == 9u)
            .Sum(s => s.Verts.Count / 8);
        Assert.Equal(6, iconVertexCount);
    }

    [Fact]
    public void LastColumn_AbsorbsRemainingWidth_RegardlessOfItsOwnDeclaredWidth()
    {
        var list = new UiMarkupList
        {
            Width = 100f, Height = 40f, RowHeight = 18f,
            SelectedIndexSource = () => -1,
            BackgroundColor = default, BorderColor = default,
            Columns = new[]
            {
                UiMarkupListColumn.Text(30f, () => new[] { "" }, null),
                UiMarkupListColumn.Icon(5f, () => new uint[] { 9u }, id => (id, 16, 16), _ => { }),
            },
        };
        var (renderer, ctx) = MakeContext(200f, 200f);

        list.DrawSelfAndChildren(ctx);

        var iconQuad = Assert.Single(renderer.DebugSpriteSegmentVerts, s => s.Texture == 9u);
        float drawnWidth = iconQuad.Verts[8] - iconQuad.Verts[0];
        Assert.True(drawnWidth > 10f,
            $"expected the icon to render near its full 16px size in the ~70px remainder "
            + $"cell (not squeezed into its declared 5px), got width {drawnWidth}");
        Assert.True(iconQuad.Verts[0] >= 30f - 0.01f);
    }

    [Fact]
    public void TextColumn_OverLongText_ClipsToItsOwnCellWidth()
    {
        var glyphs = new Dictionary<char, FontCharDesc>
        {
            ['W'] = new FontCharDesc { Unicode = 'W', Width = 8, Height = 8 },
        };
        var font = new UiDatFont(
            fgTex: 1u, fgW: 32, fgH: 32,
            bgTex: 0, bgW: 0, bgH: 0,
            lineHeight: 16f, baselineOffset: 12f,
            glyphs);

        var list = new UiMarkupList
        {
            Width = 100f, Height = 40f, RowHeight = 18f, DatFont = font,
            SelectedIndexSource = () => -1,
            BackgroundColor = default, BorderColor = default,
            Columns = new[]
            {
                UiMarkupListColumn.Text(10f, () => new[] { "WWWWWWWWWW" }, null),
                UiMarkupListColumn.Text(90f, () => new[] { "" }, null),
            },
        };
        var (renderer, ctx) = MakeContext(200f, 200f);

        list.DrawSelfAndChildren(ctx);

        var glyphVertices = renderer.DebugSpriteSegmentVerts
            .Where(s => s.Texture == 1u)
            .SelectMany(s => Chunk(s.Verts))
            .ToList();
        Assert.NotEmpty(glyphVertices);
        foreach (var vertex in glyphVertices)
        {
            float x = vertex[0];
            Assert.True(x <= 10f + 0.01f,
                $"expected every glyph vertex clipped inside the 10px column, got x={x}");
        }
    }

    /// <summary>Split a flat 8-floats/vertex, 6-vertices/quad buffer into per-vertex arrays.</summary>
    private static IEnumerable<float[]> Chunk(IReadOnlyList<float> verts)
    {
        for (int i = 0; i + 8 <= verts.Count; i += 8)
            yield return verts.Skip(i).Take(8).ToArray();
    }

    private static bool ColorMatches(float[] vertex, Vector4 color) =>
        MathF.Abs(vertex[4] - color.X) < 0.001f
        && MathF.Abs(vertex[5] - color.Y) < 0.001f
        && MathF.Abs(vertex[6] - color.Z) < 0.001f
        && MathF.Abs(vertex[7] - color.W) < 0.001f;


    private static UiMarkupList MakeHitTestList(
        out List<int> selections, out List<int> checkFires, out List<int> iconFires,
        int rowCount = 3, float width = 60f)
    {
        var sel = new List<int>();
        var chk = new List<int>();
        var icn = new List<int>();
        selections = sel; checkFires = chk; iconFires = icn;

        var textRows = Enumerable.Range(0, rowCount).Select(i => $"row{i}").ToArray();
        var checkRows = Enumerable.Range(0, rowCount).Select(i => i % 2 == 0).ToArray();
        var iconRows = Enumerable.Range(0, rowCount).Select(i => (uint)(i + 1)).ToArray();

        return new UiMarkupList
        {
            Width = width, Height = 200f, RowHeight = 20f,
            SelectedIndexSource = () => -1,
            SelectionChanged = row => sel.Add(row),
            Columns = new[]
            {
                UiMarkupListColumn.Text(20f, () => textRows, null),
                UiMarkupListColumn.Check(20f, () => checkRows, row => chk.Add(row)),
                UiMarkupListColumn.Icon(20f, () => iconRows, id => (id, 16, 16), row => icn.Add(row)),
            },
        };
    }

    [Fact]
    public void ClickInTextColumn_SelectsAndDoesNotFireCheckOrIcon()
    {
        var list = MakeHitTestList(out var sel, out var chk, out var icn);
        var (_, ctx) = MakeContext(200f, 200f);
        list.DrawSelfAndChildren(ctx); // frame order: draw, then handle input

        list.OnEvent(new UiEvent { Type = UiEventType.MouseDown, Data1 = 10, Data2 = 25 });

        Assert.Equal(new[] { 1 }, sel);
        Assert.Empty(chk);
        Assert.Empty(icn);
    }

    [Fact]
    public void ClickInCheckColumn_FiresCheckCallbackWithRowIndex_AndDoesNotSelect()
    {
        var list = MakeHitTestList(out var sel, out var chk, out var icn);
        var (_, ctx) = MakeContext(200f, 200f);
        list.DrawSelfAndChildren(ctx);

        list.OnEvent(new UiEvent { Type = UiEventType.MouseDown, Data1 = 30, Data2 = 45 });

        Assert.Equal(new[] { 2 }, chk);
        Assert.Empty(sel);
        Assert.Empty(icn);
    }

    [Fact]
    public void ClickInIconColumn_FiresIconCallbackWithRowIndex_AndDoesNotSelect()
    {
        var list = MakeHitTestList(out var sel, out var chk, out var icn);
        var (_, ctx) = MakeContext(200f, 200f);
        list.DrawSelfAndChildren(ctx);

        list.OnEvent(new UiEvent { Type = UiEventType.MouseDown, Data1 = 50, Data2 = 5 });

        Assert.Equal(new[] { 0 }, icn);
        Assert.Empty(sel);
        Assert.Empty(chk);
    }

    [Fact]
    public void ClickPastTheLastRow_DoesNothing_ButStillSwallowsThePress()
    {
        var list = MakeHitTestList(out var sel, out var chk, out var icn, rowCount: 2);
        var (_, ctx) = MakeContext(200f, 200f);
        list.DrawSelfAndChildren(ctx);

        bool handled = list.OnEvent(
            new UiEvent { Type = UiEventType.MouseDown, Data1 = 10, Data2 = 100 });

        Assert.True(handled);
        Assert.Empty(sel);
        Assert.Empty(chk);
        Assert.Empty(icn);
    }


    [Fact]
    public void ClickInShortCheckColumn_PastItsOwnRowCount_FiresNothing()
    {
        var fired = new List<int>();
        var list = new UiMarkupList
        {
            Width = 60f, Height = 200f, RowHeight = 20f,
            SelectedIndexSource = () => -1,
            Columns = new[]
            {
                UiMarkupListColumn.Text(20f, () => new[] { "a", "b", "c" }, null),
                UiMarkupListColumn.Check(20f, () => new[] { true }, row => fired.Add(row)),
            },
        };
        var (_, ctx) = MakeContext(200f, 200f);
        list.DrawSelfAndChildren(ctx);

        list.OnEvent(new UiEvent { Type = UiEventType.MouseDown, Data1 = 30, Data2 = 45 });

        Assert.Empty(fired);
    }

    [Fact]
    public void ClickInShortIconColumn_PastItsOwnRowCount_FiresNothing()
    {
        var fired = new List<int>();
        var list = new UiMarkupList
        {
            Width = 60f, Height = 200f, RowHeight = 20f,
            SelectedIndexSource = () => -1,
            Columns = new[]
            {
                UiMarkupListColumn.Text(20f, () => new[] { "a", "b", "c" }, null),
                UiMarkupListColumn.Icon(
                    20f, () => new uint[] { 9u }, id => (id, 16, 16), row => fired.Add(row)),
            },
        };
        var (_, ctx) = MakeContext(200f, 200f);
        list.DrawSelfAndChildren(ctx);

        list.OnEvent(new UiEvent { Type = UiEventType.MouseDown, Data1 = 30, Data2 = 45 });

        Assert.Empty(fired);
    }

    [Fact]
    public void ClickInShortTextColumn_WithOnclick_PastItsOwnRowCount_FiresNothing()
    {
        var fired = new List<int>();
        var selections = new List<int>();
        var list = new UiMarkupList
        {
            Width = 60f, Height = 200f, RowHeight = 20f,
            SelectedIndexSource = () => -1,
            SelectionChanged = row => selections.Add(row),
            Columns = new[]
            {
                UiMarkupListColumn.Text(
                    20f, () => new[] { "only-row-0" }, null, row => fired.Add(row)),
                UiMarkupListColumn.Text(20f, () => new[] { "a", "b", "c" }, null),
            },
        };
        var (_, ctx) = MakeContext(200f, 200f);
        list.DrawSelfAndChildren(ctx);

        list.OnEvent(new UiEvent { Type = UiEventType.MouseDown, Data1 = 10, Data2 = 45 });

        Assert.Empty(fired);
        Assert.Empty(selections);
    }

    // ── Per-frame and per-event allocation checks ────────

    [Fact]
    public void ClickEvent_DoesNotReinvokeColumnSourceFunctions_ReusesDrawSideMaterialization()
    {
        int textCalls = 0, checkCalls = 0, iconCalls = 0;
        var list = new UiMarkupList
        {
            Width = 60f, Height = 200f, RowHeight = 20f,
            SelectedIndexSource = () => -1,
            Columns = new[]
            {
                UiMarkupListColumn.Text(20f, () => { textCalls++; return new[] { "a", "b", "c" }; }, null),
                UiMarkupListColumn.Check(
                    20f,
                    () => { checkCalls++; return new[] { true, false, true }; },
                    _ => { }),
                UiMarkupListColumn.Icon(
                    20f,
                    () => { iconCalls++; return new uint[] { 1u, 2u, 3u }; },
                    id => (id, 16, 16),
                    _ => { }),
            },
        };
        var (_, ctx) = MakeContext(200f, 200f);

        list.DrawSelfAndChildren(ctx);
        Assert.Equal(1, textCalls);
        Assert.Equal(1, checkCalls);
        Assert.Equal(1, iconCalls);

        for (int i = 0; i < 5; i++)
        {
            list.OnEvent(new UiEvent { Type = UiEventType.MouseDown, Data1 = 10, Data2 = 25 });
            list.OnEvent(new UiEvent { Type = UiEventType.MouseDown, Data1 = 30, Data2 = 45 });
            list.OnEvent(new UiEvent { Type = UiEventType.MouseDown, Data1 = 50, Data2 = 5 });
        }

        Assert.Equal(1, textCalls);
        Assert.Equal(1, checkCalls);
        Assert.Equal(1, iconCalls);
    }

    [Fact]
    public void Scroll_OffsetIsRespectedBySubsequentHitTests()
    {
        var list = MakeHitTestList(out var sel, out _, out _, rowCount: 10, width: 60f);
        list.Height = 40f; // 2 visible rows of a 10-row list — forces real scrolling

        var (_, ctx) = MakeContext(200f, 200f);
        list.DrawSelfAndChildren(ctx);

        for (int i = 0; i < 3; i++)
            list.OnEvent(new UiEvent { Type = UiEventType.Scroll, Data0 = -1 });
        list.DrawSelfAndChildren(ctx);

        list.OnEvent(new UiEvent { Type = UiEventType.MouseDown, Data1 = 10, Data2 = 5 });

        Assert.Equal(new[] { 3 }, sel);
    }


    private sealed class LegacyBinding
    {
        public IReadOnlyList<string> Choices => new[] { "First", "Second" };
        public IReadOnlyList<uint> ChoiceColors => new[] { 0xFF0000u, 0x00FF00u };
        public int SelectedIndex { get; set; } = 1;
        public Action<int> SelectIndex => _ => { };
    }

    [Fact]
    public void ColumnLessList_ProducesTheIdenticalDrawRecordToTheHandBuiltWidget()
    {
        var glyphs = new Dictionary<char, FontCharDesc>
        {
            ['F'] = new FontCharDesc { Unicode = 'F', Width = 8, Height = 8 },
        };
        var font = new UiDatFont(
            fgTex: 1u, fgW: 32, fgH: 32,
            bgTex: 0, bgW: 0, bgH: 0,
            lineHeight: 16f, baselineOffset: 12f,
            glyphs);

        var binding = new LegacyBinding();
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" rowheight=\"18\" " +
            "items=\"{Choices}\" colors=\"{ChoiceColors}\" " +
            "selected=\"{SelectedIndex}\" onchange=\"{SelectIndex}\"/>" +
            "</panel>";

        var panel = MarkupDocument.Build(xml, binding, Sprite);
        var viaMarkup = Assert.IsType<UiMarkupList>(panel.Children[0]);
        viaMarkup.DatFont = font; // markup panels always get a host font; irrelevant to the pin itself
        viaMarkup.BackgroundColor = default;
        viaMarkup.BorderColor = default;

        var handBuilt = new UiMarkupList
        {
            Width = 180f, Height = 60f, RowHeight = 18f, DatFont = font,
            ItemsSource = () => binding.Choices,
            ItemColorsSource = () => binding.ChoiceColors,
            SelectedIndexSource = () => binding.SelectedIndex,
            BackgroundColor = default,
            BorderColor = default,
        };

        var (rendererMarkup, ctxMarkup) = MakeContext(200f, 200f);
        viaMarkup.DrawSelfAndChildren(ctxMarkup);
        var (rendererHand, ctxHand) = MakeContext(200f, 200f);
        handBuilt.DrawSelfAndChildren(ctxHand);

        var markupVerts = rendererMarkup.DebugSpriteSegmentVerts.ToList();
        var handVerts = rendererHand.DebugSpriteSegmentVerts.ToList();
        Assert.Equal(handVerts.Count, markupVerts.Count);
        for (int i = 0; i < handVerts.Count; i++)
        {
            Assert.Equal(handVerts[i].Texture, markupVerts[i].Texture);
            Assert.Equal(handVerts[i].Verts.Count, markupVerts[i].Verts.Count);
            for (int j = 0; j < handVerts[i].Verts.Count; j++)
                Assert.Equal(handVerts[i].Verts[j], markupVerts[i].Verts[j], 4);
        }

        Assert.Null(viaMarkup.Columns);

        // Neither side opted into selectionband="true" — row 1 IS selected
        // (LegacyBinding.SelectedIndex = 1) but the new default draws no
        // SelectedColor fill for it, on either the markup or the hand-built
        // path.
        Assert.False(viaMarkup.SelectionBandEnabled);
        Assert.False(handBuilt.SelectionBandEnabled);
        Assert.DoesNotContain(markupVerts, s => s.Texture == 0u
            && Chunk(s.Verts).Any(v => ColorMatches(v, viaMarkup.SelectedColor)));
    }


    private sealed class SelectionBandBinding
    {
        public IReadOnlyList<string> Choices => new[] { "First", "Second" };
        public int Selected { get; set; } = 1;
        public Action<int> SelectIndex => _ => { };
    }

    private static bool HasFillOfColor(
        IEnumerable<(uint Texture, IReadOnlyList<float> Verts)> segs, Vector4 color)
        => segs.Where(s => s.Texture == 0u)
            .SelectMany(s => Chunk(s.Verts))
            .Any(v => ColorMatches(v, color));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleColumnList_SelectionBand_DefaultsOffAndAttributeOptsIn(bool enabled)
    {
        var binding = new SelectionBandBinding();
        string attr = enabled ? " selectionband=\"true\"" : "";
        string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" rowheight=\"18\" " +
            $"items=\"{{Choices}}\" selected=\"{{Selected}}\" onchange=\"{{SelectIndex}}\"{attr}/>" +
            "</panel>";

        var panel = MarkupDocument.Build(xml, binding, Sprite);
        var list = Assert.IsType<UiMarkupList>(panel.Children[0]);
        Assert.Equal(enabled, list.SelectionBandEnabled);

        var (renderer, ctx) = MakeContext(200f, 200f);
        list.DrawSelfAndChildren(ctx);

        Assert.Equal(enabled, HasFillOfColor(renderer.DebugSpriteSegmentVerts, list.SelectedColor));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ColumnModeList_SelectionBand_DefaultsOffAndAttributeOptsIn(bool enabled)
    {
        var binding = new SelectionBandBinding();
        string attr = enabled ? " selectionband=\"true\"" : "";
        string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            $"<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" rowheight=\"18\" selected=\"{{Selected}}\" onchange=\"{{SelectIndex}}\"{attr}>" +
            "  <column type=\"text\" width=\"*\" items=\"{Choices}\"/>" +
            "</list></panel>";

        var panel = MarkupDocument.Build(xml, binding, Sprite);
        var list = Assert.IsType<UiMarkupList>(panel.Children[0]);
        Assert.Equal(enabled, list.SelectionBandEnabled);

        var (renderer, ctx) = MakeContext(200f, 200f);
        list.DrawSelfAndChildren(ctx);

        Assert.Equal(enabled, HasFillOfColor(renderer.DebugSpriteSegmentVerts, list.SelectedColor));
    }

    [Fact]
    public void ListSelectionBandAttribute_ParsesToProperty()
    {
        var binding = new SelectionBandBinding();
        const string xmlDefault =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" items=\"{Choices}\" " +
            "selected=\"{Selected}\" onchange=\"{SelectIndex}\"/>" +
            "</panel>";
        const string xmlEnabled =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<list x=\"0\" y=\"0\" w=\"180\" h=\"60\" items=\"{Choices}\" " +
            "selected=\"{Selected}\" onchange=\"{SelectIndex}\" selectionband=\"true\"/>" +
            "</panel>";

        var defaultList = Assert.IsType<UiMarkupList>(
            MarkupDocument.Build(xmlDefault, binding, Sprite).Children[0]);
        var enabledList = Assert.IsType<UiMarkupList>(
            MarkupDocument.Build(xmlEnabled, binding, Sprite).Children[0]);

        Assert.False(defaultList.SelectionBandEnabled);
        Assert.True(enabledList.SelectionBandEnabled);
    }

    // ── End-to-end MarkupDocument builds ───────────

    private static UiDatFont MakeAsciiFont()
    {
        var glyphs = new Dictionary<char, FontCharDesc>();
        foreach (char c in "ABCDEFGH")
            glyphs[c] = new FontCharDesc { Unicode = c, Width = 6, Height = 8 };
        return new UiDatFont(
            fgTex: 1u, fgW: 32, fgH: 32,
            bgTex: 0, bgW: 0, bgH: 0,
            lineHeight: 16f, baselineOffset: 12f,
            glyphs);
    }

    private sealed class MetaShapedBinding
    {
        public IReadOnlyList<uint> Delete { get; } = new uint[] { 101u, 102u };
        public IReadOnlyList<uint> MoveUp { get; } = new uint[] { 201u, 202u };
        public IReadOnlyList<uint> MoveDown { get; } = new uint[] { 301u, 302u };
        public IReadOnlyList<string> State { get; } = new[] { "A", "B" };
        public IReadOnlyList<string> Condition { get; } = new[] { "C", "D" };
        public IReadOnlyList<string> Action { get; } = new[] { "E", "F" };
        public int Selected { get; set; } = -1;
        public List<int> Selections { get; } = new();
        public Action<int> SelectRow => row => Selections.Add(row);
        public List<int> DeleteClicks { get; } = new();
        public Action<int> ClickDelete => row => DeleteClicks.Add(row);
        public List<int> MoveUpClicks { get; } = new();
        public Action<int> ClickMoveUp => row => MoveUpClicks.Add(row);
        public List<int> MoveDownClicks { get; } = new();
        public Action<int> ClickMoveDown => row => MoveDownClicks.Add(row);
        public List<int> ActionClicks { get; } = new();
        public Action<int> ClickAction => row => ActionClicks.Add(row);
    }

    [Fact]
    public void EndToEnd_MetaShapedSixColumnList_AutoColumnsShareRemainder_EachKindRoutesCorrectly()
    {
        var resolver = new FakeIconResolver();
        var binding = new MetaShapedBinding();
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"720\" h=\"140\">" +
            "<list x=\"0\" y=\"0\" w=\"703\" h=\"60\" rowheight=\"18\" " +
            "selected=\"{Selected}\" onchange=\"{SelectRow}\">" +
            "  <column type=\"icon\" width=\"16\" iconkind=\"item\" values=\"{Delete}\" onclick=\"{ClickDelete}\"/>" +
            "  <column type=\"icon\" width=\"16\" iconkind=\"item\" values=\"{MoveUp}\" onclick=\"{ClickMoveUp}\"/>" +
            "  <column type=\"icon\" width=\"16\" iconkind=\"item\" values=\"{MoveDown}\" onclick=\"{ClickMoveDown}\"/>" +
            "  <column type=\"text\" width=\"150\" items=\"{State}\"/>" +
            "  <column type=\"text\" width=\"*\" items=\"{Condition}\"/>" +
            "  <column type=\"text\" width=\"*\" items=\"{Action}\" onclick=\"{ClickAction}\"/>" +
            "</list></panel>";

        var panel = MarkupDocument.Build(xml, binding, Sprite, datFont: MakeAsciiFont(), icons: resolver);
        var list = Assert.IsType<UiMarkupList>(panel.Children[0]);
        Assert.Equal(6, list.Columns!.Count);
        Assert.Equal(
            new[]
            {
                UiMarkupListColumnKind.Icon, UiMarkupListColumnKind.Icon, UiMarkupListColumnKind.Icon,
                UiMarkupListColumnKind.Text, UiMarkupListColumnKind.Text, UiMarkupListColumnKind.Text,
            },
            list.Columns.Select(c => c.Kind));

        var (renderer, ctx) = MakeContext(800f, 200f);
        list.DrawSelfAndChildren(ctx);

        var deleteQuad = Assert.Single(renderer.DebugSpriteSegmentVerts, s => s.Texture == 101u);
        Assert.True(deleteQuad.Verts[0] < 16f);
        var moveUpQuad = Assert.Single(renderer.DebugSpriteSegmentVerts, s => s.Texture == 201u);
        Assert.True(moveUpQuad.Verts[0] >= 16f && moveUpQuad.Verts[0] < 32f);
        var moveDownQuad = Assert.Single(renderer.DebugSpriteSegmentVerts, s => s.Texture == 301u);
        Assert.True(moveDownQuad.Verts[0] >= 32f && moveDownQuad.Verts[0] < 48f);

        var glyphs = renderer.DebugSpriteSegmentVerts
            .Where(s => s.Texture == 1u)
            .SelectMany(s => Chunk(s.Verts))
            .ToList();
        Assert.Contains(glyphs, v => v[0] >= 48f && v[0] < 198f); // State
        Assert.Contains(glyphs, v => v[0] >= 198f && v[0] < 450f);
        Assert.Contains(glyphs, v => v[0] >= 450f && v[0] < 703f); // Action (auto, 253px — the slack)

        list.OnEvent(new UiEvent { Type = UiEventType.MouseDown, Data1 = 8, Data2 = 0 });
        list.OnEvent(new UiEvent { Type = UiEventType.MouseDown, Data1 = 24, Data2 = 18 });
        list.OnEvent(new UiEvent { Type = UiEventType.MouseDown, Data1 = 40, Data2 = 0 });
        list.OnEvent(new UiEvent { Type = UiEventType.MouseDown, Data1 = 60, Data2 = 18 });
        list.OnEvent(new UiEvent { Type = UiEventType.MouseDown, Data1 = 600, Data2 = 0 });

        Assert.Equal(new[] { 0 }, binding.DeleteClicks);
        Assert.Equal(new[] { 1 }, binding.MoveUpClicks);
        Assert.Equal(new[] { 0 }, binding.MoveDownClicks);
        Assert.Equal(new[] { 1 }, binding.Selections);
        Assert.Equal(new[] { 0 }, binding.ActionClicks);
    }

    private sealed class RouteShapedBinding
    {
        public IReadOnlyList<string> Names { get; } =
            Enumerable.Range(0, 9).Select(static i => $"WP{i}").ToArray();
        public IReadOnlyList<string> Counts { get; } =
            Enumerable.Range(0, 9).Select(static i => i.ToString()).ToArray();
        public IReadOnlyList<string> Filler { get; } = Array.Empty<string>();
        public int Selected { get; set; } = -1;
        public Action<int> Click => static _ => { };
    }

    [Fact]
    public void RouteShapedGrid_CountColumnStaysThirtyPxWhenTheListScrolls()
    {
        var binding = new RouteShapedBinding();
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"400\" h=\"200\">" +
            "<list x=\"0\" y=\"0\" w=\"370\" h=\"151\" rowheight=\"17\" " +
            "selected=\"{Selected}\">" +
            "  <column type=\"text\" width=\"324\" items=\"{Names}\" onclick=\"{Click}\"/>" +
            "  <column type=\"text\" width=\"30\" items=\"{Counts}\" onclick=\"{Click}\"/>" +
            "  <column type=\"text\" width=\"*\" items=\"{Filler}\"/>" +
            "</list></panel>";

        var panel = MarkupDocument.Build(xml, binding, Sprite);
        var list = Assert.IsType<UiMarkupList>(panel.Children[0]);

        var (renderer, ctx) = MakeContext(800f, 400f);
        list.DrawSelfAndChildren(ctx);

        System.Reflection.FieldInfo layoutField = typeof(UiMarkupList).GetField(
            "_cachedLayout",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var layout = ((float x, float w)[])layoutField.GetValue(list)!;

        Assert.Equal(3, layout.Length);
        Assert.Equal(30f, layout[1].w, 3);
    }

    private sealed class MonsterShapedBinding
    {
        public IReadOnlyList<bool> Checks { get; } = new[] { true, false };
        public IReadOnlyList<string> Texts { get; } = new[] { "A", "B" };
        public IReadOnlyList<uint> Icons0 { get; } = new uint[] { 501u, 502u };
        public IReadOnlyList<uint> Icons1 { get; } = new uint[] { 601u, 602u };
        public int Selected { get; set; } = -1;

        public List<(int Column, int Row)> Check0Fires { get; } = new();
        public Action<int> OnCheck0 => row => Check0Fires.Add((0, row));
        public Action<int> OnCheckRest => _ => { };

        public List<(int Column, int Row)> Text0Fires { get; } = new();
        public Action<int> OnText0 => row => Text0Fires.Add((0, row));
        public Action<int> OnTextRest => _ => { };

        public List<(int Column, int Row)> Icon0Fires { get; } = new();
        public Action<int> OnIcon0 => row => Icon0Fires.Add((0, row));
        public Action<int> OnIcon1 => _ => { };
    }

    [Fact]
    public void EndToEnd_MonstersShapedTwentyThreeColumnList_EachKindRoutesCorrectly()
    {
        var resolver = new FakeIconResolver();
        var binding = new MonsterShapedBinding();

        var columns = new System.Text.StringBuilder();
        for (int i = 0; i < 14; i++)
        {
            string onchange = i == 0 ? "{OnCheck0}" : "{OnCheckRest}";
            columns.Append(
                $"<column type=\"check\" width=\"16\" values=\"{{Checks}}\" onchange=\"{onchange}\"/>");
        }
        int[] textWidths = { 120, 20, 56, 56, 80, 80, 56 };
        for (int i = 0; i < textWidths.Length; i++)
        {
            string onclick = i == 0 ? "{OnText0}" : "{OnTextRest}";
            columns.Append(
                $"<column type=\"text\" width=\"{textWidths[i]}\" items=\"{{Texts}}\" onclick=\"{onclick}\"/>");
        }
        columns.Append("<column type=\"icon\" width=\"16\" iconkind=\"item\" values=\"{Icons0}\" onclick=\"{OnIcon0}\"/>");
        columns.Append("<column type=\"icon\" width=\"16\" iconkind=\"item\" values=\"{Icons1}\" onclick=\"{OnIcon1}\"/>");

        string xml =
            "<panel x=\"0\" y=\"0\" w=\"740\" h=\"140\">" +
            "<list x=\"0\" y=\"0\" w=\"724\" h=\"60\" rowheight=\"18\" selected=\"{Selected}\">" +
            columns +
            "</list></panel>";

        var panel = MarkupDocument.Build(xml, binding, Sprite, datFont: MakeAsciiFont(), icons: resolver);
        var list = Assert.IsType<UiMarkupList>(panel.Children[0]);
        Assert.Equal(23, list.Columns!.Count);
        Assert.Equal(14, list.Columns.Count(c => c.Kind == UiMarkupListColumnKind.Check));
        Assert.Equal(7, list.Columns.Count(c => c.Kind == UiMarkupListColumnKind.Text));
        Assert.Equal(2, list.Columns.Count(c => c.Kind == UiMarkupListColumnKind.Icon));

        var (renderer, ctx) = MakeContext(800f, 200f);
        list.DrawSelfAndChildren(ctx);

        var checkGlyph = renderer.DebugSpriteSegmentVerts
            .SelectMany(s => Chunk(s.Verts))
            .First(v => ColorMatches(v, UiCheckLamp.CheckedInner));
        Assert.True(checkGlyph[0] < 16f);

        var textGlyphs = renderer.DebugSpriteSegmentVerts
            .Where(s => s.Texture == 1u)
            .SelectMany(s => Chunk(s.Verts))
            .ToList();
        Assert.Contains(textGlyphs, v => v[0] >= 224f && v[0] < 344f);

        var icon0Quad = Assert.Single(renderer.DebugSpriteSegmentVerts, s => s.Texture == 501u);
        Assert.True(icon0Quad.Verts[0] >= 692f && icon0Quad.Verts[0] < 724f);

        list.OnEvent(new UiEvent { Type = UiEventType.MouseDown, Data1 = 8, Data2 = 18 });
        list.OnEvent(new UiEvent { Type = UiEventType.MouseDown, Data1 = 260, Data2 = 0 });
        list.OnEvent(new UiEvent { Type = UiEventType.MouseDown, Data1 = 700, Data2 = 18 });

        Assert.Equal(new[] { (0, 1) }, binding.Check0Fires);
        Assert.Equal(new[] { (0, 0) }, binding.Text0Fires);
        Assert.Equal(new[] { (0, 1) }, binding.Icon0Fires);
    }
}
