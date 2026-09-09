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
    public void PortableFilters_MatchCanonicalReleaseGateWithOnlyLinuxLaneEnabledOnLinux()
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

        MatchCollection linux = Regex.Matches(
            workflow, "--filter\\s+'(?<filter>[^']+)'", RegexOptions.CultureInvariant);
        Assert.Equal(2, linux.Count);
        string expectedLinux = string.Join('&', expected.Split('&').Where(term => term != "Lane!=Linux"));
        foreach (Match filter in linux)
            Assert.Equal(expectedLinux, filter.Groups["filter"].Value);
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
}
