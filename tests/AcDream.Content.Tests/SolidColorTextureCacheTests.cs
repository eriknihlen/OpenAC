using AcDream.Content;
using DatReaderWriter;
using DatReaderWriter.Options;
using DatReaderWriter.Types;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AcDream.Content.Tests;

[Trait("Lane", "InstalledDat")]
public sealed class SolidColorTextureCacheTests {
    [Fact]
    public void GetOrCreateSolidColorTexture_SameColor_ReturnsSameSharedArray() {
        var datDir = ContentConformanceDats.ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        var extractor = new MeshExtractor(adapter, NullLogger.Instance, sideStagedSink: null);

        var color = new ColorARGB { Alpha = 255, Red = 10, Green = 20, Blue = 30 };

        var first = extractor.GetOrCreateSolidColorTexture(color, 32, 32);
        var second = extractor.GetOrCreateSolidColorTexture(color, 32, 32);

        Assert.Same(first, second);
        Assert.Equal(32 * 32 * 4, first.Length);
        Assert.Equal(10, first[0]);
        Assert.Equal(20, first[1]);
        Assert.Equal(30, first[2]);
        Assert.Equal(255, first[3]);
    }

    [Fact]
    public void GetOrCreateSolidColorTexture_DifferentColors_ReturnDistinctArrays() {
        var datDir = ContentConformanceDats.ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        var extractor = new MeshExtractor(adapter, NullLogger.Instance, sideStagedSink: null);

        var red = new ColorARGB { Alpha = 255, Red = 255, Green = 0, Blue = 0 };
        var blue = new ColorARGB { Alpha = 255, Red = 0, Green = 0, Blue = 255 };

        var redTexture = extractor.GetOrCreateSolidColorTexture(red, 32, 32);
        var blueTexture = extractor.GetOrCreateSolidColorTexture(blue, 32, 32);

        Assert.NotSame(redTexture, blueTexture);
        Assert.Equal(255, redTexture[0]);
        Assert.Equal(0, redTexture[2]);
        Assert.Equal(0, blueTexture[0]);
        Assert.Equal(255, blueTexture[2]);
    }
}
