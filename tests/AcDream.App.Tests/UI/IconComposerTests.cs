using System;
using System.IO;
using AcDream.App.UI;
using AcDream.Core.Items;
using DatReaderWriter;
using DatReaderWriter.Options;

namespace AcDream.App.Tests.UI;

public class IconComposerTests
{
    private static byte[] Solid(int w, int h, byte r, byte g, byte b, byte a)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { px[i*4]=r; px[i*4+1]=g; px[i*4+2]=b; px[i*4+3]=a; }
        return px;
    }

    [Fact]
    public void Compose_alphaOver_topOpaqueLayerWins()
    {
        var bottom = (Solid(2, 2, 255, 0, 0, 255), 2, 2); // red, opaque
        var top    = (Solid(2, 2, 0, 0, 255, 255), 2, 2); // blue, opaque
        var (rgba, w, h) = IconComposer.Compose(new[] { bottom, top });
        Assert.Equal(2, w); Assert.Equal(2, h);
        Assert.Equal(0,   rgba[0]); // R
        Assert.Equal(0,   rgba[1]); // G
        Assert.Equal(255, rgba[2]); // B — top layer won
        Assert.Equal(255, rgba[3]); // A
    }

    [Fact]
    public void Compose_alphaOver_transparentTopKeepsBottom()
    {
        var bottom = (Solid(1, 1, 255, 0, 0, 255), 1, 1);
        var top    = (Solid(1, 1, 0, 0, 255, 0), 1, 1); // fully transparent blue
        var (rgba, _, _) = IconComposer.Compose(new[] { bottom, top });
        Assert.Equal(255, rgba[0]); // bottom red preserved
        Assert.Equal(0,   rgba[2]);
    }

    [Fact]
    public void Compose_opaqueUnderlayFirst_resultIsFullyOpaque()
    {
        var underlay  = (Solid(2, 2, 128, 64, 32, 255), 2, 2); // opaque tawny
        var baseIcon  = (Solid(2, 2,   0,  0,  0, 128), 2, 2); // semi-transparent black
        var (rgba, w, h) = IconComposer.Compose(new[] { underlay, baseIcon });
        Assert.Equal(2, w); Assert.Equal(2, h);
        // All pixels fully opaque: underlay A=255, baseIcon blends over it.
        for (int i = 0; i < w * h; i++)
            Assert.Equal(255, rgba[i * 4 + 3]);
    }


    private static string? ResolveDatDir()
    {
        var fromEnv = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv))
            return fromEnv;
        var def = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents", "Asheron's Call");
        return Directory.Exists(def) ? def : null;
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void ResolveUnderlayDid_goldenValues_matchDat()
    {
        var datDir = ResolveDatDir();
        if (datDir is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var composer = new IconComposer(dats, null!);

        Assert.Equal(0x060011CBu, composer.ResolveUnderlayDid(ItemType.MeleeWeapon));
        Assert.Equal(0x060011CFu, composer.ResolveUnderlayDid(ItemType.Armor));
        Assert.Equal(0x060011F3u, composer.ResolveUnderlayDid(ItemType.Clothing));
        Assert.Equal(0x060011D5u, composer.ResolveUnderlayDid(ItemType.Jewelry));
        Assert.Equal(0x060011D4u, composer.ResolveUnderlayDid(ItemType.None));
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void ResolveEffectDid_goldenValues_matchDat()
    {
        var datDir = ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var composer = new IconComposer(dats, null!);

        Assert.Equal(0x060011CAu, composer.ResolveEffectDid(0x0001u));
        Assert.Equal(0x060011C6u, composer.ResolveEffectDid(0x0002u));
        Assert.Equal(0x06001B05u, composer.ResolveEffectDid(0x0004u));
        Assert.Equal(0x06001B06u, composer.ResolveEffectDid(0x0010u));
        Assert.Equal(0x060011C5u, composer.ResolveEffectDid(0x1000u));
        Assert.Equal(0x060011C5u, composer.ResolveEffectDid(0x0000u));
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void TryGetEffectTile_noEffectBlack_magicalTextured()
    {
        var datDir = ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var composer = new IconComposer(dats, null!);

        Assert.True(composer.TryGetEffectTile(0u, out var black));
        Assert.Equal(32, black.Width);
        Assert.Equal(32, black.Height);
        Assert.True(black.Rgba8[0] <= 8 && black.Rgba8[1] <= 8 && black.Rgba8[2] <= 8);

        Assert.True(composer.TryGetEffectTile(0x1u, out var magic));
        bool uniform = true;
        for (int i = 4; i < magic.Width * magic.Height * 4 && uniform; i += 4)
            if (magic.Rgba8[i] != magic.Rgba8[0] || magic.Rgba8[i + 1] != magic.Rgba8[1] ||
                magic.Rgba8[i + 2] != magic.Rgba8[2])
                uniform = false;
        Assert.False(uniform);  // textured → gradient, not flat
    }

    [Fact]
    public void ReplaceWhiteFromSurface_copiesSourcePixelForPureWhiteOpaque()
    {
        // 2x2 dest: [white-opaque, red-opaque, white-transparent, white-opaque]
        var dst = new byte[]
        {
            255,255,255,255,   // pure white opaque  → takes src(0,0)
            255,  0,  0,255,   // red                → untouched
            255,255,255,  0,
            255,255,255,255,   // pure white opaque  → takes src(1,1)
        };
        var src = new byte[]
        {
            10, 20, 30,255,    // (0,0)
            40, 50, 60,255,    // (1,0)
            70, 80, 90,255,    // (0,1)
            100,110,120,255,   // (1,1)
        };
        IconComposer.ReplaceWhiteFromSurface(dst, 2, 2, src, 2, 2);
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, dst[0..4]);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, dst[4..8]);       // untouched (not white)
        Assert.Equal(new byte[] { 255, 255, 255, 0 }, dst[8..12]);    // untouched (transparent)
        Assert.Equal(new byte[] { 100, 110, 120, 255 }, dst[12..16]);
    }

    [Fact]
    public void TwoStageWithEffect_copiesTilePixelBeforeUnderlay()
    {
        var baseIcon  = (new byte[] { 255,255,255,255 }, 1, 1);   // 1x1 white opaque
        var drag = IconComposer.Compose(new[] { baseIcon });
        var tile = new byte[] { 0, 0, 255, 255 };                // 1x1 blue tile pixel
        IconComposer.ReplaceWhiteFromSurface(drag.rgba, drag.w, drag.h, tile, 1, 1);
        var underlay = (new byte[] { 105, 70, 50, 255 }, 1, 1);   // tawny opaque
        var final = IconComposer.Compose(new[] { underlay, (drag.rgba, drag.w, drag.h) });
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, final.rgba);  // tile pixel on top
    }
}
