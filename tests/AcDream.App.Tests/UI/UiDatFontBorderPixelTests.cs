using System;
using System.Collections.Generic;
using System.IO;
using AcDream.App.Rendering;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using AcDream.Content;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using DatReaderWriter.Types;
using SysEnv = System.Environment;

namespace AcDream.App.Tests.UI;

public sealed class UiDatFontBorderPixelTests
{
    [Theory]
    [InlineData(0x40000000u, 4u, 4u)]
    [InlineData(0x40000001u, 4u, 4u)]
    [InlineData(0x40000002u, 3u, 3u)]   // 14px bold serif
    [InlineData(0x40000025u, 3u, 3u)]   // 11px — the pre-round-4 SpewBox placeholder face
    [Trait("Lane", "InstalledDat")]
    public void RealDatFont_HasExpectedBorderPixels(uint fontId, uint expectedHorizontal, uint expectedVertical)
    {
        string? datDir = ResolveDatDir();
        if (datDir is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        Assert.True(dats.TryGet<Font>(fontId, out Font? font), $"Font 0x{fontId:X8} not found");
        Assert.NotNull(font);
        Assert.Equal(expectedHorizontal, font!.NumHorizontalBorderPixels);
        Assert.Equal(expectedVertical, font.NumVerticalBorderPixels);
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void RealDatFont_EveryFontWithABackgroundAtlas_HasANonZeroBorder()
    {
        string? datDir = ResolveDatDir();
        if (datDir is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        int checkedCount = 0;
        for (uint id = 0x40000000u; id <= 0x40000032u; id++)
        {
            if (!dats.TryGet<Font>(id, out Font? font) || font is null)
                continue;
            checkedCount++;

            bool hasBackground = font.BackgroundSurfaceDataId != 0;
            bool hasBorder = font.NumHorizontalBorderPixels > 0 || font.NumVerticalBorderPixels > 0;
            Assert.True(
                hasBackground == hasBorder,
                $"Font 0x{id:X8}: hasBackground={hasBackground} but hasBorder={hasBorder}");
        }

        Assert.True(checkedCount > 10, "expected the documented font sweep to find multiple populated fonts");
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void RealDatFont_HasLessThanAndGreaterThanGlyphs()
    {
        string? datDir = ResolveDatDir();
        if (datDir is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        var device = new RecordingGpuDevice();
        var cache = new TextureCache(device, adapter);

        UiDatFont? font = UiDatFont.Load(adapter, cache);
        Assert.NotNull(font);
        Assert.True(font!.TryGetGlyph('<', out _), "default font is missing '<' (toggle glyph)");
        Assert.True(font.TryGetGlyph('>', out _), "default font is missing '>' (toggle glyph)");
    }

    [Theory]
    [InlineData(4, 4)]
    [InlineData(3, 3)]
    [InlineData(0, 0)]
    public void Ctor_StoresBorderPixelsVerbatim(int borderX, int borderY)
    {
        var font = new UiDatFont(
            fgTex: 1, fgW: 64, fgH: 64,
            bgTex: 2, bgW: 64, bgH: 64,
            lineHeight: 16f, baselineOffset: 12f,
            glyphs: new Dictionary<char, FontCharDesc>(),
            borderX: borderX, borderY: borderY);

        Assert.Equal(borderX, font.BorderX);
        Assert.Equal(borderY, font.BorderY);
    }

    [Fact]
    public void Ctor_OmittedBorderPixels_DefaultToZero()
    {
        var font = new UiDatFont(
            fgTex: 0, fgW: 0, fgH: 0,
            bgTex: 0, bgW: 0, bgH: 0,
            lineHeight: 16f, baselineOffset: 12f,
            glyphs: new Dictionary<char, FontCharDesc>());

        Assert.Equal(0, font.BorderX);
        Assert.Equal(0, font.BorderY);
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
