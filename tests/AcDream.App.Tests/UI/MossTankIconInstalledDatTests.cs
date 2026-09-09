using AcDream.App.Rendering;
using AcDream.App.Tests.Rendering;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using AcDream.Content;
using AcDream.Core.Items;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using Xunit;

namespace AcDream.App.Tests.UI;

[Trait("Lane", "InstalledDat")]
public sealed class MossTankIconInstalledDatTests
{
    /// <summary>The id <c>MossTankPlugin.cs</c> sets as its shelf descriptor's
    /// <c>IconSurfaceId</c>.</summary>
    private const uint MossTankShelfIconId = 0x06002C41u;

    [Fact]
    public void MossTankShelfIconId_IsARealInstalledRenderSurface()
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

        bool existsInPortal = adapter.Portal.TryGet<RenderSurface>(MossTankShelfIconId, out _);
        bool existsInHighRes = adapter.HighRes.TryGet<RenderSurface>(MossTankShelfIconId, out _);
        Assert.True(
            existsInPortal || existsInHighRes,
            $"expected 0x{MossTankShelfIconId:X8} to be a real installed RenderSurface (Portal or HighRes)");

        var device = new RecordingGpuDevice();
        using var cache = new TextureCache(device, adapter);
        var icons = new IconComposer(adapter, cache);
        var objects = new ClientObjectTable();
        var resolver = new RetailMarkupIconResolver(adapter, icons, objects);

        (uint tex, int w, int h) = resolver.ResolveDid(MossTankShelfIconId);
        Assert.NotEqual(0u, tex);
        Assert.True(w > 0 && h > 0);
    }
}
