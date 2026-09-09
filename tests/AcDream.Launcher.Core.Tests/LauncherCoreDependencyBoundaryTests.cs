using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace AcDream.Launcher.Core.Tests;

public sealed class LauncherCoreDependencyBoundaryTests
{
    [Fact]
    public void LauncherCoreProjectReferencesOnlyPlatformAndDeclaresNoPackages()
    {
        string repositoryRoot = FindRepositoryRoot();
        string projectPath = Path.Combine(
            repositoryRoot,
            "src",
            "AcDream.Launcher.Core",
            "AcDream.Launcher.Core.csproj");
        var project = XDocument.Load(projectPath);

        var projectReferences = project.Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Select(include => include is null
                ? null
                : Path.GetFileName(include.Replace('\\', '/')))
            .ToList();

        Assert.Equal(["AcDream.Platform.csproj"], projectReferences);
        Assert.Empty(project.Descendants("PackageReference"));
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
}
