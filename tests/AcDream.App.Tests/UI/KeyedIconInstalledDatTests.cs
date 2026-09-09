using AcDream.App.Rendering;
using AcDream.App.Tests.Rendering;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using AcDream.Content;
using AcDream.Core.Items;
using DatReaderWriter;
using DatReaderWriter.Options;
using Xunit;

namespace AcDream.App.Tests.UI;

[Trait("Lane", "InstalledDat")]
public sealed class KeyedIconInstalledDatTests
{
    private const uint MossTankShelfIconId = 0x06002C41u;

    [Fact]
    public void KeyedIcon_ReplacesTheArtsPureWhiteKey_AndResolveDidUsesIt()
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

        Assert.True(icons.TryDecodeRaw(MossTankShelfIconId, out byte[] raw, out int rw, out int rh));
        Assert.True(
            CountPureWhite(raw) > 0,
            "the raw art must carry pure-white keyed pixels, or this pin proves nothing");

        Assert.True(icons.TryGetKeyedIconRgba(MossTankShelfIconId, out byte[] keyed, out int kw, out int kh));
        Assert.Equal((rw, rh), (kw, kh));
        Assert.Equal(0, CountPureWhite(keyed));

        var resolver = new RetailMarkupIconResolver(adapter, icons, new ClientObjectTable());
        (uint tex, int w, int h) = resolver.ResolveDid(MossTankShelfIconId);
        Assert.Equal(icons.GetKeyedIcon(MossTankShelfIconId), (tex, w, h));
        Assert.NotEqual(0u, tex);
    }

    private static int CountPureWhite(byte[] rgba)
    {
        int count = 0;
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i] == 255 && rgba[i + 1] == 255 && rgba[i + 2] == 255 && rgba[i + 3] == 255)
                count++;
        }
        return count;
    }
}
