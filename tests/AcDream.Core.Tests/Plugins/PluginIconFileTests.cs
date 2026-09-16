using AcDream.Core.Plugins;
using AcDream.Core.Textures;
using AcDream.Tests.Fixtures.PluginIcons;

namespace AcDream.Core.Tests.Plugins;

public sealed class PluginIconFileTests
{
    [Fact]
    public void LoadsAValidIcon()
    {
        using var directory = new TemporaryDirectory();
        WriteIcon(directory.Path, PngTestData.Valid());

        Assert.True(PluginIconFile.TryLoad(directory.Path, out DecodedTexture? texture));
        Assert.Equal(64, texture.Width);
        Assert.Equal(64, texture.Height);
        Assert.Equal(64 * 64 * 4, texture.Rgba8.Length);
    }

    [Fact]
    public void FailsWithNoIconFile()
    {
        using var directory = new TemporaryDirectory();

        Assert.False(PluginIconFile.TryLoad(directory.Path, out _));
    }

    [Fact]
    public void FailsWhenOverMaximumBytes()
    {
        using var directory = new TemporaryDirectory();
        WriteIcon(directory.Path, PngTestData.OverMaximumBytes());

        Assert.False(PluginIconFile.TryLoad(directory.Path, out _));
    }

    [Fact]
    public void FailsWithTheWrongDimensions()
    {
        using var directory = new TemporaryDirectory();
        WriteIcon(directory.Path, PngTestData.WrongDimensions());

        Assert.False(PluginIconFile.TryLoad(directory.Path, out _));
    }

    [Fact]
    public void FailsWhenAnimated()
    {
        using var directory = new TemporaryDirectory();
        WriteIcon(directory.Path, PngTestData.AnimatedControl());

        Assert.False(PluginIconFile.TryLoad(directory.Path, out _));
    }

    [Fact]
    public void FailsWithBytesAfterIend()
    {
        using var directory = new TemporaryDirectory();
        WriteIcon(directory.Path, PngTestData.TrailingBytesAfterIend());

        Assert.False(PluginIconFile.TryLoad(directory.Path, out _));
    }

    [Fact]
    public void FailsWithCorruptPixelDataThatPassesTheHeaderRules()
    {
        using var directory = new TemporaryDirectory();
        byte[] bytes = PngTestData.CorruptPixelData();
        PluginIconFile.ValidateHeader(bytes);
        WriteIcon(directory.Path, bytes);

        Assert.False(PluginIconFile.TryLoad(directory.Path, out _));
    }

    [Fact]
    public void RefusesAnInflateBombWithoutAllocatingItsExpandedSize()
    {
        using var directory = new TemporaryDirectory();
        WriteIcon(directory.Path, PngTestData.InflateBomb());

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool loaded = PluginIconFile.TryLoad(directory.Path, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.False(loaded);
        Assert.True(
            allocated < 1_000_000,
            $"expected well under the bomb's 4,000,000-byte expansion, allocated {allocated}");
    }

    private static void WriteIcon(string pluginDirectory, byte[] bytes) =>
        File.WriteAllBytes(Path.Combine(pluginDirectory, PluginIconFile.FileName), bytes);

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"acdream-plugin-icon-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
