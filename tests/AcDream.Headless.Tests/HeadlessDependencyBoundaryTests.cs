using System.Text.Json;
using System.Xml.Linq;
using System.Runtime.CompilerServices;

namespace AcDream.Headless.Tests;

public sealed class HeadlessDependencyBoundaryTests
{
    private static readonly string[] ForbiddenDependencyPrefixes =
    [
        "AcDream.App",
        "AcDream.UI.",
        "Silk.NET",
        "OpenAL",
        "Arch",
        "ImGui",
    ];

    /// <summary>
    /// The runtime, the automation surface plugins talk to (shared with the
    /// graphical client, built over the runtime alone), and DrakBot's engine
    /// so a headless session can run the bot. The engine speaks the plugin
    /// contract only; its ImGui windows are a separate assembly the headless
    /// host never references.
    /// </summary>
    [Fact]
    public void HeadlessAssemblyReferencesOnlyTheRuntimeAutomationAndBotProjects()
    {
        string repositoryRoot = FindRepositoryRoot();
        string projectPath = Path.Combine(
            repositoryRoot,
            "src",
            "AcDream.Headless",
            "AcDream.Headless.csproj");
        var project = XDocument.Load(projectPath);
        string projectDirectory = Path.GetDirectoryName(projectPath)!;

        string[] actual = project
            .Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .Where(static include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFullPath(Path.Combine(
                projectDirectory,
                include!.Replace(
                    '\\',
                    Path.DirectorySeparatorChar))))
            .ToArray();
        string[] expected =
        [
            Path.Combine(
                repositoryRoot,
                "src",
                "AcDream.Runtime",
                "AcDream.Runtime.csproj"),
            Path.Combine(
                repositoryRoot,
                "src",
                "AcDream.Automation",
                "AcDream.Automation.csproj"),
            Path.Combine(
                repositoryRoot,
                "src",
                "AcDream.DrakBot",
                "AcDream.DrakBot.csproj"),
            Path.Combine(
                repositoryRoot,
                "src",
                "AcDream.DrakBot.Remote",
                "AcDream.DrakBot.Remote.csproj"),
        ];

        Assert.Equal(expected, actual);
        Assert.Empty(project.Descendants("PackageReference"));
    }

    [Fact]
    public void HeadlessDependencyClosureHasNoPresentationOrBackendLibraries()
    {
        string depsPath = Path.ChangeExtension(
            typeof(HeadlessDependencyBoundaryTests).Assembly.Location,
            ".deps.json");

        using JsonDocument document =
            JsonDocument.Parse(File.ReadAllText(depsPath));
        string[] libraries = document.RootElement
            .GetProperty("libraries")
            .EnumerateObject()
            .Select(static library => LibraryName(library.Name))
            .ToArray();

        Assert.DoesNotContain(libraries, IsForbidden);
    }

    [Fact]
    public void HelpPathLoadsNoPresentationOrBackendAssembly()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(
            0,
            HeadlessEntryPoint.Run(["--help"], output, error));

        string[] loaded = AppDomain.CurrentDomain
            .GetAssemblies()
            .Select(static assembly =>
                assembly.GetName().Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(loaded, IsForbidden);
    }

    private static string FindRepositoryRoot(
        [CallerFilePath] string sourcePath = "")
    {
        string[] starts =
        {
            Path.GetDirectoryName(sourcePath) ?? string.Empty,
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
        };
        foreach (string start in starts)
        {
            if (string.IsNullOrEmpty(start))
            {
                continue;
            }

            DirectoryInfo? directory = new(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(
                        directory.FullName,
                        "AcDream.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not find AcDream.slnx above the working or output directory.");
    }

    private static string LibraryName(string libraryIdentity)
    {
        int separatorIndex = libraryIdentity.IndexOf('/');
        return separatorIndex < 0
            ? libraryIdentity
            : libraryIdentity[..separatorIndex];
    }

    private static bool IsForbidden(string assemblyName) =>
        ForbiddenDependencyPrefixes.Any(prefix =>
            assemblyName.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase));
}
