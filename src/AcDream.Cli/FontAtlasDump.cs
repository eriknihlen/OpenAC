using AcDream.Core.Textures;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using DatReaderWriter.Types;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace AcDream.Cli;

public static class FontAtlasDump
{
    public static int Run(string datDir, string? fontIdText, string? sampleText, string outBase)
    {
        if (!Directory.Exists(datDir)) { Console.Error.WriteLine($"error: dir not found: {datDir}"); return 2; }
        uint fontId = string.IsNullOrWhiteSpace(fontIdText) ? 0x40000000u : ParseHex(fontIdText);
        string sample = string.IsNullOrEmpty(sampleText) ? "Chat  Send  12345  ghpqy" : sampleText;

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var font = dats.Get<Font>(fontId);
        if (font is null) { Console.Error.WriteLine($"error: Font 0x{fontId:X8} not found"); return 1; }

        Console.WriteLine($"Font 0x{fontId:X8}: fg=0x{font.ForegroundSurfaceDataId:X8} bg=0x{font.BackgroundSurfaceDataId:X8} " +
                          $"MaxCharHeight={font.MaxCharHeight} Baseline={font.BaselineOffset} glyphs={font.CharDescs.Count}");

        DecodedTexture fg = DecodeRs(dats, font.ForegroundSurfaceDataId);
        DecodedTexture? bg = font.BackgroundSurfaceDataId != 0 ? DecodeRs(dats, font.BackgroundSurfaceDataId) : null;
        Console.WriteLine($"  fg atlas {fg.Width}x{fg.Height}" + (bg is { } b ? $"   bg atlas {b.Width}x{b.Height}" : "   (no bg atlas)"));

        AlphaLuma(fg).SaveAsPng($"{outBase}-fg.png");
        Console.WriteLine($"wrote {outBase}-fg.png");
        if (bg is { } bgt) { AlphaLuma(bgt).SaveAsPng($"{outBase}-bg.png"); Console.WriteLine($"wrote {outBase}-bg.png"); }

        // Build a glyph lookup.
        var glyphs = new Dictionary<char, FontCharDesc>();
        foreach (var cd in font.CharDescs) glyphs[(char)cd.Unicode] = cd;

        var panel = new Rgba32(28, 28, 32, 255);
        var fill = new Rgba32(255, 255, 255, 255);   // white fill, like System default-ish
        var outline = new Rgba32(0, 0, 0, 255);

        int lineH = Math.Max((int)font.MaxCharHeight, 8);

        // (a) integer baseline, per-glyph round (works — like the vitals digits).
        using var native = RenderSample(sample, glyphs, fg, bg, lineH, panel, fill, outline, 0f, snapOnce: false);
        Save6x(native, $"{outBase}-sample");

        using var jitter = RenderSample(sample, glyphs, fg, bg, lineH, panel, fill, outline, 0.5f, snapOnce: false);
        Save6x(jitter, $"{outBase}-jitter");

        // (c) Same fractional baseline, but the line baseline is snapped to a whole pixel ONCE
        //     before adding the integer per-glyph offsets → the fix. Should be straight again.
        using var fixed_ = RenderSample(sample, glyphs, fg, bg, lineH, panel, fill, outline, 0.5f, snapOnce: true);
        Save6x(fixed_, $"{outBase}-fixed");

        Console.WriteLine($"wrote {outBase}-sample-6x.png (ok), {outBase}-jitter-6x.png (bug repro), {outBase}-fixed-6x.png (fix)");
        return 0;
    }

    private static Image<Rgba32> RenderSample(
        string text, Dictionary<char, FontCharDesc> glyphs,
        DecodedTexture fg, DecodedTexture? bg, int lineH,
        Rgba32 panel, Rgba32 fill, Rgba32 outline, float originYExtra, bool snapOnce)
    {
        // First pass: measure pen width.
        float pen = 0; float maxX = 0;
        foreach (char ch in text)
            if (glyphs.TryGetValue(ch, out var g)) { maxX = Math.Max(maxX, pen + g.HorizontalOffsetBefore + g.Width); pen += g.HorizontalOffsetBefore + g.Width + g.HorizontalOffsetAfter; }
        int w = Math.Max(8, (int)MathF.Ceiling(Math.Max(maxX, pen)) + 4);
        int h = lineH + 6;
        var img = new Image<Rgba32>(w, h, panel);

        float originY = 3f + originYExtra;
        float baseY = MathF.Round(originY);   // snapped line baseline (the fix)
        pen = 2;
        foreach (char ch in text)
        {
            if (!glyphs.TryGetValue(ch, out var g)) { continue; }
            float gx = MathF.Round(pen + g.HorizontalOffsetBefore);
            float gy = snapOnce
                ? baseY + g.VerticalOffsetBefore                       // fix: integer baseline + integer offset
                : MathF.Round(originY + g.VerticalOffsetBefore);       // bug: independent per-glyph rounding
            if (g.Width > 0 && g.Height > 0)
            {
                if (bg is { } bgt) BlitGlyph(img, bgt, g, (int)gx, (int)gy, outline);
                BlitGlyph(img, fg, g, (int)gx, (int)gy, fill);
            }
            pen += g.HorizontalOffsetBefore + g.Width + g.HorizontalOffsetAfter;
        }
        return img;
    }

    private static void Save6x(Image<Rgba32> native, string outBase)
    {
        using var zoom = native.Clone(c => c.Resize(native.Width * 6, native.Height * 6, KnownResamplers.NearestNeighbor));
        zoom.SaveAsPng($"{outBase}-6x.png");
    }

    private static void BlitGlyph(Image<Rgba32> dst, DecodedTexture atlas, FontCharDesc g, int dx, int dy, Rgba32 tint)
    {
        for (int sy = 0; sy < g.Height; sy++)
        {
            int py = dy + sy;
            if (py < 0 || py >= dst.Height) continue;
            int ay = g.OffsetY + sy;
            if (ay < 0 || ay >= atlas.Height) continue;
            for (int sx = 0; sx < g.Width; sx++)
            {
                int px = dx + sx;
                if (px < 0 || px >= dst.Width) continue;
                int ax = g.OffsetX + sx;
                if (ax < 0 || ax >= atlas.Width) continue;
                int idx = (ay * atlas.Width + ax) * 4;
                float cov = atlas.Rgba8[idx + 3] / 255f;
                if (cov <= 0f) continue;
                var bgpx = dst[px, py];
                dst[px, py] = new Rgba32(
                    (byte)(tint.R * cov + bgpx.R * (1 - cov)),
                    (byte)(tint.G * cov + bgpx.G * (1 - cov)),
                    (byte)(tint.B * cov + bgpx.B * (1 - cov)),
                    255);
            }
        }
    }

    private static Image<Rgba32> AlphaLuma(DecodedTexture t)
    {
        var img = new Image<Rgba32>(t.Width, t.Height);
        for (int y = 0; y < t.Height; y++)
            for (int x = 0; x < t.Width; x++)
            {
                byte a = t.Rgba8[(y * t.Width + x) * 4 + 3];
                img[x, y] = new Rgba32(a, a, a, 255);
            }
        img.Mutate(c => c.Resize(t.Width * 4, t.Height * 4, KnownResamplers.NearestNeighbor));
        return img;
    }

    private static DecodedTexture DecodeRs(DatCollection dats, uint id)
    {
        var rs = dats.Get<RenderSurface>(id);
        if (rs is null) { Console.Error.WriteLine($"  missing RenderSurface 0x{id:X8}"); return DecodedTexture.Magenta; }
        return SurfaceDecoder.DecodeRenderSurface(rs);
    }

    private static uint ParseHex(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0u;
    }
}
