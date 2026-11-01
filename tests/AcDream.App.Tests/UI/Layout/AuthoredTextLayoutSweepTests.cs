using System.IO;
using System.Linq;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using DatReaderWriter;
using Font = DatReaderWriter.DBObjs.Font;
using LayoutDesc = DatReaderWriter.DBObjs.LayoutDesc;
using DatReaderWriter.Options;
using DatReaderWriter.Types;
using Xunit.Abstractions;

namespace AcDream.App.Tests.UI.Layout;

/// <summary>
/// Every installed layout: an element's own string that is taller than its
/// box shows its first line, and one that is not one line stays inside its
/// width.
/// </summary>
[Trait("Lane", "InstalledDat")]
public sealed class AuthoredTextLayoutSweepTests(ITestOutputHelper output)
{
    private static string DatDirectory =>
        Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents",
            "Asheron's Call");

    [InstalledDatFact]
    public void EveryAuthoredTextLaysOutInsideItsBoxFromItsFirstLine()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        var fonts = new Dictionary<uint, UiDatFont?>();
        UiDatFont? ResolveFont(uint id)
        {
            if (fonts.TryGetValue(id, out UiDatFont? cached))
                return cached;
            UiDatFont? font = null;
            if (dats.TryGet<Font>(id, out var dat) && dat is not null)
            {
                var glyphs = new Dictionary<char, FontCharDesc>(dat.CharDescs.Count);
                foreach (FontCharDesc glyph in dat.CharDescs)
                    glyphs[(char)glyph.Unicode] = glyph;
                font = new UiDatFont(
                    0, 0, 0, 0, 0, 0, dat.MaxCharHeight, dat.BaselineOffset, glyphs);
            }
            fonts[id] = font;
            return font;
        }

        var renderer = new TextRenderer(new RecordingGpuDevice(), new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(1600f, 1200f));
        var ctx = new UiRenderContext(renderer, new Vector2(1600f, 1200f));

        int texts = 0;
        var wrapped = new List<string>();
        var overflowing = new List<string>();
        foreach (uint layoutId in dats.GetAllIdsOfType<LayoutDesc>().Order())
        {
            ImportedLayout? layout = LayoutImporter.Import(
                dats, layoutId, _ => (0u, 0, 0),
                ResolveFont(UiDatFont.DefaultFontId), ResolveFont);
            if (layout is null)
                continue;
            var stack = new Stack<UiElement>();
            stack.Push(layout.Root);
            while (stack.Count > 0)
            {
                UiElement element = stack.Pop();
                foreach (UiElement child in element.Children)
                    stack.Push(child);
                if (element is not UiText { OneLine: false } text
                    || text.DatFont is not { } font)
                    continue;
                IReadOnlyList<UiText.Line> lines = text.LinesProvider();
                if (lines.Count == 0)
                    continue;
                texts++;
                string where = $"0x{layoutId:X8} 0x{text.ElementId:X8}";

                float room = text.Width - text.MarginLeft - text.MarginRight;
                foreach (UiText.Line line in lines)
                    Assert.True(
                        font.MeasureWidth(line.Text) <= room || !line.Text.Contains(' '),
                        $"{where}: \"{line.Text}\" is wider than {room} px");
                if (lines.Count > 1)
                    wrapped.Add($"{where} ({text.Width}x{text.Height}): "
                        + string.Join(" | ", lines.Select(static line => line.Text)));

                text.Visible = true;
                text.DrawSelfAndChildren(ctx);
                if (text.Scroll.MaxScroll > 0)
                {
                    overflowing.Add(where);
                    Assert.True(
                        text.Scroll.ScrollY == 0,
                        $"{where} shows \"{string.Join(" | ", lines.Select(static l => l.Text))}\" from its end");
                }
            }
        }

        output.WriteLine($"{texts} authored texts; {wrapped.Count} on more than one line, "
            + $"{overflowing.Count} taller than their box");
        foreach (string line in wrapped)
            output.WriteLine(line);
        // The squelch page's 100-pixel "Character Name:" label wraps past its
        // 18-pixel box and shows "Character"; the profession page's help box
        // holds its sentence on two lines.
        Assert.Contains("0x21000060 0x1000053F", overflowing);
        Assert.Contains(wrapped, line => line.StartsWith("0x21000038 0x100003EC", StringComparison.Ordinal)
            && line.Count(static c => c == '|') == 1);
    }

    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }
}
