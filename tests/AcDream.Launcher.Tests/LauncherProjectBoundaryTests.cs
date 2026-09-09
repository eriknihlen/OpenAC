using System.Diagnostics;
using System.Xml.Linq;

namespace AcDream.Launcher.Tests;

public sealed class LauncherProjectBoundaryTests
{
    private static readonly string[] ExpectedAvaloniaPackages =
    [
        "Avalonia",
        "Avalonia.Desktop",
        "Avalonia.Themes.Fluent",
    ];

    [Fact]
    public void LauncherReferencesOnlyLauncherCoreAndUsesCentralAvaloniaVersion()
    {
        string root = FindRepositoryRoot();
        string projectPath = Path.Combine(
            root,
            "src",
            "AcDream.Launcher",
            "AcDream.Launcher.csproj");
        XDocument project = XDocument.Load(projectPath);

        string projectReference = Assert.Single(
            project.Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value ?? string.Empty));
        Assert.EndsWith(
            "AcDream.Launcher.Core\\AcDream.Launcher.Core.csproj",
            projectReference,
            StringComparison.Ordinal);

        (string Name, string? Version)[] packages = project
            .Descendants("PackageReference")
            .Select(element => (
                element.Attribute("Include")?.Value ?? string.Empty,
                element.Attribute("Version")?.Value))
            .OrderBy(package => package.Item1, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedAvaloniaPackages, packages.Select(package => package.Name));
        Assert.All(packages, package => Assert.Null(package.Version));

        XDocument centralVersions = XDocument.Load(Path.Combine(root, "Directory.Packages.props"));
        (string Name, string Version)[] centralAvaloniaPackages = centralVersions
            .Descendants("PackageVersion")
            .Select(element => (
                element.Attribute("Include")?.Value ?? string.Empty,
                element.Attribute("Version")?.Value ?? string.Empty))
            .Where(package => ExpectedAvaloniaPackages.Contains(package.Item1, StringComparer.Ordinal))
            .OrderBy(package => package.Item1, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedAvaloniaPackages, centralAvaloniaPackages.Select(package => package.Name));
        Assert.All(centralAvaloniaPackages, package => Assert.Equal("12.1.1", package.Version));
    }

    [Fact]
    public void LauncherAndItsTestsAreSolutionMembers()
    {
        string root = FindRepositoryRoot();
        XDocument solution = XDocument.Load(Path.Combine(root, "AcDream.slnx"));
        string[] paths = solution.Descendants("Project")
            .Select(element => (element.Attribute("Path")?.Value ?? string.Empty)
                .Replace('\\', '/'))
            .ToArray();

        Assert.Contains("src/AcDream.Launcher/AcDream.Launcher.csproj", paths);
        Assert.Contains("tests/AcDream.Launcher.Tests/AcDream.Launcher.Tests.csproj", paths);
    }

    [Fact]
    public void LinuxPublishEvaluatesAsSelfContainedSingleFile()
    {
        string root = FindRepositoryRoot();
        string projectPath = Path.Combine(
            root,
            "src",
            "AcDream.Launcher",
            "AcDream.Launcher.csproj");

        Assert.Equal("true", EvaluateProperty(projectPath, "SelfContained"));
        Assert.Equal("true", EvaluateProperty(projectPath, "PublishSingleFile"));
    }

    [Fact]
    public void RidPublishComposesBakeWithoutAProjectReference()
    {
        string project = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "AcDream.Launcher",
            "AcDream.Launcher.csproj"));

        Assert.DoesNotContain(
            "ProjectReference Include=\"..\\AcDream.Bake",
            project,
            StringComparison.Ordinal);
        Assert.Contains("PublishCoDeployedBakeTool", project, StringComparison.Ordinal);
        Assert.Contains("..\\AcDream.Bake\\AcDream.Bake.csproj", project, StringComparison.Ordinal);
        Assert.Contains("SelfContained=true", project, StringComparison.Ordinal);
        Assert.Contains("PublishSingleFile=true", project, StringComparison.Ordinal);
    }

    [Fact]
    public void ModalMarkupAndCodeBehindCarryKeyboardFocusAndAccessibilityGuards()
    {
        string root = FindRepositoryRoot();
        string markup = File.ReadAllText(Path.Combine(
            root,
            "src",
            "AcDream.Launcher",
            "MainWindow.axaml"));
        string codeBehind = File.ReadAllText(Path.Combine(
            root,
            "src",
            "AcDream.Launcher",
            "MainWindow.axaml.cs"));

        Assert.Equal(6, Count(markup, "KeyboardNavigation.TabNavigation=\"Cycle\""));
        Assert.Equal(6, Count(markup, "KeyDown=\"OnModalKeyDown\""));
        Assert.True(Count(markup, "AutomationProperties.Name=") >= 13);
        Assert.True(Count(markup, "IsDefault=\"True\"") >= 3);
        Assert.True(Count(markup, "IsCancel=\"True\"") >= 3);
        Assert.Contains("ServerNameTextBox", markup, StringComparison.Ordinal);
        Assert.Contains("AccountNameTextBox", markup, StringComparison.Ordinal);
        Assert.Contains("CharacterNameTextBox", markup, StringComparison.Ordinal);
        Assert.Contains("FocusActiveModal", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_focusBeforeModal", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Key.Escape", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void PortabilityWorkflowBuildsTestsPublishesAndExecutesTheLauncher()
    {
        string workflow = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            ".github",
            "workflows",
            "headless-portability.yml"));

        Assert.Contains("portable-launcher:", workflow, StringComparison.Ordinal);
        Assert.Contains("tests/AcDream.Launcher.Tests/AcDream.Launcher.Tests.csproj", workflow, StringComparison.Ordinal);
        Assert.Contains("tests/AcDream.Bake.Tests/AcDream.Bake.Tests.csproj", workflow, StringComparison.Ordinal);
        Assert.Contains("-r linux-x64", workflow, StringComparison.Ordinal);
        Assert.Contains("-getProperty:SelfContained", workflow, StringComparison.Ordinal);
        Assert.Contains("DOTNET_ROOT", workflow, StringComparison.Ordinal);
        Assert.Contains("--verify-publish", workflow, StringComparison.Ordinal);
        Assert.Contains("acdream-bake.exe\" --help", workflow, StringComparison.Ordinal);
        Assert.Contains("\"$root/acdream-bake\" --help", workflow, StringComparison.Ordinal);
        Assert.Contains("test -x \"$root/acdream-bake\"", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "test -x src/AcDream.Headless/bin/Release/net10.0/acdream-headless",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("test -x \"$root/AcDream.App\"", workflow, StringComparison.Ordinal);
    }

    private static string EvaluateProperty(string projectPath, string property)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-property:RuntimeIdentifier=linux-x64");
        startInfo.ArgumentList.Add($"-getProperty:{property}");

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start dotnet msbuild.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "dotnet msbuild did not exit.");
        Assert.True(
            process.ExitCode == 0,
            $"dotnet msbuild exited {process.ExitCode}: {error}");
        return output.Trim();
    }

    private static int Count(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;

    private static string FindRepositoryRoot()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            DirectoryInfo? directory = new(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not find AcDream.slnx.");
    }
}
