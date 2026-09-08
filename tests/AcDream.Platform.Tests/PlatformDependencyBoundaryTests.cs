using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace AcDream.Platform.Tests;

public sealed class PlatformDependencyBoundaryTests
{
    [Fact]
    public void PlatformProjectDeclaresNoProjectOrPackageDependencies()
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(
            repositoryRoot,
            "src",
            "AcDream.Platform",
            "AcDream.Platform.csproj");
        var project = XDocument.Load(projectPath);

        Assert.Empty(project.Descendants("ProjectReference"));
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
