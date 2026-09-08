using System.Diagnostics;

namespace AcDream.App.Tests.Diagnostics;

public sealed class AtmosphericPreviewLauncherContractTests
{
    [Fact]
    public void ScriptParsesWithoutLaunchingTheClient()
    {
        string script = ScriptPath();
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        string quotedScript = "'" + script.Replace("'", "''", StringComparison.Ordinal) + "'";
        start.ArgumentList.Add(
            $"[scriptblock]::Create((Get-Content -Raw -LiteralPath {quotedScript})) | Out-Null");

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start pwsh parser process.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "PowerShell parser did not exit.");
        Assert.True(
            process.ExitCode == 0,
            $"PowerShell parser failed with exit code {process.ExitCode}.\n{stdout}\n{stderr}");
    }

    [Fact]
    public void PreviewIsAudioSafeIsolatedAndDiagnosticByDefault()
    {
        string source = File.ReadAllText(ScriptPath());

        Assert.Contains("[switch]$EnableAudio", source, StringComparison.Ordinal);
        Assert.Contains("$audioEnabled = [bool]$EnableAudio", source, StringComparison.Ordinal);
        Assert.Contains(
            "$env:ACDREAM_NO_AUDIO = if ($audioEnabled) { $null } else { '1' }",
            source,
            StringComparison.Ordinal);
        Assert.Contains("'enabled-explicit'", source, StringComparison.Ordinal);
        Assert.Contains("'disabled-default'", source, StringComparison.Ordinal);

        Assert.Contains(
            ".StartsWith('ACDREAM_', [StringComparison]::OrdinalIgnoreCase)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "[Environment]::SetEnvironmentVariable($name, $null, 'Process')",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "[Environment]::SetEnvironmentVariable($name, $prior[$name], 'Process')",
            source,
            StringComparison.Ordinal);
        AssertAppearsInOrder(
            source,
            "[Environment]::SetEnvironmentVariable($name, $null, 'Process')",
            "$env:ACDREAM_CONFIG_DIR = $config",
            "$process = Start-Process",
            "[Environment]::SetEnvironmentVariable($name, $prior[$name], 'Process')");

        Assert.Contains("schemaVersion = 2", source, StringComparison.Ordinal);
        Assert.Contains("executableSha256", source, StringComparison.Ordinal);
        Assert.Contains("executableProductVersion", source, StringComparison.Ordinal);
        Assert.Contains("stdoutLog = $stdoutLog", source, StringComparison.Ordinal);
        Assert.Contains("stderrLog = $stderrLog", source, StringComparison.Ordinal);
        Assert.Contains("status = 'prepared'", source, StringComparison.Ordinal);
        Assert.Contains("$launch.status = 'start-failed'", source, StringComparison.Ordinal);
        Assert.Contains("$launch.status = 'started'", source, StringComparison.Ordinal);
        Assert.Contains("-RedirectStandardOutput $stdoutLog", source, StringComparison.Ordinal);
        Assert.Contains("-RedirectStandardError $stderrLog", source, StringComparison.Ordinal);
        Assert.DoesNotContain("-WindowStyle Hidden", source, StringComparison.Ordinal);

        Assert.Contains("if (Test-Path -LiteralPath $root)", source, StringComparison.Ordinal);
        Assert.Contains(
            "New-Item -ItemType Directory -Path $config, $data, $cache",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "New-Item -ItemType Directory -Force -Path $config, $data, $cache",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("prior = $prior", source, StringComparison.OrdinalIgnoreCase);
    }

    private static string ScriptPath() => Path.Combine(
        FindRepoRoot(),
        "tools",
        "launch-atmospheric-preview.ps1");

    private static void AssertAppearsInOrder(string source, params string[] fragments)
    {
        int cursor = -1;
        foreach (string fragment in fragments)
        {
            int next = source.IndexOf(fragment, cursor + 1, StringComparison.Ordinal);
            Assert.True(next > cursor, $"Missing or out-of-order fragment: {fragment}");
            cursor = next;
        }
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find AcDream.slnx.");
    }
}
