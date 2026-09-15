using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.Core.Plugins;

/// <summary>The parsed contents of a release's <c>.sha256</c> asset: the output of
/// <c>shasum -a 256 &lt;zip&gt;</c>, hash first, an optional file name after it.</summary>
public sealed record PluginSha256File(string Sha256, string? FileName)
{
    public static PluginSha256File Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        string[] tokens = content.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || !ReleaseManifestClient.IsSha256(tokens[0]))
        {
            throw new LauncherUpdateException("The plugin SHA-256 file is malformed.");
        }

        string? fileName = tokens.Length > 1 ? tokens[1].TrimStart('*') : null;
        return new PluginSha256File(tokens[0].ToLowerInvariant(), fileName);
    }

    public void RequireMatches(string expectedZipName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedZipName);
        if (FileName is not null
            && !string.Equals(FileName, expectedZipName, StringComparison.Ordinal))
        {
            throw new LauncherUpdateException(
                $"The plugin SHA-256 file names '{FileName}', expected '{expectedZipName}'.");
        }
    }
}
