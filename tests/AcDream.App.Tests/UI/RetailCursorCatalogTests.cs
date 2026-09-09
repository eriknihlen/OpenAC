using AcDream.App.Rendering;
using AcDream.App.UI;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using SysEnv = System.Environment;

namespace AcDream.App.Tests.UI;

public sealed class RetailCursorCatalogTests
{
    [Theory]
    [InlineData(CursorFeedbackKind.WindowMove, 0x06006119u)]
    [InlineData(CursorFeedbackKind.ResizeHorizontal, 0x06006128u)]
    [InlineData(CursorFeedbackKind.ResizeVertical, 0x06005E66u)]
    [InlineData(CursorFeedbackKind.ResizeDiagonalNwse, 0x06006126u)]
    [InlineData(CursorFeedbackKind.ResizeDiagonalNesw, 0x06006127u)]
    public void WindowControlCursorSpecs_matchRetailLayoutDescMedia(
        CursorFeedbackKind kind,
        uint expectedDid)
    {
        Assert.True(RetailCursorCatalog.TryGetWindowControlCursor(kind, out var cursor));
        Assert.Equal(new UiCursorMedia(expectedDid, 16, 16), cursor);
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void WindowControlCursorSpecs_resolveGoldenDatSurfaces()
    {
        string? datDir = ResolveDatDir();
        if (datDir is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        CursorFeedbackKind[] kinds =
        [
            CursorFeedbackKind.WindowMove,
            CursorFeedbackKind.ResizeHorizontal,
            CursorFeedbackKind.ResizeVertical,
            CursorFeedbackKind.ResizeDiagonalNwse,
            CursorFeedbackKind.ResizeDiagonalNesw,
        ];

        foreach (CursorFeedbackKind kind in kinds)
        {
            Assert.True(RetailCursorCatalog.TryGetWindowControlCursor(kind, out var cursor));
            Assert.True(dats.Portal.TryGet<RenderSurface>(cursor.File, out var surface), kind.ToString());
            Assert.NotNull(surface);
            Assert.Equal(32, surface.Width);
            Assert.Equal(32, surface.Height);
        }
    }

    [Theory]
    [InlineData(RetailGlobalCursorKind.Default, 0x01u, 0, 0)]
    [InlineData(RetailGlobalCursorKind.DefaultFound, 0x02u, 0, 0)]
    [InlineData(RetailGlobalCursorKind.MeleeOrMissile, 0x03u, 0, 0)]
    [InlineData(RetailGlobalCursorKind.MeleeOrMissileFound, 0x04u, 0, 0)]
    [InlineData(RetailGlobalCursorKind.Magic, 0x05u, 0, 0)]
    [InlineData(RetailGlobalCursorKind.MagicFound, 0x06u, 0, 0)]
    [InlineData(RetailGlobalCursorKind.Examine, 0x0Au, 0, 0)]
    [InlineData(RetailGlobalCursorKind.ExamineFound, 0x0Bu, 0, 0)]
    [InlineData(RetailGlobalCursorKind.Use, 0x0Cu, 14, 14)]
    [InlineData(RetailGlobalCursorKind.UseFound, 0x0Du, 14, 14)]
    [InlineData(RetailGlobalCursorKind.Busy, 0x0Eu, 0, 0)]
    [InlineData(RetailGlobalCursorKind.BusyFound, 0x0Fu, 0, 0)]
    [InlineData(RetailGlobalCursorKind.TargetPending, 0x27u, 14, 14)]
    [InlineData(RetailGlobalCursorKind.TargetValid, 0x28u, 14, 14)]
    [InlineData(RetailGlobalCursorKind.TargetInvalid, 0x29u, 14, 14)]
    public void GlobalCursorSpecs_matchClientUISystemUpdateCursorState(
        RetailGlobalCursorKind kind,
        uint expectedEnum,
        int expectedHotspotX,
        int expectedHotspotY)
    {
        Assert.Equal(6u, RetailCursorCatalog.CursorEnumTable);

        Assert.True(RetailCursorCatalog.TryGetGlobalCursor(kind, out var spec));

        Assert.Equal(expectedEnum, spec.EnumId);
        Assert.Equal(expectedHotspotX, spec.HotspotX);
        Assert.Equal(expectedHotspotY, spec.HotspotY);
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void ReachableGlobalCursorSpecs_resolveGoldenDatSurfaces()
    {
        string? datDir = ResolveDatDir();
        if (datDir is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var resolver = new RetailCursorResolver(dats, new object());

        var expected = new Dictionary<RetailGlobalCursorKind, UiCursorMedia>
        {
            [RetailGlobalCursorKind.Default] = new(0x06004D68u, 0, 0),
            [RetailGlobalCursorKind.DefaultFound] = new(0x06004D69u, 0, 0),
            [RetailGlobalCursorKind.MeleeOrMissile] = new(0x06004D6Au, 0, 0),
            [RetailGlobalCursorKind.MeleeOrMissileFound] = new(0x06004D6Bu, 0, 0),
            [RetailGlobalCursorKind.Magic] = new(0x06004D6Cu, 0, 0),
            [RetailGlobalCursorKind.MagicFound] = new(0x06004D6Du, 0, 0),
            [RetailGlobalCursorKind.Examine] = new(0x06004D71u, 0, 0),
            [RetailGlobalCursorKind.ExamineFound] = new(0x06004D71u, 0, 0),
            [RetailGlobalCursorKind.Use] = new(0x06004D72u, 14, 14),
            [RetailGlobalCursorKind.UseFound] = new(0x06004D72u, 14, 14),
            [RetailGlobalCursorKind.Busy] = new(0x06004D74u, 0, 0),
            [RetailGlobalCursorKind.BusyFound] = new(0x06004D75u, 0, 0),
            [RetailGlobalCursorKind.TargetPending] = new(0x06004D73u, 14, 14),
            [RetailGlobalCursorKind.TargetValid] = new(0x06005E6Bu, 14, 14),
            [RetailGlobalCursorKind.TargetInvalid] = new(0x06005E6Au, 14, 14),
        };

        foreach (var (kind, cursor) in expected)
        {
            Assert.True(resolver.TryResolve(kind, out var actual), kind.ToString());
            Assert.Equal(cursor, actual);
        }
    }

    private static string? ResolveDatDir()
    {
        string? fromEnv = SysEnv.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv))
            return fromEnv;

        string defaultDir = Path.Combine(
            SysEnv.GetFolderPath(SysEnv.SpecialFolder.UserProfile),
            "Documents",
            "Asheron's Call");
        return Directory.Exists(defaultDir) ? defaultDir : null;
    }
}
