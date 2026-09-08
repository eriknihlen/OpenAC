using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using AcDream.Content;
using AcDream.Core.Items;
using AcDream.Plugin.Abstractions;
using DatReaderWriter;
using DatReaderWriter.Options;
using Xunit;

namespace AcDream.App.Tests.UI;

[Trait("Lane", "InstalledDat")]
public sealed class RetailMarkupIconResolverInstalledDatTests
{
    private const uint DecalHabitDoubleNormalizedId = 0x0C000165u;

    private const uint KnownRealDid = 0x06000165u;

    [Fact]
    public void ResolveDid_UnresolvableId_ReturnsNothing_AndKnownRealId_ReturnsATexture()
    {
        string? datDir = InstalledDatTestPath.Resolve();
        if (datDir is null)
        {
            Assert.Fail(
                "Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
            return;
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        var device = new RecordingGpuDevice();
        using var cache = new TextureCache(device, adapter);
        var icons = new IconComposer(adapter, cache);
        var objects = new ClientObjectTable();
        var resolver = new RetailMarkupIconResolver(adapter, icons, objects);

        (uint missTex, int missW, int missH) = resolver.ResolveDid(DecalHabitDoubleNormalizedId);
        Assert.Equal((0u, 0, 0), (missTex, missW, missH));

        (uint realTex, int realW, int realH) = resolver.ResolveDid(KnownRealDid);
        Assert.NotEqual(0u, realTex);
        Assert.True(realW > 0 && realH > 0);
    }

    [Fact]
    public void ShelfButton_BogusDescriptorId_OnTheRealResolver_FallsBackToInitials()
    {
        string? datDir = InstalledDatTestPath.Resolve();
        if (datDir is null)
        {
            Assert.Fail(
                "Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
            return;
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        var device = new RecordingGpuDevice();
        using var cache = new TextureCache(device, adapter);
        var icons = new IconComposer(adapter, cache);
        var objects = new ClientObjectTable();
        var resolver = new RetailMarkupIconResolver(adapter, icons, objects);

        var root = new UiRoot { Width = 800f, Height = 600f };
        using var shelf = new PluginSidePanel(
            root.WindowManager,
            resolver.ResolveDid,
            font: null);
        root.AddChild(shelf);

        var frame = new UiPanel { Width = 200f, Height = 100f };
        root.AddChild(frame);
        RetailWindowHandle handle = root.WindowManager.Register(
            "plugin:acdream.test:main", frame);
        shelf.Add(
            new PluginUiOwner("acdream.test", "Test Plugin"),
            new PluginPanelDescriptor("main", "Test Plugin")
            {
                IconText = "TP",
                IconSurfaceId = DecalHabitDoubleNormalizedId,
            },
            handle);

        PluginSidePanel.PluginShelfButton button = Assert.Single(
            shelf.Children.OfType<PluginSidePanel.PluginShelfButton>());
        Assert.Equal(string.Empty, button.Text);

        var textRenderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        textRenderer.Begin(new Vector2(200f, 200f));
        var ctx = new UiRenderContext(textRenderer, new Vector2(200f, 200f));
        button.DrawSelfAndChildren(ctx);

        Assert.Equal("TP", button.Text);
    }

    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }
}
