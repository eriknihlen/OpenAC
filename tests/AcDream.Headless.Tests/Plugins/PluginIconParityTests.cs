using AcDream.Core.Plugins;
using AcDream.Launcher.Core.Plugins;
using AcDream.Tests.Fixtures.PluginIcons;

namespace AcDream.Headless.Tests.Plugins;

/// <summary>Both sides read the same header rules for icon.png, but neither may reference the
/// other's project (L-303, L-317); this runs the shared corpus through both and requires the
/// same verdict.</summary>
public sealed class PluginIconParityTests
{
    public static TheoryData<string, byte[]> Cases()
    {
        var data = new TheoryData<string, byte[]>
        {
            { "valid", PngTestData.Valid() },
            { "over maximum bytes", PngTestData.OverMaximumBytes() },
            { "no signature", PngTestData.NoSignature() },
            { "first chunk not IHDR", PngTestData.FirstChunkWrongType() },
            { "IHDR wrong length", PngTestData.FirstChunkWrongLength() },
            { "wrong dimensions", PngTestData.WrongDimensions() },
            { "chunk length above int.MaxValue", PngTestData.ChunkLengthOverflow() },
            { "chunk runs past end", PngTestData.ChunkRunsPastEnd() },
            { "acTL present", PngTestData.AnimatedControl() },
            { "missing IEND", PngTestData.MissingIend() },
            { "trailing bytes after IEND", PngTestData.TrailingBytesAfterIend() },
            { "inflate bomb", PngTestData.InflateBomb() },
            { "header outside spec: bit depth 255", PngTestData.WithHeader(255, 6, 16) },
            { "header outside spec: color type 3 at depth 16", PngTestData.WithHeader(16, 3, 16) },
            { "header outside spec: color type 2 at depth 1", PngTestData.WithHeader(1, 2, 16) },
            { "header outside spec: color type 5", PngTestData.WithHeader(8, 5, 16) },
            { "header outside spec: compression method 1", PngTestData.WithHeader(8, 6, 16, compression: 1) },
            { "header outside spec: filter method 1", PngTestData.WithHeader(8, 6, 16, filter: 1) },
            { "header outside spec: interlace method 2", PngTestData.WithHeader(8, 6, 16, interlace: 2) },
            { "allowed depth: 1-bit grayscale", PngTestData.WithHeader(1, 0, 64 * 9) },
            { "allowed depth: 16-bit truecolor+alpha", PngTestData.WithHeader(16, 6, 64 * 513) },
            { "allowed depth: 8-bit palette", PngTestData.WithHeader(8, 3, 64 * 65) },
            { "interlaced at its exact raw size", PngTestData.WithHeader(8, 6, 16504, interlace: 1) },
            { "interlaced one byte over its raw size", PngTestData.WithHeader(8, 6, 16505, interlace: 1) },
        };
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void LauncherAndClientAgreeOnEveryRule(string _, byte[] bytes)
    {
        bool launcherAccepts = Record.Exception(() => LauncherPluginIcon.Validate(bytes)) is null;
        bool clientAccepts = Record.Exception(() => PluginIconFile.ValidateHeader(bytes)) is null;

        Assert.Equal(launcherAccepts, clientAccepts);
    }
}
