namespace AcDream.App.Tests.Rendering;

internal static class InstalledDatTestPath
{
    internal static string? Resolve()
    {
        string? configured = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            return configured;

        string defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents",
            "Asheron's Call");
        return Directory.Exists(defaultPath) ? defaultPath : null;
    }
}
