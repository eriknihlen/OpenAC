using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AcDream.App.Tests.Release;

/// <summary>
/// Pins what the launcher fingerprint covers. A launcher whose fingerprint matches a release's does
/// not update itself, so the fingerprint follows the launcher's own code, the bake tool's own
/// project, the prepared-data recipe version, the build files, the SDK and runtime and the publish
/// flags, and not the shared game code: an older launcher's bake tool prepares valid data until the
/// recipe version goes up. These run the script itself, on trees built from the committed one with
/// a single file changed (git objects only; no branch, index or working file is touched).
/// </summary>
public sealed class LauncherFingerprintContractTests
{
    [Fact]
    public void TheInputsAreTheLaunchersOwnCodeTheRecipeAndTheBuildNotTheGameCode()
    {
        Dictionary<string, string> inputs = ListInputs("HEAD");

        foreach (string expected in new[]
                 {
                     "src/AcDream.Launcher", "src/AcDream.Launcher.Core", "src/AcDream.Platform", "src/AcDream.Bake",
                     "recipe.CurrentFormatVersion", "recipe.CurrentBakeToolVersion",
                     "assets/icons", "global.json", "Directory.Packages.props", "NuGet.Config", "Directory.Build.props",
                     "tools/publish-bin.ps1", "tools/package-macos-launcher.ps1", "tools/launcher-fingerprint.ps1",
                     "dotnet-sdk", "dotnet-runtime", "publish-flags", "workflow-publish",
                 })
        {
            Assert.True(inputs.ContainsKey(expected), $"The launcher fingerprint does not cover {expected}.");
        }

        Assert.DoesNotContain("src/AcDream.Core", inputs.Keys);
        Assert.DoesNotContain("src/AcDream.Content", inputs.Keys);
        Assert.Matches(@"^\d+\.\d+\.\d+", inputs["dotnet-sdk"]);
        Assert.Matches(@"^\d+\.\d+\.\d+", inputs["dotnet-runtime"]);
        Assert.Contains("--self-contained true", inputs["publish-flags"], StringComparison.Ordinal);
        Assert.Contains("publish-bin.ps1", inputs["workflow-publish"], StringComparison.Ordinal);
        Assert.DoesNotContain("-Version", inputs["workflow-publish"], StringComparison.Ordinal);
        Assert.Contains("<Version />", inputs["Directory.Build.props"], StringComparison.Ordinal);
    }

    [Fact]
    public void GameCodeAndTheReleaseVersionLeaveItAndTheLauncherOrTheRecipeChangeIt()
    {
        string head = Git("rev-parse", "HEAD^{tree}").Trim();
        string fingerprint = Fingerprint(head);
        string pak = Git("show", "HEAD:src/AcDream.Content/Pak/PakFormat.cs");
        string props = Git("show", "HEAD:Directory.Build.props");

        Assert.Equal(fingerprint, Fingerprint(head));
        Assert.Equal(fingerprint, Fingerprint(WithFile(head, "src/AcDream.Core/AcDream.Core.csproj",
            Git("show", "HEAD:src/AcDream.Core/AcDream.Core.csproj") + "\n<!-- a game code change -->\n")));
        Assert.Equal(fingerprint, Fingerprint(WithFile(head, "src/AcDream.Content/Pak/PakFormat.cs",
            pak + "\n// elsewhere in the content code\n")));
        Assert.Equal(fingerprint, Fingerprint(WithFile(head, "Directory.Build.props",
            Regex.Replace(props, "<Version>[^<]*</Version>", "<Version>99.0.0</Version>"))));

        Assert.NotEqual(fingerprint, Fingerprint(WithFile(head, "src/AcDream.Launcher/App.axaml.cs",
            Git("show", "HEAD:src/AcDream.Launcher/App.axaml.cs") + "\n// a launcher change\n")));
        Assert.NotEqual(fingerprint, Fingerprint(WithFile(head, "src/AcDream.Bake/AcDream.Bake.csproj",
            Git("show", "HEAD:src/AcDream.Bake/AcDream.Bake.csproj") + "\n<!-- a bake tool change -->\n")));
        Assert.NotEqual(fingerprint, Fingerprint(WithFile(head, "src/AcDream.Content/Pak/PakFormat.cs",
            Regex.Replace(pak, @"(CurrentBakeToolVersion\s*=\s*)(\d+)", match =>
                match.Groups[1].Value + (int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) + 1)))));
    }

    [Fact]
    public void ThePublishScriptEmbedsAndPublishesThisFingerprint()
    {
        string publish = File.ReadAllText(Path.Combine(RepositoryRoot(), "tools", "publish-bin.ps1"));

        Assert.Contains(". (Join-Path $PSScriptRoot 'launcher-fingerprint.ps1')", publish, StringComparison.Ordinal);
        Assert.Contains("$LauncherFingerprint = Get-LauncherFingerprint", publish, StringComparison.Ordinal);
        Assert.Contains(") + $CommonPublishFlags", publish, StringComparison.Ordinal);
        Assert.Contains("-Fingerprint $LauncherFingerprint", publish, StringComparison.Ordinal);
        Assert.Contains("launcher-fingerprint.json", publish, StringComparison.Ordinal);
    }

    /// <summary>The tree <paramref name="baseTree"/> with one file replaced, written as git objects
    /// through a private index file.</summary>
    private static string WithFile(string baseTree, string path, string content)
    {
        string blob = Git(["hash-object", "-w", "--stdin"], stdin: content).Trim();
        string index = Path.Combine(Path.GetTempPath(), "acdream-fingerprint-index-" + Guid.NewGuid().ToString("N"));
        try
        {
            var environment = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index };
            Git(["read-tree", baseTree], environment: environment);
            Git(["update-index", "--add", "--cacheinfo", $"100644,{blob},{path}"], environment: environment);
            return Git(["write-tree"], environment: environment).Trim();
        }
        finally
        {
            File.Delete(index);
        }
    }

    private static string Fingerprint(string revision)
    {
        string output = Run("-Revision", revision).Trim();
        Assert.Matches("^[0-9a-f]{64}$", output);
        return output;
    }

    private static Dictionary<string, string> ListInputs(string revision) =>
        Run("-ListInputs", "-Revision", revision)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\t', 2))
            .ToDictionary(parts => parts[0], parts => parts.Length > 1 ? parts[1] : "", StringComparer.Ordinal);

    private static string Run(params string[] arguments)
    {
        string script = Path.Combine(RepositoryRoot(), "tools", "launcher-fingerprint.ps1");
        Assert.True(File.Exists(script), $"Missing launcher fingerprint script: {script}");
        return Execute("pwsh", ["-NoProfile", "-NonInteractive", "-File", script, .. arguments]);
    }

    private static string Git(params string[] arguments) => Git(arguments, stdin: null);

    private static string Git(string[] arguments, string? stdin = null, Dictionary<string, string>? environment = null) =>
        Execute("git", ["-C", RepositoryRoot(), .. arguments], stdin, environment);

    private static string Execute(
        string program,
        IEnumerable<string> arguments,
        string? stdin = null,
        Dictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            WorkingDirectory = RepositoryRoot(),
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach ((string name, string value) in environment ?? [])
        {
            startInfo.Environment[name] = value;
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {program}.");
        if (stdin is not null)
        {
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();
        }

        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(120_000), $"{program} did not finish within two minutes.");
        Assert.True(process.ExitCode == 0, $"{program} failed: {error.Result}");
        return output;
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
