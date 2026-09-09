using AcDream.Platform;

namespace AcDream.Headless.Platform;

internal interface IHeadlessPlatformEnvironment
    : IApplicationPathEnvironment
{
}

internal sealed class HeadlessPlatformEnvironment
    : IHeadlessPlatformEnvironment
{
    internal static HeadlessPlatformEnvironment Instance { get; } = new();

    private HeadlessPlatformEnvironment()
    {
    }

    public bool IsWindows => OperatingSystem.IsWindows();
    public string CurrentDirectory => Environment.CurrentDirectory;

    public string? GetEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name);

    public string GetFolderPath(Environment.SpecialFolder folder) =>
        Environment.GetFolderPath(folder);
}
