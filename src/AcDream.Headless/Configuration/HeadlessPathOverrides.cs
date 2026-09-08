namespace AcDream.Headless.Configuration;

internal sealed record HeadlessPathOverrides(
    string? ConfigDirectory = null,
    string? DataDirectory = null,
    string? CacheDirectory = null)
{
    internal HeadlessPathOverrides Merge(HeadlessPathOverrides commandLine) =>
        new(
            commandLine.ConfigDirectory ?? ConfigDirectory,
            commandLine.DataDirectory ?? DataDirectory,
            commandLine.CacheDirectory ?? CacheDirectory);
}
