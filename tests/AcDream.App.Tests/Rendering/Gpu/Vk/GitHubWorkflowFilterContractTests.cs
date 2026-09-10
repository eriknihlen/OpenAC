using System.Text.RegularExpressions;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

/// <summary>
/// Pins the GitHub CI workflow's test filters to the canonical portable filter
/// in <c>tools/run-release-gate.ps1</c>, and requires the Vulkan hardware job
/// to run the complete trait-owned lane. Reads repository text only.
/// </summary>
public sealed class GitHubWorkflowFilterContractTests
{
    [Fact]
    public void PortableFilters_MatchCanonicalReleaseGateWithOnlyTheirNativeLaneEnabled()
    {
        string root = RepositoryRoot();
        string releaseGate = File.ReadAllText(Path.Combine(root, "tools", "run-release-gate.ps1"));
        Match canonical = Regex.Match(
            releaseGate,
            "\\[string\\]\\$TestFilter\\s*=\\s*'(?<filter>[^']+)'",
            RegexOptions.CultureInvariant);
        Assert.True(canonical.Success, "Could not locate the canonical portable filter.");
        string expected = canonical.Groups["filter"].Value;

        string workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
        Match windows = Regex.Match(
            workflow, "\\$filter\\s*=\\s*'(?<filter>[^']+)'", RegexOptions.CultureInvariant);
        Assert.True(windows.Success, "Could not locate the Windows test filter in ci.yml.");
        Assert.Equal(expected, windows.Groups["filter"].Value);

        string linuxBody = JobBody(workflow, "linux-portable", "macos-portable");
        MatchCollection linux = Regex.Matches(
            linuxBody, "--filter\\s+'(?<filter>[^']+)'", RegexOptions.CultureInvariant);
        Assert.Equal(2, linux.Count);
        string expectedLinux = string.Join('&', expected.Split('&').Where(term =>
            term is not "Lane!=Linux" and not "Lane!=Unix"));
        foreach (Match filter in linux)
            Assert.Equal(expectedLinux, filter.Groups["filter"].Value);

        string macBody = JobBody(workflow, "macos-portable", "vulkan-hardware");
        Match mac = Regex.Match(
            macBody,
            "\\$filter\\s*=\\s*'(?<filter>[^']+)'",
            RegexOptions.CultureInvariant);
        Assert.True(mac.Success, "Could not locate the macOS test filter in ci.yml.");
        string expectedMac = string.Join('&', expected.Split('&').Where(term =>
            term is not "Lane!=MacOS" and not "Lane!=Unix"));
        Assert.Equal(expectedMac, mac.Groups["filter"].Value);
    }

    [Fact]
    public void VulkanHardwareJob_RunsTheCompleteLaneOnlyOnPushes()
    {
        string workflow = File.ReadAllText(Path.Combine(
            RepositoryRoot(), ".github", "workflows", "ci.yml"));
        int job = workflow.IndexOf("  vulkan-hardware:", StringComparison.Ordinal);
        int next = workflow.IndexOf("\n  release:", job, StringComparison.Ordinal);
        Assert.True(job >= 0 && next > job, "vulkan-hardware must precede release in ci.yml.");
        string body = workflow[job..next];

        Assert.Contains("if: github.event_name == 'push'", body, StringComparison.Ordinal);
        Assert.Contains("runs-on: openac-windows", body, StringComparison.Ordinal);
        Assert.Contains("--filter \"Lane=Vulkan\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("FullyQualifiedName", body, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static string JobBody(string workflow, string job, string nextJob)
    {
        int start = workflow.IndexOf($"  {job}:", StringComparison.Ordinal);
        int end = workflow.IndexOf($"\n  {nextJob}:", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"Could not locate the {job} job body.");
        return workflow[start..end];
    }
}
