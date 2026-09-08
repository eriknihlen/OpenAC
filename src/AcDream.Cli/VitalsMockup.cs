using AcDream.Core.Textures;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace AcDream.Cli;

public static class VitalsMockup
{
    private const uint TL = 0x060074C3, TOP = 0x060074BF, TR = 0x060074C4;
    private const uint LEFT = 0x060074C0, RIGHT = 0x060074C2;
    private const uint BL = 0x060074C5, BOT = 0x060074C1, BR = 0x060074C6;

    private readonly record struct Vital(
        string Name, float Frac,
        uint BackL, uint BackM, uint BackR, uint FrontL, uint FrontM, uint FrontR);

    private static readonly Vital[] Vitals =
    {
        new("health",  0.80f, 0x0600747E, 0x0600747F, 0x06007480, 0x06007481, 0x06007482, 0x06007483),
        new("stamina", 0.50f, 0x06007484, 0x06007485, 0x06007486, 0x06007487, 0x06007488, 0x06007489),
        new("mana",    0.65f, 0x0600748A, 0x0600748B, 0x0600748C, 0x0600748D, 0x0600748E, 0x0600748F),
    };

    private const uint CenterFill = 0x06004CC2;   // dark interior panel (UiNineSlicePanel draws this)
    private const int Border = 5, BarH = 16, Zoom = 6;
    private const int BarW = 150;
    private static readonly int[] BarLocalY = { 0, 16, 32 };   // flush stacked inside the interior

    public static int Render(string datDir, string outPath)
    {
        if (!Directory.Exists(datDir)) { Console.Error.WriteLine($"error: dir not found: {datDir}"); return 2; }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        int winW = BarW + 2 * Border;          // 160
        int winH = 3 * BarH + 2 * Border;       // 58
        using var canvas = new Image<Rgba32>(winW, winH, new Rgba32(20, 20, 24, 255));

        DrawWindow(canvas, dats, 0, winW, winH, tileMid: true);

        canvas.Mutate(c => c.Resize(canvas.Width * Zoom, canvas.Height * Zoom, KnownResamplers.NearestNeighbor));
        canvas.SaveAsPng(outPath);
        Console.WriteLine($"wrote {outPath} ({canvas.Width}x{canvas.Height}) — faithful default vitals window 0x2100006C");
        return 0;
    }

    private static void DrawWindow(Image<Rgba32> canvas, DatCollection dats, int offY, int winW, int winH, bool tileMid)
    {
        using (var cf = Load(dats, CenterFill))
            Blit(canvas, cf, Border, offY + Border, winW - 2 * Border, winH - 2 * Border);

        using (var tl = Load(dats, TL)) using (var top = Load(dats, TOP)) using (var tr = Load(dats, TR))
        using (var le = Load(dats, LEFT)) using (var ri = Load(dats, RIGHT))
        using (var bl = Load(dats, BL)) using (var bo = Load(dats, BOT)) using (var br = Load(dats, BR))
        {
            Blit(canvas, tl, 0, offY, Border, Border);
            Blit(canvas, top, Border, offY, winW - 2 * Border, Border);
            Blit(canvas, tr, winW - Border, offY, Border, Border);
            Blit(canvas, le, 0, offY + Border, Border, winH - 2 * Border);
            Blit(canvas, ri, winW - Border, offY + Border, Border, winH - 2 * Border);
            Blit(canvas, bl, 0, offY + winH - Border, Border, Border);
            Blit(canvas, bo, Border, offY + winH - Border, winW - 2 * Border, Border);
            Blit(canvas, br, winW - Border, offY + winH - Border, Border, Border);
        }

        using (var gc = Load(dats, 0x06006129))
        using (var gt = Load(dats, 0x0600612A)) using (var gb = Load(dats, 0x0600612C))
        using (var gl = Load(dats, 0x0600612B)) using (var gr = Load(dats, 0x0600612D))
        {
            Blit(canvas, gt, Border, offY, winW - 2 * Border, Border);
            Blit(canvas, gb, Border, offY + winH - Border, winW - 2 * Border, Border);
            Blit(canvas, gl, 0, offY + Border, Border, winH - 2 * Border);
            Blit(canvas, gr, winW - Border, offY + Border, Border, winH - 2 * Border);
            Blit(canvas, gc, 0, offY, Border, Border);
            Blit(canvas, gc, winW - Border, offY, Border, Border);
            Blit(canvas, gc, 0, offY + winH - Border, Border, Border);
            Blit(canvas, gc, winW - Border, offY + winH - Border, Border, Border);
        }

        for (int i = 0; i < Vitals.Length; i++)
        {
            var v = Vitals[i];
            int y = offY + Border + BarLocalY[i];
            using var bl_ = Load(dats, v.BackL); using var bm = Load(dats, v.BackM); using var br_ = Load(dats, v.BackR);
            using var fl = Load(dats, v.FrontL); using var fm = Load(dats, v.FrontM); using var fr = Load(dats, v.FrontR);
            DrawHBar(canvas, bl_, bm, br_, Border, y, BarW, BarH, BarW, tileMid);
            int fw = (int)MathF.Round(BarW * v.Frac);
            if (fw > 0) DrawHBar(canvas, fl, fm, fr, Border, y, BarW, BarH, fw, tileMid);
        }
    }

    private static void DrawHBar(
        Image<Rgba32> canvas, Image<Rgba32> left, Image<Rgba32> mid, Image<Rgba32> right,
        int x, int y, int w, int h, int clipW, bool tileMid)
    {
        if (w <= 0 || clipW <= 0) return;
        int capL = Math.Min(left.Width, w);
        int capR = Math.Min(right.Width, w - capL);
        int midW = w - capL - capR;

        DrawClippedPiece(canvas, left, x, y, 0, capL, h, clipW);          // left cap (once, native)
        if (tileMid) TileMiddle(canvas, mid, x, y, capL, midW, h, clipW);
        else DrawClippedPiece(canvas, mid, x, y, capL, midW, h, clipW);   // stretch across the span
        DrawClippedPiece(canvas, right, x, y, w - capR, capR, h, clipW);  // right cap (once, native)
    }

    private static void TileMiddle(
        Image<Rgba32> canvas, Image<Rgba32> mid, int x, int y, int midLocalX, int midW, int h, int clipW)
    {
        int tileW = Math.Max(1, mid.Width);
        for (int mx = 0; mx < midW; mx += tileW)
        {
            int localX = midLocalX + mx;
            int segW = Math.Min(tileW, midW - mx);
            int visible = Math.Min(segW, clipW - localX);
            if (visible <= 0) break;
            int cropW = Math.Min(visible, mid.Width);
            using var seg = mid.Clone(c => c.Crop(new Rectangle(0, 0, cropW, mid.Height)).Resize(visible, h));
            canvas.Mutate(c => c.DrawImage(seg, new Point(x + localX, y), 1f));
        }
    }

    private static void DrawClippedPiece(
        Image<Rgba32> canvas, Image<Rgba32> src, int x, int y, int pieceLocalX, int pieceW, int h, int clipW)
    {
        if (pieceW <= 0) return;
        int visibleW = Math.Min(pieceW, clipW - pieceLocalX);
        if (visibleW <= 0) return;
        int srcCropW = Math.Max(1, (int)MathF.Round(src.Width * (visibleW / (float)pieceW)));
        srcCropW = Math.Min(srcCropW, src.Width);
        using var piece = src.Clone(c => c.Crop(new Rectangle(0, 0, srcCropW, src.Height)).Resize(visibleW, h));
        canvas.Mutate(c => c.DrawImage(piece, new Point(x + pieceLocalX, y), 1f));
    }

    private static void Blit(Image<Rgba32> canvas, Image<Rgba32> src, int x, int y, int dw, int dh)
    {
        if (dw <= 0 || dh <= 0) return;
        using var s = src.Clone(c => c.Resize(dw, dh));
        canvas.Mutate(c => c.DrawImage(s, new Point(x, y), 1f));
    }

    public static int ExportSheet(string datDir, string idsCsv, string outPath)
    {
        if (!Directory.Exists(datDir)) { Console.Error.WriteLine($"error: dir not found: {datDir}"); return 2; }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var ids = idsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(ParseHex).Where(x => x != 0).ToArray();
        if (ids.Length == 0) { Console.Error.WriteLine("no valid ids"); return 2; }

        var imgs = ids.Select(id => Load(dats, id)).ToArray();
        const int pad = 6, zoom = 10;
        int totalW = pad + imgs.Sum(i => i.Width + pad);
        int maxH = imgs.Max(i => i.Height);
        using var canvas = new Image<Rgba32>(totalW, maxH + 2 * pad, new Rgba32(64, 64, 72, 255));
        int x = pad;
        foreach (var im in imgs)
        {
            canvas.Mutate(c => c.DrawImage(im, new Point(x, pad), 1f));
            x += im.Width + pad;
        }
        canvas.Mutate(c => c.Resize(canvas.Width * zoom, canvas.Height * zoom, KnownResamplers.NearestNeighbor));
        canvas.SaveAsPng(outPath);
        Console.WriteLine("order (L→R): " + string.Join("  ", ids.Zip(imgs, (id, im) => $"0x{id:X8}={im.Width}x{im.Height}")));
        foreach (var im in imgs) im.Dispose();
        return 0;
    }

    public static int MockSelBar(string datDir, string outPath)
    {
        if (!Directory.Exists(datDir)) { Console.Error.WriteLine($"error: dir not found: {datDir}"); return 2; }
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var back = Load(dats, 0x0600193E);
        using var fill = Load(dats, 0x0600193F);

        const int elemW = 140, zoom = 8, gap = 4;
        int elemH = Math.Min(back.Height, fill.Height);
        float[] fracs = { 1.0f, 0.9f, 0.7f, 0.5f, 0.0f };
        int rowH = elemH + gap;
        using var canvas = new Image<Rgba32>(elemW, rowH * fracs.Length, new Rgba32(20, 20, 24, 255));

        for (int i = 0; i < fracs.Length; i++)
        {
            int y = i * rowH;
            float p = fracs[i];
            int backCrop = Math.Min(elemW, back.Width);
            using (var b = back.Clone(c => c.Crop(new Rectangle(0, 0, backCrop, elemH))))
                canvas.Mutate(c => c.DrawImage(b, new Point(0, y), 1f));
            int fillW = (int)MathF.Round(elemW * p);
            if (fillW > 0)
            {
                int fillCrop = Math.Min(fillW, fill.Width);
                using var f = fill.Clone(c => c.Crop(new Rectangle(0, 0, fillCrop, elemH)));
                canvas.Mutate(c => c.DrawImage(f, new Point(0, y), 1f));
            }
        }

        canvas.Mutate(c => c.Resize(canvas.Width * zoom, canvas.Height * zoom, KnownResamplers.NearestNeighbor));
        canvas.SaveAsPng(outPath);
        Console.WriteLine($"wrote {outPath} — selbar composite, rows = health 1.0 / 0.9 / 0.7 / 0.5 / 0.0");
        return 0;
    }

    /// <summary>Print the RGB of a rectangular block of pixels from a PNG (framebuffer probe).</summary>
    public static int Probe(string inPath, int x0, int y0, int x1, int y1)
    {
        if (!File.Exists(inPath)) { Console.Error.WriteLine($"not found: {inPath}"); return 2; }
        using var img = Image.Load<Rgba32>(inPath);
        x0 = Math.Clamp(x0, 0, img.Width - 1); x1 = Math.Clamp(x1, 0, img.Width - 1);
        y0 = Math.Clamp(y0, 0, img.Height - 1); y1 = Math.Clamp(y1, 0, img.Height - 1);
        Console.WriteLine($"{inPath} {img.Width}x{img.Height}  cols x={x0}..{x1}");
        for (int y = y0; y <= y1; y++)
        {
            var sb = new System.Text.StringBuilder($"y={y,4}: ");
            for (int x = x0; x <= x1; x++) { var p = img[x, y]; sb.Append($"{p.R:X2}{p.G:X2}{p.B:X2} "); }
            Console.WriteLine(sb.ToString());
        }
        return 0;
    }

    public static int Crop(string inPath, int x, int y, int w, int h, int zoom, string outPath)
    {
        if (!File.Exists(inPath)) { Console.Error.WriteLine($"not found: {inPath}"); return 2; }
        using var img = Image.Load<Rgba32>(inPath);
        x = Math.Clamp(x, 0, img.Width - 1);
        y = Math.Clamp(y, 0, img.Height - 1);
        w = Math.Clamp(w, 1, img.Width - x);
        h = Math.Clamp(h, 1, img.Height - y);
        if (zoom < 1) zoom = 1;
        img.Mutate(c => c.Crop(new Rectangle(x, y, w, h)).Resize(w * zoom, h * zoom, KnownResamplers.NearestNeighbor));
        img.SaveAsPng(outPath);
        Console.WriteLine($"wrote {outPath} ({w * zoom}x{h * zoom}) from {inPath} region ({x},{y},{w},{h})");
        return 0;
    }

    public static int DumpEdges(string datDir, string idText)
    {
        if (!Directory.Exists(datDir)) { Console.Error.WriteLine($"error: dir not found: {datDir}"); return 2; }
        uint id = ParseHex(idText);
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var rs = dats.Get<RenderSurface>(id);
        if (rs is null) { Console.Error.WriteLine($"no RenderSurface 0x{id:X8}"); return 1; }
        var pal = rs.DefaultPaletteId != 0 ? dats.Get<Palette>(rs.DefaultPaletteId) : null;
        var dec = SurfaceDecoder.DecodeRenderSurface(rs, pal);
        Console.WriteLine($"0x{id:X8} {rs.Format} {dec.Width}x{dec.Height}");
        int[] cols = { 0, 1, 2, 3, dec.Width - 4, dec.Width - 3, dec.Width - 2, dec.Width - 1 };
        foreach (int cx in cols)
        {
            if (cx < 0 || cx >= dec.Width) continue;
            var sb = new System.Text.StringBuilder();
            for (int y = 0; y < dec.Height; y++)
            {
                int i = (y * dec.Width + cx) * 4;
                sb.Append($"{dec.Rgba8[i]:X2}{dec.Rgba8[i + 1]:X2}{dec.Rgba8[i + 2]:X2} ");
            }
            Console.WriteLine($"x={cx,3}: {sb}");
        }
        return 0;
    }

    public static int ExportSprite(string datDir, string idText, string outPath)
    {
        if (!Directory.Exists(datDir)) { Console.Error.WriteLine($"error: dir not found: {datDir}"); return 2; }
        uint id = ParseHex(idText);
        if (id == 0) { Console.Error.WriteLine($"error: bad id '{idText}'"); return 2; }
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var img = Load(dats, id);
        img.SaveAsPng(outPath);
        Console.WriteLine($"wrote {outPath} (0x{id:X8} {img.Width}x{img.Height})");
        return 0;
    }

    private static Image<Rgba32> Load(DatCollection dats, uint id)
    {
        var rs = dats.Get<RenderSurface>(id);
        if (rs is null) { Console.Error.WriteLine($"  missing RenderSurface 0x{id:X8}"); return new Image<Rgba32>(1, 1); }
        var dt = SurfaceDecoder.DecodeRenderSurface(rs);
        return Image.LoadPixelData<Rgba32>(dt.Rgba8, dt.Width, dt.Height);
    }

    private static uint ParseHex(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0u;
    }
}
