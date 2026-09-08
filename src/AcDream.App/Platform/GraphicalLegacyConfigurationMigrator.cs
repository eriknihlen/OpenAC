using AcDream.Platform;

namespace AcDream.App.Platform;

internal static class GraphicalLegacyConfigurationMigrator
{
    private static readonly string[] FileNames =
    [
        "settings.json",
        "keybinds.json",
    ];

    internal static IReadOnlyList<string> Migrate(
        ApplicationPathSet paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.LegacyConfigDirectory is not { } legacyDirectory
            || PathsEqual(legacyDirectory, paths.ConfigDirectory))
        {
            return [];
        }

        List<string>? migrated = null;
        foreach (string fileName in FileNames)
        {
            string source = Path.Combine(legacyDirectory, fileName);
            string destination = Path.Combine(
                paths.ConfigDirectory,
                fileName);
            if (!File.Exists(source) || File.Exists(destination))
                continue;

            Directory.CreateDirectory(paths.ConfigDirectory);
            File.Copy(source, destination, overwrite: false);
            (migrated ??= []).Add(destination);
        }

        return migrated ?? [];
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
