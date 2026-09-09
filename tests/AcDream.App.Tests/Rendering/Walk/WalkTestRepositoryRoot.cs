namespace AcDream.App.Tests.Rendering.Walk;

internal static class WalkOracleTraceRepoRoot
{
    internal static string Find()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AcDream.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "AcDream.slnx not found above the test base directory; repository-relative fixtures unavailable.");
    }
}
