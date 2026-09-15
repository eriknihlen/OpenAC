using AcDream.Launcher.Core.Plugins;
using AcDream.Launcher.Core.Updates;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Tests.Plugins;

public sealed class InstalledPluginRecordStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "acdream-installed-plugin-record-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void FilePathIsUnderAppUnderDataDirectory()
    {
        var paths = new ApplicationPathSet(
            Path.Combine(_root, "config"),
            Path.Combine(_root, "data"),
            Path.Combine(_root, "cache"),
            null);

        InstalledPluginRecordStore store = InstalledPluginRecordStore.ForApplicationPaths(paths);

        Assert.Equal(
            Path.Combine(paths.DataDirectory, "app", "plugins-installed.json"),
            store.FilePath);
    }

    [Fact]
    public void LoadOfAMissingFileStartsEmptyAndReturnsFalse()
    {
        var store = new InstalledPluginRecordStore(
            Path.Combine(_root, "app", "plugins-installed.json"));

        bool loaded = store.Load();

        Assert.False(loaded);
        Assert.Empty(store.Records);
    }

    [Fact]
    public void SaveThenLoadRoundTripsRecordsIncludingAPendingOne()
    {
        var store = new InstalledPluginRecordStore(
            Path.Combine(_root, "app", "plugins-installed.json"));
        store.Records.Add(new InstalledPluginRecord(
            "edwards.hello",
            "shaneedwards/openac-plugin-hello",
            PluginInstallSource.Listed,
            "0.1.0",
            "v0.1.0",
            new string('a', 64),
            DateTimeOffset.UtcNow,
            Pending: null));
        store.Records.Add(new InstalledPluginRecord(
            "edwards.pending",
            "shaneedwards/openac-plugin-pending",
            PluginInstallSource.Unlisted,
            Version: null,
            Tag: null,
            ZipSha256: null,
            DateTimeOffset.UtcNow,
            Pending: new PendingPluginInstall("0.2.0", "v0.2.0", new string('b', 64))));

        store.Save();
        var reloaded = new InstalledPluginRecordStore(store.FilePath);
        reloaded.Load();

        Assert.Equal(2, reloaded.Records.Count);
        InstalledPluginRecord confirmed = Assert.Single(
            reloaded.Records,
            record => record.Id == "edwards.hello");
        Assert.Equal("0.1.0", confirmed.Version);
        Assert.Null(confirmed.Pending);
        InstalledPluginRecord pending = Assert.Single(
            reloaded.Records,
            record => record.Id == "edwards.pending");
        Assert.Null(pending.Version);
        Assert.Equal("0.2.0", pending.Pending!.Version);
    }

    [Fact]
    public void FindLooksUpCaseInsensitively()
    {
        var store = new InstalledPluginRecordStore(
            Path.Combine(_root, "app", "plugins-installed.json"));
        store.Records.Add(new InstalledPluginRecord(
            "edwards.hello",
            "shaneedwards/openac-plugin-hello",
            PluginInstallSource.Listed,
            "0.1.0",
            "v0.1.0",
            new string('a', 64),
            DateTimeOffset.UtcNow,
            null));

        Assert.NotNull(store.Find("edwards.hello"));
        Assert.Null(store.Find("edwards.other"));
        Assert.NotNull(store.Find("EDWARDS.HELLO"));
    }

    [Fact]
    public void DuplicateIdsOnDiskAreRejected()
    {
        string path = Path.Combine(_root, "app", "plugins-installed.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "schemaVersion": 1,
              "plugins": [
                { "id": "edwards.hello", "repo": "shaneedwards/openac-plugin-hello",
                  "source": "listed", "version": "0.1.0", "tag": "v0.1.0",
                  "zipSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "installedAt": "2026-01-01T00:00:00Z", "warningAcceptedAt": null,
                  "pending": null },
                { "id": "edwards.hello", "repo": "shaneedwards/openac-plugin-hello",
                  "source": "listed", "version": "0.1.1", "tag": "v0.1.1",
                  "zipSha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                  "installedAt": "2026-01-01T00:00:00Z", "warningAcceptedAt": null,
                  "pending": null }
              ]
            }
            """);

        var store = new InstalledPluginRecordStore(path);
        Assert.Throws<LauncherUpdateException>(() => store.Load());
    }

    [Fact]
    public void CaseVariantDuplicateIdsOnDiskAreRejected()
    {
        string path = Path.Combine(_root, "app", "plugins-installed.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "schemaVersion": 1,
              "plugins": [
                { "id": "edwards.hello", "repo": "shaneedwards/openac-plugin-hello",
                  "source": "listed", "version": "0.1.0", "tag": "v0.1.0",
                  "zipSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "installedAt": "2026-01-01T00:00:00Z", "warningAcceptedAt": null,
                  "pending": null },
                { "id": "Edwards.Hello", "repo": "shaneedwards/openac-plugin-hello",
                  "source": "listed", "version": "0.1.1", "tag": "v0.1.1",
                  "zipSha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                  "installedAt": "2026-01-01T00:00:00Z", "warningAcceptedAt": null,
                  "pending": null }
              ]
            }
            """);

        var store = new InstalledPluginRecordStore(path);
        Assert.Throws<LauncherUpdateException>(() => store.Load());
    }

    [Fact]
    public void UnsupportedSchemaVersionIsRejected()
    {
        string path = Path.Combine(_root, "app", "plugins-installed.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "schemaVersion": 2, "plugins": [] }""");

        var store = new InstalledPluginRecordStore(path);
        Assert.Throws<LauncherUpdateException>(() => store.Load());
    }

    [Fact]
    public void SaveSecuresTheFileToOwnerOnlyModeOnUnix()
    {
        if (!LauncherOperatingSystem.IsUnix)
        {
            return;
        }

        var store = new InstalledPluginRecordStore(
            Path.Combine(_root, "app", "plugins-installed.json"));
        store.Records.Add(new InstalledPluginRecord(
            "edwards.hello",
            "shaneedwards/openac-plugin-hello",
            PluginInstallSource.Listed,
            "0.1.0",
            "v0.1.0",
            new string('a', 64),
            DateTimeOffset.UtcNow,
            null));

        store.Save();

        Assert.Equal(
            InstalledPluginRecordStore.OwnerOnlyFileMode,
            File.GetUnixFileMode(store.FilePath));
    }

    [Fact]
    public void LoadToleratesARecordWrittenByTheEarlierBuildsWarningAcceptedAtField()
    {
        string path = Path.Combine(_root, "app", "plugins-installed.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "schemaVersion": 1,
              "plugins": [
                { "id": "edwards.hello", "repo": "shaneedwards/openac-plugin-hello",
                  "source": "listed", "version": "0.1.0", "tag": "v0.1.0",
                  "zipSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "installedAt": "2026-01-01T00:00:00Z",
                  "warningAcceptedAt": "2026-01-01T00:00:00Z", "pending": null }
              ]
            }
            """);

        var store = new InstalledPluginRecordStore(path);

        bool loaded = store.Load();

        Assert.True(loaded);
        InstalledPluginRecord record = Assert.Single(store.Records);
        Assert.Equal("edwards.hello", record.Id);
    }

    [Fact]
    public void SaveAfterLoadNeverWritesWarningAcceptedAt()
    {
        string path = Path.Combine(_root, "app", "plugins-installed.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "schemaVersion": 1,
              "plugins": [
                { "id": "edwards.hello", "repo": "shaneedwards/openac-plugin-hello",
                  "source": "listed", "version": "0.1.0", "tag": "v0.1.0",
                  "zipSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "installedAt": "2026-01-01T00:00:00Z",
                  "warningAcceptedAt": "2026-01-01T00:00:00Z", "pending": null }
              ]
            }
            """);

        var store = new InstalledPluginRecordStore(path);
        store.Load();
        store.Save();

        string saved = File.ReadAllText(path);
        Assert.DoesNotContain("warningAcceptedAt", saved);
    }
}
