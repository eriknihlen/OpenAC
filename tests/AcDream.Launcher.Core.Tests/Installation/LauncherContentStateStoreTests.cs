using System.Buffers.Binary;
using AcDream.Launcher.Core.Integrity;
using AcDream.Launcher.Core.Installation;
using AcDream.Launcher.Core.Launching;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Tests.Installation;

public sealed class LauncherContentStateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "acdream-content-state-tests",
        Guid.NewGuid().ToString("N"));
    private readonly ApplicationPathSet _paths;

    public LauncherContentStateStoreTests()
    {
        _paths = new ApplicationPathSet(
            Path.Combine(_root, "config"),
            Path.Combine(_root, "data"),
            Path.Combine(_root, "cache"),
            null);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task MatchingSidecarResolvesWithoutStartupHashAndForceVerifyHashesBoth()
    {
        int hashCalls = 0;
        var store = new LauncherContentStateStore(
            _paths,
            async (path, cancellationToken) =>
            {
                hashCalls++;
                return await FileIntegrity.ComputeSha256HexAsync(
                    path,
                    cancellationToken);
            });
        string basePath = Path.Combine(_paths.DataDirectory, "pak", "acdream.pak");
        string overlayPath = Path.Combine(
            _paths.DataDirectory,
            "pak",
            "acdream-update-5-test.pak");
        WritePakHeader(basePath, recipe: 4);
        WritePakHeader(overlayPath, recipe: 5);
        LauncherInstallRecord record = await RecordAsync(basePath, recipe: 4);
        var overlay = new LauncherContentOverlay(
            Path.GetFileName(overlayPath),
            await FileIntegrity.ComputeSha256HexAsync(overlayPath),
            new FileInfo(overlayPath).Length,
            5);
        var state = new LauncherContentState(
            LauncherContentState.CurrentSchemaVersion,
            record.PreparedAssetSha256,
            5,
            overlay);
        await store.SaveAtomicallyAsync(record, state);

        (LauncherContentState? quick, string? quickError) =
            await store.LoadAsync(record);

        Assert.Null(quickError);
        Assert.Equal(state, quick);
        Assert.Equal(0, hashCalls);

        (LauncherContentState? verified, string? verifyError) =
            await store.LoadAsync(record, forceFullVerification: true);
        Assert.Null(verifyError);
        Assert.Equal(state, verified);
        Assert.Equal(2, hashCalls);
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(store.StatePath)!,
            "content.current.json.*.tmp"));
    }

    [Theory]
    [InlineData("../escape.pak")]
    [InlineData("nested/escape.pak")]
    [InlineData("C:\\escape.pak")]
    [InlineData("not-a-pak.txt")]
    public async Task OverlayPathMustBeOneContainedPakFilename(string path)
    {
        var store = new LauncherContentStateStore(_paths);
        string basePath = Path.Combine(_paths.DataDirectory, "pak", "acdream.pak");
        WritePakHeader(basePath, recipe: 4);
        LauncherInstallRecord record = await RecordAsync(basePath, recipe: 4);
        var state = new LauncherContentState(
            LauncherContentState.CurrentSchemaVersion,
            record.PreparedAssetSha256,
            5,
            new LauncherContentOverlay(path, new string('a', 64), 64, 5));

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.SaveAtomicallyAsync(record, state));

        Assert.Contains("filename", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(store.StatePath));
    }

    [Fact]
    public async Task BaseDigestBindingRejectsSidecarFromPriorFullRebuild()
    {
        var store = new LauncherContentStateStore(_paths);
        string basePath = Path.Combine(_paths.DataDirectory, "pak", "acdream.pak");
        WritePakHeader(basePath, recipe: 4);
        LauncherInstallRecord record = await RecordAsync(basePath, recipe: 4);
        var state = new LauncherContentState(
            LauncherContentState.CurrentSchemaVersion,
            new string('b', 64),
            5,
            new LauncherContentOverlay(
                "acdream-update-5-test.pak",
                new string('a', 64),
                64,
                5));

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.SaveAtomicallyAsync(record, state));

        Assert.Contains("base pak", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    internal static void WritePakHeader(string path, uint recipe)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] bytes = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x4B504341u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), 200);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 300);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), 400);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24, 8), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36, 4), recipe);
        File.WriteAllBytes(path, bytes);
    }

    internal static async Task<LauncherInstallRecord> RecordAsync(
        string basePath,
        uint recipe,
        string? datDirectory = null) =>
        new(
            datDirectory ?? Path.GetDirectoryName(basePath)!,
            basePath,
            await FileIntegrity.ComputeSha256HexAsync(basePath),
            new FileInfo(basePath).Length,
            recipe);
}
