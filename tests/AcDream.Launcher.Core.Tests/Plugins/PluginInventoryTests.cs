using AcDream.Launcher.Core.Plugins;
using AcDream.Launcher.Core.Updates;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Tests.Plugins;

public sealed class PluginInventoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "acdream-plugin-inventory-tests",
        Guid.NewGuid().ToString("N"));
    private readonly ApplicationPathSet _paths;

    public PluginInventoryTests()
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
    public void AManagedPluginIsDistinguishedFromAManualOneByTheRecord()
    {
        WriteManifest(
            Path.Combine(_paths.PluginsDirectory, "edwards.hello"),
            "edwards.hello",
            "0.1.0");
        WriteManifest(
            Path.Combine(_paths.PluginsDirectory, "someone.manual"),
            "someone.manual",
            "1.0.0");
        InstalledPluginRecordStore recordStore = MakeRecordStore();
        recordStore.Records.Add(new InstalledPluginRecord(
            "edwards.hello",
            "shaneedwards/openac-plugin-hello",
            PluginInstallSource.Listed,
            "0.1.0",
            "v0.1.0",
            new string('a', 64),
            DateTimeOffset.UtcNow,
            null));
        var inventory = new PluginInventory(_paths, recordStore);

        IReadOnlyList<InstalledPluginInfo> plugins = inventory.Build(
            clientResolution: null,
            catalog: null);

        InstalledPluginInfo managed = Assert.Single(
            plugins,
            info => info.Id == "edwards.hello");
        Assert.Equal(InstalledPluginSource.Managed, managed.Source);
        Assert.Equal("shaneedwards/openac-plugin-hello", managed.Repo);
        Assert.Equal(PluginInstallSource.Listed, managed.ListedSource);
        InstalledPluginInfo direct = Assert.Single(
            plugins,
            info => info.Id == "someone.manual");
        Assert.Equal(InstalledPluginSource.Direct, direct.Source);
        Assert.Null(direct.Repo);
        Assert.Null(direct.ListedSource);
    }

    [Fact]
    public void ABundledIdThatIsAlsoManagedIsFlaggedAsAConflictOnce()
    {
        string clientDirectory = Path.Combine(_root, "client");
        WriteManifest(
            Path.Combine(_paths.PluginsDirectory, "edwards.hello"),
            "edwards.hello",
            "0.1.0");
        WriteManifest(
            Path.Combine(clientDirectory, "plugins", "edwards.hello"),
            "edwards.hello",
            "0.1.0");
        InstalledPluginRecordStore recordStore = MakeRecordStore();
        recordStore.Records.Add(new InstalledPluginRecord(
            "edwards.hello",
            "shaneedwards/openac-plugin-hello",
            PluginInstallSource.Listed,
            "0.1.0",
            "v0.1.0",
            new string('a', 64),
            DateTimeOffset.UtcNow,
            null));
        var inventory = new PluginInventory(_paths, recordStore);
        var clientResolution = new ClientVersionResolution(
            ClientVersionState.Verified,
            "ok",
            LauncherVersion.Parse("0.1.7"),
            clientDirectory,
            null,
            null);

        IReadOnlyList<InstalledPluginInfo> plugins = inventory.Build(clientResolution, catalog: null);

        InstalledPluginInfo info = Assert.Single(plugins);
        Assert.Equal(InstalledPluginSource.Managed, info.Source);
        Assert.True(info.Conflict);
    }

    [Fact]
    public void ARecordWhoseOwnFolderManifestIdDiffersFallsThroughToTheDirectScan()
    {
        WriteManifest(
            Path.Combine(_paths.PluginsDirectory, "edwards.hello"),
            "edwards.other",
            "0.1.0");
        InstalledPluginRecordStore recordStore = MakeRecordStore();
        recordStore.Records.Add(new InstalledPluginRecord(
            "edwards.hello",
            "shaneedwards/openac-plugin-hello",
            PluginInstallSource.Listed,
            "0.1.0",
            "v0.1.0",
            new string('a', 64),
            DateTimeOffset.UtcNow,
            null));
        var inventory = new PluginInventory(_paths, recordStore);

        InstalledPluginInfo info = Assert.Single(
            inventory.Build(clientResolution: null, catalog: null));

        Assert.Equal("edwards.other", info.Id);
        Assert.Equal(InstalledPluginSource.Direct, info.Source);
    }

    [Fact]
    public void AManagedPluginAndAStrayFolderSharingItsIdAreBothFlaggedRegardlessOfSortOrder()
    {
        WriteManifest(
            Path.Combine(_paths.PluginsDirectory, "edwards.hello"),
            "edwards.hello",
            "0.1.0");
        WriteManifest(
            Path.Combine(_paths.PluginsDirectory, "aaa-stray"),
            "edwards.hello",
            "0.1.0");
        InstalledPluginRecordStore recordStore = MakeRecordStore();
        recordStore.Records.Add(new InstalledPluginRecord(
            "edwards.hello",
            "shaneedwards/openac-plugin-hello",
            PluginInstallSource.Listed,
            "0.1.0",
            "v0.1.0",
            new string('a', 64),
            DateTimeOffset.UtcNow,
            null));
        var inventory = new PluginInventory(_paths, recordStore);

        IReadOnlyList<InstalledPluginInfo> plugins = inventory.Build(
            clientResolution: null,
            catalog: null);

        InstalledPluginInfo managed = Assert.Single(
            plugins, info => info.Source == InstalledPluginSource.Managed);
        Assert.True(managed.HasDuplicate);
        Assert.Null(managed.Refusal);
        InstalledPluginInfo stray = Assert.Single(
            plugins, info => info.Source == InstalledPluginSource.Direct);
        Assert.Equal("Another copy of this plugin is installed.", stray.Refusal);
    }

    [Fact]
    public void TwoDirectFoldersSharingAnIdAreBothRefused()
    {
        WriteManifest(
            Path.Combine(_paths.PluginsDirectory, "aaa-copy"),
            "edwards.hello",
            "0.1.0");
        WriteManifest(
            Path.Combine(_paths.PluginsDirectory, "zzz-copy"),
            "edwards.hello",
            "0.1.0");
        var inventory = new PluginInventory(_paths, MakeRecordStore());

        IReadOnlyList<InstalledPluginInfo> plugins = inventory.Build(
            clientResolution: null,
            catalog: null);

        Assert.Equal(2, plugins.Count);
        Assert.All(plugins, info =>
        {
            Assert.Equal(InstalledPluginSource.Direct, info.Source);
            Assert.Equal("Another copy of this plugin is installed.", info.Refusal);
        });
    }

    [Fact]
    public void ADirectFolderSharingAnIdWithABundledPluginIsRefused()
    {
        string clientDirectory = Path.Combine(_root, "client");
        WriteManifest(
            Path.Combine(_paths.PluginsDirectory, "edwards.hello"),
            "edwards.hello",
            "0.1.0");
        WriteManifest(
            Path.Combine(clientDirectory, "plugins", "edwards.hello"),
            "edwards.hello",
            "0.1.0");
        var inventory = new PluginInventory(_paths, MakeRecordStore());
        var clientResolution = new ClientVersionResolution(
            ClientVersionState.Verified,
            "ok",
            LauncherVersion.Parse("0.1.7"),
            clientDirectory,
            null,
            null);

        InstalledPluginInfo info = Assert.Single(
            inventory.Build(clientResolution, catalog: null));

        Assert.Equal(InstalledPluginSource.Direct, info.Source);
        Assert.Equal("Another copy of this plugin is installed.", info.Refusal);
    }

    [Fact]
    public void FindReturnsNullWhenNoSourceHasTheId()
    {
        InstalledPluginRecordStore recordStore = MakeRecordStore();
        var inventory = new PluginInventory(_paths, recordStore);

        Assert.Null(inventory.Find("nobody.home", clientResolution: null, catalog: null));
    }

    [Fact]
    public void FindIgnoresCase()
    {
        WriteManifest(
            Path.Combine(_paths.PluginsDirectory, "edwards.hello"),
            "edwards.hello",
            "0.1.0");
        InstalledPluginRecordStore recordStore = MakeRecordStore();
        var inventory = new PluginInventory(_paths, recordStore);

        Assert.NotNull(inventory.Find("Edwards.Hello", clientResolution: null, catalog: null));
    }

    [Theory]
    [InlineData(null, "client not installed")]
    [InlineData("0.0.9", "requires OpenAC 0.1.0 or newer (this is 0.0.9)")]
    [InlineData("0.1.0", null)]
    public void EvaluateVersionCompatibilityIgnoresHostsAndChecksOnlyVersion(
        string? clientVersion,
        string? expectedReason)
    {
        LauncherPluginManifest manifest = LauncherPluginManifest.Parse(ManifestJson(
            "edwards.hello",
            "0.1.0",
            minHostVersion: "0.1.0",
            hosts: ["headless"]));

        string? reason = PluginInventory.EvaluateVersionCompatibility(
            manifest,
            clientVersion is null ? null : LauncherVersion.Parse(clientVersion));

        Assert.Equal(expectedReason, reason);
    }

    [Fact]
    public void FindBlockReasonMatchesOnIdAndVersion()
    {
        PluginCatalog catalog = PluginCatalog.Parse("""
            {
              "schemaVersion": 1,
              "plugins": [
                { "id": "edwards.hello", "name": "Hello", "author": "Shane Edwards",
                  "description": "Says hello.", "repo": "shaneedwards/openac-plugin-hello" }
              ],
              "blocked": [
                { "id": "edwards.hello", "versions": ["0.1.0"], "reason": "test" }
              ]
            }
            """);

        Assert.Equal(
            "test",
            PluginInventory.FindBlockReason(catalog, "edwards.hello", LauncherVersion.Parse("0.1.0")));
        Assert.Null(
            PluginInventory.FindBlockReason(catalog, "edwards.hello", LauncherVersion.Parse("0.1.1")));
        Assert.Null(PluginInventory.FindBlockReason(null, "edwards.hello", LauncherVersion.Parse("0.1.0")));
        Assert.Equal(
            "test",
            PluginInventory.FindBlockReason(catalog, "Edwards.Hello", LauncherVersion.Parse("0.1.0")));
    }

    private InstalledPluginRecordStore MakeRecordStore() =>
        InstalledPluginRecordStore.ForApplicationPaths(_paths);

    private static void WriteManifest(string directory, string id, string version)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "plugin.json"),
            ManifestJson(id, version, minHostVersion: "0.1.0", hosts: ["headless"]));
        File.WriteAllBytes(Path.Combine(directory, $"{id}.dll"), []);
    }

    private static string ManifestJson(
        string id,
        string version,
        string minHostVersion,
        string[] hosts) =>
        $$"""
        {
          "id": "{{id}}",
          "displayName": "{{id}}",
          "version": "{{version}}",
          "entryDll": "{{id}}.dll",
          "apiVersion": 1,
          "minHostVersion": "{{minHostVersion}}",
          "hosts": [{{string.Join(", ", hosts.Select(h => $"\"{h}\""))}}]
        }
        """;
}
