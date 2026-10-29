using AcDream.Core.Plugins;
using AcDream.Core.Textures;
using AcDream.Tests.Fixtures.PluginIcons;

namespace AcDream.Core.Tests.Plugins;

public sealed class PluginImageFileTests
{
    [Theory]
    [InlineData("icons/sword.png")]
    [InlineData(@"icons\sword.png")]
    [InlineData("icons/shield.JPG")]
    [InlineData("icons/helm.jpeg")]
    public void ResolvePathAcceptsAPngOrJpegInsideTheFolder(string relative)
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "icons"));
        string name = Path.GetFileName(relative.Replace('\\', '/'));
        File.WriteAllBytes(Path.Combine(directory.Path, "icons", name), [1]);

        Assert.True(PluginImageFile.TryResolvePath(directory.Path, relative, out string? full, out string? reason), reason);
        Assert.EndsWith(name, full, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../outside.png", "points outside")]
    [InlineData("/etc/icon.png", "points outside")]
    [InlineData("icons/loot.svg", "not a .png or .jpg")]
    [InlineData("icons/loot.gif", "not a .png or .jpg")]
    [InlineData("missing.png", "does not exist")]
    public void ResolvePathRejectsAnythingElse(string relative, string named)
    {
        using var outer = new TemporaryDirectory();
        string folder = Path.Combine(outer.Path, "plugin");
        Directory.CreateDirectory(Path.Combine(folder, "icons"));
        File.WriteAllBytes(Path.Combine(outer.Path, "outside.png"), PngTestData.Sized(4, 4));
        File.WriteAllBytes(Path.Combine(folder, "icons", "loot.svg"), [1]);
        File.WriteAllBytes(Path.Combine(folder, "icons", "loot.gif"), [1]);

        Assert.False(PluginImageFile.TryResolvePath(folder, relative, out _, out string? reason));
        Assert.Contains(named, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadsAPngOfAnySizeUpToTheLimit()
    {
        using var directory = new TemporaryDirectory();
        string path = Write(directory, "wide.png", PngTestData.Sized(48, 16));

        Assert.True(PluginImageFile.TryLoad(path, out DecodedTexture? texture, out string? reason), reason);
        Assert.Equal(48, texture.Width);
        Assert.Equal(16, texture.Height);
        Assert.Equal(48 * 16 * 4, texture.Rgba8.Length);
    }

    [Fact]
    public void LoadsAJpegAsOpaqueRgba()
    {
        using var directory = new TemporaryDirectory();
        string path = Write(directory, "red.jpg", JpegTestData.Red());

        Assert.True(PluginImageFile.TryLoad(path, out DecodedTexture? texture, out string? reason), reason);
        Assert.Equal(JpegTestData.Width, texture.Width);
        Assert.Equal(JpegTestData.Height, texture.Height);
        Assert.Equal(255, texture.Rgba8[3]);
        Assert.True(texture.Rgba8[0] > 150 && texture.Rgba8[1] < 100, "the first pixel should be red");
    }

    [Fact]
    public void RefusesAnImageWiderThanTheLimitBeforeDecodingIt()
    {
        using var directory = new TemporaryDirectory();
        string path = Write(directory, "big.png", PngTestData.Sized(PluginImageFile.MaximumDimension + 1, 1));

        Assert.False(PluginImageFile.TryLoad(path, out _, out string? reason));
        Assert.Contains("512", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAFileLargerThanTheByteLimit()
    {
        using var directory = new TemporaryDirectory();
        string path = Write(directory, "huge.png", new byte[PluginImageFile.MaximumBytes + 1]);

        Assert.False(PluginImageFile.TryLoad(path, out _, out string? reason));
        Assert.Contains("1 MiB", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAFileThatIsNeitherPngNorJpegWhateverItsName()
    {
        using var directory = new TemporaryDirectory();
        string path = Write(directory, "fake.png", "GIF89a not really"u8.ToArray());

        Assert.False(PluginImageFile.TryLoad(path, out _, out string? reason));
        Assert.Contains("not a PNG or JPEG", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAPngWhosePixelsDoNotDecode()
    {
        using var directory = new TemporaryDirectory();
        byte[] png = PngTestData.Sized(4, 4);
        Array.Resize(ref png, png.Length - 20);
        string path = Write(directory, "cut.png", png);

        Assert.False(PluginImageFile.TryLoad(path, out _, out string? reason));
        Assert.Contains("could not be decoded", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAMissingFileWithoutThrowing()
    {
        using var directory = new TemporaryDirectory();

        Assert.False(PluginImageFile.TryLoad(Path.Combine(directory.Path, "gone.png"), out _, out string? reason));
        Assert.Contains("could not be read", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0, (byte)'n', (byte)'o', (byte)'p', (byte)'e', 1, 2, 3, 4 })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 })]
    public void RefusesAnImageWhoseHeaderDoesNotSayItsSize(byte[] bytes)
    {
        using var directory = new TemporaryDirectory();
        string path = Write(directory, "headless.png", bytes);

        Assert.False(PluginImageFile.TryLoad(path, out _, out string? reason));
        Assert.Contains("header that does not say", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAFolderNamedLikeAnImage()
    {
        using var directory = new TemporaryDirectory();
        string path = System.IO.Path.Combine(directory.Path, "folder.png");
        Directory.CreateDirectory(path);

        Assert.False(PluginImageFile.TryLoad(path, out _, out string? reason));
        Assert.Contains("not a regular file", reason, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Lane", "Unix")]
    public async Task RefusesAPipeNamedLikeAnImageWithoutWaitingOnIt()
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Lane=Unix requires a native Unix host.");
        using var directory = new TemporaryDirectory();
        string path = System.IO.Path.Combine(directory.Path, "pipe.png");
        using (var mkfifo = System.Diagnostics.Process.Start("mkfifo", [path]))
        {
            mkfifo.WaitForExit();
            Assert.Equal(0, mkfifo.ExitCode);
        }

        // Nobody writes to the pipe, so a reader that opens it as a file waits forever.
        string? reason = null;
        Task<bool> load = Task.Factory.StartNew(
            () => PluginImageFile.TryLoad(path, out _, out reason),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        try
        {
            Task first = await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(ReferenceEquals(first, load), "reading a pipe should not wait for a writer");
            Assert.False(await load);
            Assert.Contains("not a regular file", reason, StringComparison.Ordinal);
        }
        finally
        {
            if (!load.IsCompleted)
            {
                // Let a reader stuck on the pipe go: a writer opens and closes it.
                using (new FileStream(path, FileMode.Open, FileAccess.Write)) { }
                await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(10)));
            }
        }
    }

    // Links: PluginFilePath follows every link on the way to a file, so none
    // can lead a plugin's markup out of its own folder. Windows needs extra
    // rights to make links, so these run where links can be made freely.

    [Fact]
    [Trait("Lane", "Unix")]
    public void ResolvePathRefusesAFileLinkThatPointsOutsideTheFolder()
    {
        using var outer = LinkTestFolders(out string folder);
        File.CreateSymbolicLink(Path.Combine(folder, "icons", "sword.png"), Path.Combine(outer.Path, "outside.png"));

        Assert.False(PluginImageFile.TryResolvePath(folder, "icons/sword.png", out _, out string? reason));
        Assert.Contains("points outside", reason, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Lane", "Unix")]
    public void ResolvePathRefusesAFolderLinkThatPointsOutsideTheFolder()
    {
        using var outer = LinkTestFolders(out string folder);
        string elsewhere = Path.Combine(outer.Path, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllBytes(Path.Combine(elsewhere, "sword.png"), PngTestData.Sized(4, 4));
        Directory.CreateSymbolicLink(Path.Combine(folder, "linked"), elsewhere);

        Assert.False(PluginImageFile.TryResolvePath(folder, "linked/sword.png", out _, out string? reason));
        Assert.Contains("points outside", reason, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Lane", "Unix")]
    public void ResolvePathFollowsALinkToALinkToWhereItReallyEnds()
    {
        using var outer = LinkTestFolders(out string folder);
        string inner = Path.Combine(folder, "inner.png");
        File.CreateSymbolicLink(inner, Path.Combine(outer.Path, "outside.png"));
        File.CreateSymbolicLink(Path.Combine(folder, "icons", "sword.png"), inner);

        Assert.False(PluginImageFile.TryResolvePath(folder, "icons/sword.png", out _, out string? reason));
        Assert.Contains("points outside", reason, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Lane", "Unix")]
    public void ResolvePathAcceptsALinkThatStaysInsideTheFolder()
    {
        using var outer = LinkTestFolders(out string folder);
        string real = Path.Combine(folder, "art", "sword.png");
        Directory.CreateDirectory(Path.GetDirectoryName(real)!);
        File.WriteAllBytes(real, PngTestData.Sized(4, 4));
        File.CreateSymbolicLink(Path.Combine(folder, "icons", "sword.png"), Path.Combine("..", "art", "sword.png"));

        Assert.True(PluginImageFile.TryResolvePath(folder, "icons/sword.png", out string? full, out string? reason), reason);
        Assert.Equal(Path.GetFileName(real), Path.GetFileName(full));
        Assert.EndsWith(Path.Combine("art", "sword.png"), full, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Lane", "Unix")]
    public void ResolvePathServesAFolderThatIsItselfALink()
    {
        using var outer = LinkTestFolders(out string folder);
        File.WriteAllBytes(Path.Combine(folder, "icons", "sword.png"), PngTestData.Sized(4, 4));
        string alias = Path.Combine(outer.Path, "alias");
        Directory.CreateSymbolicLink(alias, folder);

        Assert.True(PluginImageFile.TryResolvePath(alias, "icons/sword.png", out _, out string? reason), reason);
    }

    /// <summary>A plugin folder with an icons folder, and a PNG beside it, outside it.</summary>
    private static TemporaryDirectory LinkTestFolders(out string folder)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Lane=Unix requires a native Unix host.");
        var outer = new TemporaryDirectory();
        folder = Path.Combine(outer.Path, "plugin");
        Directory.CreateDirectory(Path.Combine(folder, "icons"));
        File.WriteAllBytes(Path.Combine(outer.Path, "outside.png"), PngTestData.Sized(4, 4));
        return outer;
    }

    private static string Write(TemporaryDirectory directory, string name, byte[] bytes)
    {
        string path = Path.Combine(directory.Path, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"acdream-plugin-image-{Guid.NewGuid():N}");
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
