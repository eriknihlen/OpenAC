using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using AcDream.Content;
using AcDream.Plugin.Abstractions;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using DatReaderWriter.Types;
using SysEnv = System.Environment;

namespace AcDream.App.Tests.UI;

public sealed class PluginSidePanelToggleGlyphClipTests
{
    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }

    private static string? ResolveDatDir()
    {
        string? fromEnv = SysEnv.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv))
            return fromEnv;

        string defaultDir = Path.Combine(
            SysEnv.GetFolderPath(SysEnv.SpecialFolder.UserProfile),
            "Documents", "Asheron's Call");
        return Directory.Exists(defaultDir) ? defaultDir : null;
    }

    /// <summary>Decodes (x0,y0)/(x1,y1) of every quad (6 verts / 8 floats each)
    /// in a recorded sprite segment — same 8-floats-per-vertex layout every
    /// other draw-level test in this tree decodes
    /// (<c>UiRenderContextDrawStringDatOutlineTests.DecodeQuads</c>).</summary>
    private static IEnumerable<(float y0, float y1)> DecodeVerticalSpans(IReadOnlyList<float> verts)
    {
        const int floatsPerVertex = 8;
        const int floatsPerQuad = floatsPerVertex * 6;
        for (int i = 0; i + floatsPerQuad <= verts.Count; i += floatsPerQuad)
        {
            float y0 = verts[i + 1];
            float y1 = verts[i + 8 + 1];
            yield return (y0, y1);
        }
    }

    private static (float top, float bottom) ComputeUnclippedInkSpan(
        UiDatFont font, char glyph, float bandHeight)
    {
        Assert.True(font.TryGetGlyph(glyph, out FontCharDesc g), $"font is missing glyph '{glyph}'");

        float y = (bandHeight - font.LineHeight) * 0.5f;
        float baseY = MathF.Floor(y + 0.5f);
        float gy = baseY + g.VerticalOffsetBefore;
        float gh = g.Height;

        float top = gy;
        float bottom = gy + gh;
        if (font.HasBackground)
        {
            float iy = gy - font.BorderY;
            float ih = gh + 2f * font.BorderY;
            top = MathF.Min(top, iy);
            bottom = MathF.Max(bottom, iy + ih);
        }
        return (top, bottom);
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void ToggleGlyph_InkFullyFitsInsideTheGripBand_ExpandedAndCollapsed()
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

        var root = new UiRoot { Width = 800f, Height = 600f };
        using var shelf = new PluginSidePanel(root.WindowManager, _ => (0u, 0, 0), font);
        root.AddChild(shelf);
        root.WindowManager.Register(WindowNames.PluginShelf, shelf, shelf, controller: shelf);

        var frame = new UiPanel { Width = 200f, Height = 100f };
        root.AddChild(frame);
        RetailWindowHandle pluginHandle = root.WindowManager.Register(
            "plugin:acdream.test:main", frame);
        shelf.Add(
            new PluginUiOwner("acdream.test", "Test Plugin"),
            new PluginPanelDescriptor("main", "Test Plugin"),
            pluginHandle);

        root.Tick(0.016d, 16L);

        UiSimpleButton toggle = Assert.Single(
            shelf.Children,
            c => c is UiSimpleButton && c is not PluginSidePanel.PluginShelfButton) as UiSimpleButton
            ?? throw new InvalidOperationException("toggle button not found");

        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        var ctx = new UiRenderContext(renderer, new Vector2(800f, 600f));

        // ── Expanded state ('>') ────────────────────────────────────────
        Assert.False(shelf.CaptureWindowState().Collapsed);
        AssertGlyphInkFullyContained(font!, '>', toggle, renderer, ctx);

        int toggleX = (int)shelf.Left + (int)shelf.Width - 8;
        int toggleY = (int)shelf.Top + 4;
        root.OnMouseDown(UiMouseButton.Left, toggleX, toggleY);
        root.OnMouseUp(UiMouseButton.Left, toggleX, toggleY);
        Assert.True(shelf.CaptureWindowState().Collapsed);
        root.Tick(0.016d, 16L);

        AssertGlyphInkFullyContained(font!, '<', toggle, renderer, ctx);
    }

    private static void AssertGlyphInkFullyContained(
        UiDatFont font, char glyph, UiSimpleButton toggle, TextRenderer renderer, UiRenderContext ctx)
    {
        (float expectedTop, float expectedBottom) = ComputeUnclippedInkSpan(font, glyph, toggle.Height);

        Assert.True(
            expectedTop >= -0.01f && expectedBottom <= toggle.Height + 0.01f,
            $"'{glyph}': computed unclipped ink span [{expectedTop},{expectedBottom}] " +
            $"does not fit inside the {toggle.Height}px band — the band is too short for this font's metrics " +
            $"(LineHeight={font.LineHeight}, BorderY={font.BorderY}).");

        renderer.Begin(new Vector2(800f, 600f));
        toggle.DrawSelfAndChildren(ctx);

        float observedMinY = float.MaxValue, observedMaxY = float.MinValue;
        int quadCount = 0;
        foreach (var seg in renderer.DebugSpriteSegmentVerts)
        {
            foreach ((float y0, float y1) in DecodeVerticalSpans(seg.Verts))
            {
                observedMinY = MathF.Min(observedMinY, MathF.Min(y0, y1));
                observedMaxY = MathF.Max(observedMaxY, MathF.Max(y0, y1));
                quadCount++;
            }
        }

        Assert.True(quadCount > 0, $"toggle drew no glyph quads at all for '{glyph}'");

        Assert.Equal(expectedTop, observedMinY, 1);
        Assert.Equal(expectedBottom, observedMaxY, 1);

        Assert.True(observedMinY >= -0.01f, $"'{glyph}': ink top ({observedMinY}) is above the band (0)");
        Assert.True(
            observedMaxY <= toggle.Height + 0.01f,
            $"'{glyph}': ink bottom ({observedMaxY}) overflows the {toggle.Height}px band");
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void CollapsedTab_IsButtonSized_ForFindability()
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

        var root = new UiRoot { Width = 800f, Height = 600f };
        using var shelf = new PluginSidePanel(root.WindowManager, _ => (0u, 0, 0), font);
        root.AddChild(shelf);
        root.WindowManager.Register(WindowNames.PluginShelf, shelf, shelf, controller: shelf);

        var frame = new UiPanel { Width = 200f, Height = 100f };
        root.AddChild(frame);
        RetailWindowHandle pluginHandle = root.WindowManager.Register(
            "plugin:acdream.test:main", frame);
        shelf.Add(
            new PluginUiOwner("acdream.test", "Test Plugin"),
            new PluginPanelDescriptor("main", "Test Plugin"),
            pluginHandle);
        root.Tick(0.016d, 16L);

        float expandedHeight = shelf.Height;

        shelf.RestoreWindowState(new RetainedWindowState(Collapsed: true));

        Assert.True(shelf.Height > 20f, $"collapsed shelf height ({shelf.Height}) is not button-sized");
        Assert.NotEqual(expandedHeight, shelf.Height);
    }

    [Fact]
    public void Collapse_AfterADraw_RepositionsGripAndToggle_NotFrozenAtExpandedGeometry()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        using var shelf = new PluginSidePanel(root.WindowManager, _ => (0u, 0, 0), font: null);
        root.AddChild(shelf);

        for (int i = 0; i < 2; i++)
        {
            var frame = new UiPanel { Width = 200f, Height = 100f };
            root.AddChild(frame);
            RetailWindowHandle handle = root.WindowManager.Register(
                $"plugin:acdream.test:{i}", frame);
            shelf.Add(
                new PluginUiOwner($"acdream.test.{i}", $"Test Plugin {i}"),
                new PluginPanelDescriptor("main", $"Test Plugin {i}"),
                handle);
        }

        root.Tick(0.016d, 16L);

        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        var ctx = new UiRenderContext(renderer, new Vector2(800f, 600f));

        renderer.Begin(new Vector2(800f, 600f));
        root.Draw(ctx);

        UiSimpleButton toggle = Assert.Single(
            shelf.Children,
            c => c is UiSimpleButton && c is not PluginSidePanel.PluginShelfButton) as UiSimpleButton
            ?? throw new InvalidOperationException("toggle button not found");
        UiElement grip = Assert.Single(
            shelf.Children,
            c => c is UiPanel && c is not UiSimpleButton);

        float expandedShelfHeight = shelf.Height;
        float expandedBandHeight = shelf.ExpandedGripBandHeight;
        float toggleWidth = toggle.Width;

        Assert.Equal(0f, grip.Left);
        Assert.Equal(0f, grip.Top);
        Assert.Equal(shelf.Width - toggleWidth, grip.Width, 3);
        Assert.Equal(expandedBandHeight, grip.Height, 3);
        Assert.Equal(shelf.Width - toggleWidth, toggle.Left, 3);
        Assert.Equal(0f, toggle.Top);
        Assert.Equal(expandedBandHeight, toggle.Height, 3);

        int toggleX = (int)shelf.Left + (int)shelf.Width - 8;
        int toggleY = (int)shelf.Top + 4;
        root.OnMouseDown(UiMouseButton.Left, toggleX, toggleY);
        root.OnMouseUp(UiMouseButton.Left, toggleX, toggleY);
        Assert.True(shelf.CaptureWindowState().Collapsed);

        renderer.Begin(new Vector2(800f, 600f));
        root.Draw(ctx);

        Assert.Equal(0f, grip.Left);
        Assert.Equal(0f, grip.Top);
        Assert.Equal(shelf.Width - toggleWidth, grip.Width, 3);
        Assert.Equal(shelf.Height, grip.Height, 3);
        Assert.Equal(shelf.Width - toggleWidth, toggle.Left, 3);
        Assert.Equal(0f, toggle.Top);
        Assert.Equal(shelf.Height, toggle.Height, 3);
        Assert.True(
            toggle.Left + toggle.Width <= shelf.Width + 0.01f,
            $"toggle (Left={toggle.Left}, Width={toggle.Width}) lies outside " +
            $"the collapsed shelf (Width={shelf.Width}) — the ancestor clip " +
            "would remove it, matching the owner's \"no <\" report.");

        int toggleX2 = (int)shelf.Left + (int)shelf.Width - 8;
        int toggleY2 = (int)shelf.Top + 4;
        root.OnMouseDown(UiMouseButton.Left, toggleX2, toggleY2);
        root.OnMouseUp(UiMouseButton.Left, toggleX2, toggleY2);
        Assert.False(shelf.CaptureWindowState().Collapsed);

        renderer.Begin(new Vector2(800f, 600f));
        root.Draw(ctx);

        Assert.Equal(expandedShelfHeight, shelf.Height, 3);
        Assert.Equal(0f, grip.Left);
        Assert.Equal(0f, grip.Top);
        Assert.Equal(shelf.Width - toggleWidth, grip.Width, 3);
        Assert.Equal(expandedBandHeight, grip.Height, 3);
        Assert.Equal(shelf.Width - toggleWidth, toggle.Left, 3);
        Assert.Equal(0f, toggle.Top);
        Assert.Equal(expandedBandHeight, toggle.Height, 3);
    }

    [Fact]
    public void MultiColumnReflow_AfterADraw_EveryRemainingButtonMatchesAFreshSinglePassLayout()
    {
        const int entryCount = 12;

        var root = new UiRoot { Width = 800f, Height = 260f };
        using var shelf = new PluginSidePanel(root.WindowManager, _ => (0u, 0, 0), font: null);
        root.AddChild(shelf);

        var handles = new RetailWindowHandle[entryCount];
        for (int i = 0; i < entryCount; i++)
        {
            var frame = new UiPanel { Width = 200f, Height = 100f };
            root.AddChild(frame);
            handles[i] = root.WindowManager.Register($"plugin:acdream.test:{i}", frame);
            shelf.Add(
                new PluginUiOwner($"acdream.test.{i}", $"Test Plugin {i}"),
                new PluginPanelDescriptor("main", $"Test Plugin {i}"),
                handles[i]);
        }

        root.Tick(0.016d, 16L);

        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        var ctx = new UiRenderContext(renderer, new Vector2(800f, 600f));

        renderer.Begin(new Vector2(800f, 600f));
        root.Draw(ctx);

        // ── Remove the FIRST entry — every remaining button shifts down one
        // slot, so this exercises the widest possible reflow. ─────────────
        root.WindowManager.Unregister(handles[0].Name);
        Assert.Equal(entryCount - 1, shelf.EntryCount);

        // ── Second draw: must reflect the fresh 11-entry reflow. Pre-fix,
        // each surviving button stays pinned at its ORIGINAL 12-entry
        // position/size instead. ───────────────────────────────────────────
        renderer.Begin(new Vector2(800f, 600f));
        root.Draw(ctx);

        List<PluginSidePanel.PluginShelfButton> afterRemoval =
            shelf.Children.OfType<PluginSidePanel.PluginShelfButton>().ToList();
        Assert.Equal(entryCount - 1, afterRemoval.Count);

        // ── Reference: an independent shelf built directly with the SAME
        // final 11-entry set (never having been through a 12-entry layout),
        // ticked and drawn exactly once. ───────────────────────────────────
        var referenceRoot = new UiRoot { Width = 800f, Height = 260f };
        using var referenceShelf = new PluginSidePanel(
            referenceRoot.WindowManager, _ => (0u, 0, 0), font: null);
        referenceRoot.AddChild(referenceShelf);
        for (int i = 1; i < entryCount; i++)
        {
            var frame = new UiPanel { Width = 200f, Height = 100f };
            referenceRoot.AddChild(frame);
            RetailWindowHandle handle = referenceRoot.WindowManager.Register(
                $"plugin:acdream.reference:{i}", frame);
            referenceShelf.Add(
                new PluginUiOwner($"acdream.reference.{i}", $"Test Plugin {i}"),
                new PluginPanelDescriptor("main", $"Test Plugin {i}"),
                handle);
        }
        referenceRoot.Tick(0.016d, 16L);
        var referenceDevice = new RecordingGpuDevice();
        var referenceRenderer = new TextRenderer(referenceDevice, new NullGpuFrameSource(), "unused");
        var referenceCtx = new UiRenderContext(referenceRenderer, new Vector2(800f, 600f));
        referenceRenderer.Begin(new Vector2(800f, 600f));
        referenceRoot.Draw(referenceCtx);

        List<PluginSidePanel.PluginShelfButton> reference =
            referenceShelf.Children.OfType<PluginSidePanel.PluginShelfButton>().ToList();
        Assert.Equal(reference.Count, afterRemoval.Count);

        for (int i = 0; i < afterRemoval.Count; i++)
        {
            Assert.Equal(reference[i].Left, afterRemoval[i].Left, 3);
            Assert.Equal(reference[i].Top, afterRemoval[i].Top, 3);
            Assert.Equal(reference[i].Width, afterRemoval[i].Width, 3);
            Assert.Equal(reference[i].Height, afterRemoval[i].Height, 3);
        }

        Assert.Equal(shelf.Width, referenceShelf.Width, 3);
        Assert.Equal(shelf.Height, referenceShelf.Height, 3);
    }
}
