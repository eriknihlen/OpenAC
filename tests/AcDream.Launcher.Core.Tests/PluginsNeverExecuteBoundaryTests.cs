using System.Runtime.CompilerServices;

namespace AcDream.Launcher.Core.Tests;

/// <summary>Pins the L-300 guarantee that nothing plugin-related loads, reflects over, or starts a
/// downloaded file. A source-text scan needs no project reference to the paths it checks, so it
/// catches the launcher UI files once they exist too.</summary>
public sealed class PluginsNeverExecuteBoundaryTests
{
    private static readonly string[] ForbiddenTokens =
    [
        "Assembly.Load",
        "LoadFrom",
        "LoadFile",
        "AssemblyLoadContext",
        "MetadataLoadContext",
        "PEReader",
        "Process.Start",
        "ProcessStartInfo",
        "new Process",
        "System.Diagnostics.Process",
        "NativeLibrary",
        "Activator.CreateInstance",
        "AppDomain",
        "Type.GetType",
        "GetMethod(\"",
        "InvokeMember",
        "LaunchFileAsync",
        "LaunchUriAsync",
        "Launcher.Launch",
        "UseShellExecute",
    ];

    [Fact]
    public void NoPluginSourceFileUsesAnExecutionOrReflectionApi()
    {
        string repositoryRoot = FindRepositoryRoot();
        var candidates = new List<string>();

        AddDirectory(
            candidates,
            Path.Combine(repositoryRoot, "src", "AcDream.Launcher.Core", "Plugins"),
            "*.cs");

        AddFile(
            candidates,
            repositoryRoot,
            "src",
            "AcDream.Launcher.Core",
            "Updates",
            "SafeZipExtractor.cs");
        AddFile(
            candidates,
            repositoryRoot,
            "src",
            "AcDream.Launcher.Core",
            "Updates",
            "VerifiedArtifactDownloader.cs");
        AddFile(
            candidates,
            repositoryRoot,
            "src",
            "AcDream.Launcher",
            "LauncherPluginComposition.cs");

        string launcherProject = Path.Combine(repositoryRoot, "src", "AcDream.Launcher");
        AddDirectory(candidates, launcherProject, "*Plugin*.cs");
        AddDirectory(candidates, launcherProject, "*Plugin*.axaml.cs");

        candidates = candidates
            .Distinct(StringComparer.Ordinal)
            .Where(path => !IsUnderBuildOutput(path))
            .ToList();
        Assert.NotEmpty(candidates);

        var offenders = new List<string>();
        foreach (string path in candidates)
        {
            string text = File.ReadAllText(path);
            if (ContainsForbiddenToken(text, out string? token))
            {
                offenders.Add($"{Path.GetFileName(path)} uses '{token}'");
            }
        }

        Assert.True(offenders.Count == 0, string.Join("; ", offenders));
    }

    [Theory]
    [MemberData(nameof(ForbiddenTokensData))]
    public void EachForbiddenTokenIsDetectedPlainAndWhitespaceSplit(string token)
    {
        Assert.True(ContainsForbiddenToken($"before {token} after", out string? found));
        Assert.Equal(token, found);

        string splitToken = string.Join(" ", token.ToCharArray());
        Assert.True(
            ContainsForbiddenToken($"before {splitToken} after", out string? foundSplit));
        Assert.Equal(token, foundSplit);
    }

    [Fact]
    public void AUsingStaticImportOfProcessIsDetected()
    {
        const string source =
            "using static System.Diagnostics.Process;\n"
            + "class C { void M() => Start(\"cmd\", \"args\"); }";

        Assert.True(ContainsForbiddenToken(source, out string? token));
        Assert.Equal("System.Diagnostics.Process", token);
    }

    [Fact]
    public void CleanSourceHasNoForbiddenToken()
    {
        const string source = "class C { void M() { var x = 1 + 1; } }";

        Assert.False(ContainsForbiddenToken(source, out _));
    }

    public static TheoryData<string> ForbiddenTokensData()
    {
        var data = new TheoryData<string>();
        foreach (string token in ForbiddenTokens)
        {
            data.Add(token);
        }

        return data;
    }

    private static bool ContainsForbiddenToken(string source, out string? token)
    {
        string stripped = StripWhitespace(source);
        foreach (string candidate in ForbiddenTokens)
        {
            if (stripped.Contains(StripWhitespace(candidate), StringComparison.Ordinal))
            {
                token = candidate;
                return true;
            }
        }

        token = null;
        return false;
    }

    private static string StripWhitespace(string text) =>
        new(text.Where(character => !char.IsWhiteSpace(character)).ToArray());

    private static void AddDirectory(List<string> candidates, string directory, string searchPattern)
    {
        if (Directory.Exists(directory))
        {
            candidates.AddRange(Directory.GetFiles(directory, searchPattern, SearchOption.AllDirectories));
        }
    }

    private static void AddFile(List<string> candidates, params string[] pathParts)
    {
        string path = Path.Combine(pathParts);
        if (File.Exists(path))
        {
            candidates.Add(path);
        }
    }

    private static bool IsUnderBuildOutput(string path)
    {
        string[] segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Contains("bin") || segments.Contains("obj");
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
                if (File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
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
