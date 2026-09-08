using System.IO.Compression;
using System.Text;
using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.Core.Tests.Updates;

public sealed class SafeZipExtractorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "acdream-safe-zip-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractsPortableTreeWithHashesAndExecutableMode()
    {
        byte[] archive = UpdateTestData.CreateZip(
        [
            ("bin/", [], 0x41ED),
            ("bin/client", Encoding.UTF8.GetBytes("client"), 0x81ED),
            ("data/value.txt", Encoding.UTF8.GetBytes("value"), 0x81A4),
        ]);
        string zip = WriteArchive("valid.zip", archive);
        string destination = Path.Combine(_root, "valid");

        IReadOnlyList<ExtractedFileRecord> files = await new SafeZipExtractor()
            .ExtractAsync(zip, destination);

        Assert.Equal(["bin/client", "data/value.txt"], files.Select(file => file.Path));
        Assert.Equal(0x1ED, files[0].UnixMode);
        Assert.Equal(UpdateTestData.Sha256(Encoding.UTF8.GetBytes("client")), files[0].Sha256);
        Assert.Equal("value", await File.ReadAllTextAsync(Path.Combine(destination, "data", "value.txt")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/../../escape")]
    [InlineData("/rooted")]
    [InlineData("C:/drive")]
    [InlineData("file:stream")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    [InlineData("CON")]
    [InlineData("aux.txt")]
    [InlineData("CLOCK$/value")]
    [InlineData("CONIN$.txt")]
    [InlineData("CONOUT$/value")]
    [InlineData("COM¹.dll")]
    [InlineData("com²/value")]
    [InlineData("LPT³.log")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    public async Task RejectsTraversalRootedAdsAndPortableUnsafeNames(string entry)
    {
        string zip = WriteArchive(
            "unsafe-" + Guid.NewGuid().ToString("N") + ".zip",
            UpdateTestData.CreateZip([(entry, Encoding.UTF8.GetBytes("bad"), 0x81A4)]));
        string destination = Path.Combine(_root, "unsafe-" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            new SafeZipExtractor().ExtractAsync(zip, destination));

        Assert.False(Directory.Exists(destination));
        Assert.False(File.Exists(Path.Combine(_root, "escape")));
    }

    [Theory]
    [InlineData("CONIN$.txt")]
    [InlineData("CONOUT$/child")]
    [InlineData("COM¹.dll")]
    [InlineData("LPT³/child")]
    [InlineData("CLOCK$")]
    public void VersionMetadataUsesTheSameCompletePortableDeviceRules(string path) =>
        Assert.False(ClientVersionStore.IsNormalizedRelative(path));

    [Fact]
    public async Task RejectsDuplicateCaseAndFileDirectoryCollisionsBeforeExtraction()
    {
        byte[][] archives =
        [
            UpdateTestData.CreateZip(
            [
                ("Readme.txt", Encoding.UTF8.GetBytes("a"), 0x81A4),
                ("README.TXT", Encoding.UTF8.GetBytes("b"), 0x81A4),
            ]),
            UpdateTestData.CreateZip(
            [
                ("node", Encoding.UTF8.GetBytes("file"), 0x81A4),
                ("node/child", Encoding.UTF8.GetBytes("child"), 0x81A4),
            ]),
            UpdateTestData.CreateZip(
            [
                ("Folder/one", Encoding.UTF8.GetBytes("one"), 0x81A4),
                ("folder/two", Encoding.UTF8.GetBytes("two"), 0x81A4),
            ]),
        ];

        foreach (byte[] archive in archives)
        {
            string id = Guid.NewGuid().ToString("N");
            string zip = WriteArchive(id + ".zip", archive);
            string destination = Path.Combine(_root, id);
            await Assert.ThrowsAsync<LauncherUpdateException>(() =>
                new SafeZipExtractor().ExtractAsync(zip, destination));
            Assert.False(Directory.Exists(destination));
        }
    }

    [Fact]
    public async Task RejectsSymlinkAndReparseMetadata()
    {
        byte[] symlink = UpdateTestData.CreateZip(
            [("link", Encoding.UTF8.GetBytes("../../outside"), 0xA1FF)]);
        string zip = WriteArchive("symlink.zip", symlink);
        string destination = Path.Combine(_root, "symlink");

        LauncherUpdateException error = await Assert.ThrowsAsync<LauncherUpdateException>(
            () => new SafeZipExtractor().ExtractAsync(zip, destination));

        Assert.Contains("symlink", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task RejectsEntryCountSizeTotalAndCompressionRatioBombs()
    {
        var cases = new (byte[] Archive, SafeZipExtractionLimits Limits)[]
        {
            (
                UpdateTestData.CreateZip(
                [
                    ("one", [1], 0x81A4),
                    ("two", [2], 0x81A4),
                ]),
                new SafeZipExtractionLimits(MaximumEntries: 1)),
            (
                UpdateTestData.CreateZip([("large", new byte[8], 0x81A4)]),
                new SafeZipExtractionLimits(MaximumEntryBytes: 7)),
            (
                UpdateTestData.CreateZip(
                [
                    ("one", new byte[6], 0x81A4),
                    ("two", new byte[6], 0x81A4),
                ]),
                new SafeZipExtractionLimits(MaximumTotalBytes: 10)),
            (
                UpdateTestData.CreateZip(
                    [("ratio", new byte[64 * 1024], 0x81A4)],
                    CompressionLevel.SmallestSize),
                new SafeZipExtractionLimits(MaximumCompressionRatio: 2)),
        };

        foreach ((byte[] archive, SafeZipExtractionLimits limits) in cases)
        {
            string id = Guid.NewGuid().ToString("N");
            string zip = WriteArchive(id + ".zip", archive);
            string destination = Path.Combine(_root, id);
            await Assert.ThrowsAsync<LauncherUpdateException>(() =>
                new SafeZipExtractor(limits).ExtractAsync(zip, destination));
            Assert.False(Directory.Exists(destination));
        }
    }

    [Fact]
    public async Task ExistingNonEmptyDestinationAndCancellationNeverPublishPartialTree()
    {
        string zip = WriteArchive(
            "cancel.zip",
            UpdateTestData.CreateZip([("large", new byte[1024 * 1024], 0x81A4)]));
        string nonEmpty = Path.Combine(_root, "nonempty");
        Directory.CreateDirectory(nonEmpty);
        await File.WriteAllTextAsync(Path.Combine(nonEmpty, "owner"), "preserve");

        await Assert.ThrowsAsync<LauncherUpdateException>(() =>
            new SafeZipExtractor().ExtractAsync(zip, nonEmpty));
        Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Combine(nonEmpty, "owner")));

        string cancelled = Path.Combine(_root, "cancelled");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SafeZipExtractor().ExtractAsync(zip, cancelled, cancellation.Token));
        Assert.False(Directory.Exists(cancelled));
    }

    private string WriteArchive(string name, byte[] content)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, content);
        return path;
    }
}
