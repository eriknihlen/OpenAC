using System.Text.Json;
using System.Xml.Linq;
using System.Runtime.CompilerServices;

namespace AcDream.Runtime.Tests;

public sealed class RuntimeDependencyBoundaryTests
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

    [Fact]
    public void RuntimeAssemblyHasNoDirectPresentationOrBackendReferences()
    {
        var references = typeof(RuntimeAssemblyMarker).Assembly
            .GetReferencedAssemblies()
            .Select(static reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, IsForbidden);
    }

    [Fact]
    public void RuntimeDependencyClosureHasNoPresentationOrBackendLibraries()
    {
        var depsPath = Path.ChangeExtension(
            typeof(RuntimeDependencyBoundaryTests).Assembly.Location,
            ".deps.json");

        using var document = JsonDocument.Parse(File.ReadAllText(depsPath));
        var libraries = document.RootElement
            .GetProperty("libraries")
            .EnumerateObject()
            .Select(static library => LibraryName(library.Name))
            .ToArray();

        Assert.DoesNotContain(libraries, IsForbidden);
    }

    [Fact]
    public void RuntimeProjectDeclaresOnlyApprovedProjectDependencies()
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(
            repositoryRoot,
            "src",
            "AcDream.Runtime",
            "AcDream.Runtime.csproj");
        var project = XDocument.Load(projectPath);
        var projectDirectory = Path.GetDirectoryName(projectPath)!;

        var actualReferences = project
            .Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .Where(static include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFullPath(Path.Combine(
                projectDirectory,
                include!.Replace(
                    '\\',
                    Path.DirectorySeparatorChar))))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var expectedReferences = new[]
            {
                "AcDream.Content",
                "AcDream.Core",
                "AcDream.Core.Net",
                "AcDream.Platform",
                "AcDream.Plugin.Abstractions",
            }
            .Select(projectName => Path.Combine(
                repositoryRoot,
                "src",
                projectName,
                $"{projectName}.csproj"))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(expectedReferences, actualReferences);
        Assert.Empty(project.Descendants("PackageReference"));
    }

    [Fact]
    public void LoadingRuntimeMarkerDoesNotLoadPresentationOrBackendAssemblies()
    {
        _ = typeof(RuntimeAssemblyMarker).Assembly;

        var loadedAssemblies = AppDomain.CurrentDomain
            .GetAssemblies()
            .Select(static assembly => assembly.GetName().Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(loadedAssemblies, IsForbidden);
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

            var directory = new DirectoryInfo(start);
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
            "Could not find AcDream.slnx above the source, working, or output directory.");
    }

    private static string LibraryName(string libraryIdentity)
    {
        var separatorIndex = libraryIdentity.IndexOf('/');
        return separatorIndex < 0
            ? libraryIdentity
            : libraryIdentity[..separatorIndex];
    }

    private static bool IsForbidden(string assemblyName)
    {
        return ForbiddenDependencyPrefixes.Any(prefix =>
            assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
