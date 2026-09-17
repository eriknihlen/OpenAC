using AcDream.Headless.Configuration;
using AcDream.Headless.Platform;

namespace AcDream.Headless.Tests;

internal static class IsolatedHeadlessPaths
{
    // Keeps process-host tests from discovering plugins installed on the machine running them.
    internal static HeadlessPathSet Create()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"acdream-headless-paths-{Guid.NewGuid():N}");
        return HeadlessPathSet.Resolve(new HeadlessPathOverrides(
            ConfigDirectory: Path.Combine(root, "config"),
            DataDirectory: Path.Combine(root, "data"),
            CacheDirectory: Path.Combine(root, "cache")));
    }
}
