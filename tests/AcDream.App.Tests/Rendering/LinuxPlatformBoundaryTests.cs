namespace AcDream.App.Tests.Rendering;

public sealed class LinuxPlatformBoundaryTests
{
    [Fact]
    public void FramePacingSelectsPlatformWaiterOnlyAtFactoryBoundary()
    {
        string app = AppSourceRoot();
        string controller = File.ReadAllText(Path.Combine(
            app,
            "Rendering",
            "FramePacingController.cs"));
        Assert.Contains(
            "PlatformFramePacingWaiterFactory.ForCurrentProcess()",
            controller,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "WindowsHighResolutionFramePacingWaiter.Create()",
            controller,
            StringComparison.Ordinal);

        string[] windowsImports = Directory
            .EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains(
                "LibraryImport(\"kernel32.dll\"",
                StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path)!)
            .ToArray();

        Assert.Equal(
            ["WindowsHighResolutionFramePacingWaiter.cs"],
            windowsImports);
    }

    [Fact]
    public void GraphicalFeatureCodeDoesNotResolveWindowsLocalAppData()
    {
        string root = RepositoryRoot();
        string[] productionFiles =
        [
            .. Directory.EnumerateFiles(
                Path.Combine(root, "src", "AcDream.App"),
                "*.cs",
                SearchOption.AllDirectories),
            .. Directory.EnumerateFiles(
                Path.Combine(root, "src", "AcDream.UI.Abstractions"),
                "*.cs",
                SearchOption.AllDirectories),
        ];

        string[] offenders = productionFiles
            .Where(path => File.ReadAllText(path).Contains(
                "LocalApplicationData",
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void OperatingSystemChecksRemainInsidePlatformOwners()
    {
        string app = AppSourceRoot();
        string[] offenders = Directory
            .EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains(
                "OperatingSystem.Is",
                StringComparison.Ordinal))
            .Where(path =>
            {
                string relative = Path.GetRelativePath(app, path)
                    .Replace('\\', '/');
                return !relative.StartsWith(
                        "Platform/",
                        StringComparison.Ordinal)
                    && relative is not
                        "Rendering/FramePacingWaiterFactory.cs"
                    && relative is not
                        "Rendering/LinuxMonotonicFramePacingWaiter.cs"
                    && relative is not
                        "Rendering/WindowsHighResolutionFramePacingWaiter.cs";
            })
            .Select(path => Path.GetRelativePath(app, path))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void RuntimePlatformGuardHasOneDefinitionAndOneApprovedConsumer()
    {
        string app = AppSourceRoot();
        string[] files = Directory
            .EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains(
                "RuntimePlatformGuard",
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(app, path).Replace('\\', '/'))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "Credentials/AppCredentialResolver.cs",
                "Platform/GraphicalHostPlatformServices.cs",
            ],
            files);
    }

    [Fact]
    public void ShippedPluginCopiesUseResolvedTargetPathsForBuildAndPublish()
    {
        string project = File.ReadAllText(Path.Combine(
            AppSourceRoot(),
            "AcDream.App.csproj"));

        Assert.Contains("$(TargetFramework)", project, StringComparison.Ordinal);
        Assert.Contains("$(RuntimeIdentifier)", project, StringComparison.Ordinal);
        Assert.Contains("$(OutputPath)plugins/", project, StringComparison.Ordinal);
        Assert.Contains("$(PublishDir)plugins/", project, StringComparison.Ordinal);
        Assert.Equal(
            2,
            project.Split("Targets=\"GetTargetPath\"", StringSplitOptions.None)
                .Length - 1);
        Assert.Contains(
            "../AcDream.Plugins.MossTank/mosstank*.xml",
            project,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "/bin/$(Configuration)",
            project,
            StringComparison.Ordinal);
    }

    private static string AppSourceRoot() =>
        Path.Combine(RepositoryRoot(), "src", "AcDream.App");

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate repository root from test output.");
    }
}
