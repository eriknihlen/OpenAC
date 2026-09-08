using AcDream.App.Platform;
using AcDream.Platform;

namespace AcDream.App.Tests.Platform;

public sealed class GraphicalLegacyConfigurationMigratorTests
    : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "acdream-l0-migration-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void MissingCanonicalFilesAreCopiedWithoutOverwriting()
    {
        string legacy = Path.Combine(_root, "legacy");
        string canonical = Path.Combine(_root, "canonical");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(canonical);
        File.WriteAllText(
            Path.Combine(legacy, "settings.json"),
            "legacy-settings");
        File.WriteAllText(
            Path.Combine(legacy, "keybinds.json"),
            "legacy-keybinds");
        File.WriteAllText(
            Path.Combine(canonical, "keybinds.json"),
            "canonical-keybinds");
        var paths = new ApplicationPathSet(
            canonical,
            Path.Combine(_root, "data"),
            Path.Combine(_root, "cache"),
            legacy);

        IReadOnlyList<string> migrated =
            GraphicalLegacyConfigurationMigrator.Migrate(paths);

        Assert.Single(migrated);
        Assert.Equal(
            "legacy-settings",
            File.ReadAllText(paths.SettingsFile));
        Assert.Equal(
            "canonical-keybinds",
            File.ReadAllText(paths.KeyBindingsFile));
    }

    [Fact]
    public void NoLegacyDirectoryProducesNoWrites()
    {
        var paths = new ApplicationPathSet(
            Path.Combine(_root, "canonical"),
            Path.Combine(_root, "data"),
            Path.Combine(_root, "cache"),
            LegacyConfigDirectory: null);

        Assert.Empty(
            GraphicalLegacyConfigurationMigrator.Migrate(paths));
        Assert.False(Directory.Exists(paths.ConfigDirectory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
