using System.Text.RegularExpressions;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed class VulkanLaneOwnershipContractTests
{
    private const string WitnessMethod =
        "CommittedProductionOrdinaryShader_RendersLocalSidecarsAtNonzeroTransformPrefix";

    [Fact]
    public void HardwareWitness_PortableFilterAndLavapipeOwnerStayAligned()
    {
        string root = RepositoryRoot();
        string witness = File.ReadAllText(Path.Combine(
            root,
            "tests", "AcDream.App.Tests", "Rendering", "Gpu", "Vk",
            "MeshModernSharedIndexOffscreenTests.cs"));
        Assert.Matches(
            new Regex(
                "\\[Trait\\(\"Lane\", \"Vulkan\"\\)\\]\\s*"
                + "\\[Fact\\]\\s*public void " + WitnessMethod + "\\(",
                RegexOptions.CultureInvariant),
            witness);
        Assert.Equal(1, Count(witness, "[Trait(\"Lane\", \"Vulkan\")]"));

        string releaseGate = File.ReadAllText(Path.Combine(root, "tools", "run-release-gate.ps1"));
        Match defaultFilter = Regex.Match(
            releaseGate,
            "\\[string\\]\\$TestFilter\\s*=\\s*'(?<filter>[^']+)'",
            RegexOptions.CultureInvariant);
        Assert.True(defaultFilter.Success, "Could not locate the portable Release gate's default filter.");
        Assert.Contains("Lane!=Vulkan", defaultFilter.Groups["filter"].Value, StringComparison.Ordinal);
        Assert.Equal(1, Count(defaultFilter.Groups["filter"].Value, "Lane!=Vulkan"));

        string workflow = File.ReadAllText(Path.Combine(
            root, ".github", "workflows", "headless-portability.yml"));
        int install = workflow.IndexOf("- name: Install lavapipe, the Vulkan loader and Xvfb", StringComparison.Ordinal);
        int portable = workflow.IndexOf("- name: Test the Vulkan backend's platform-independent decisions", StringComparison.Ordinal);
        int hardware = workflow.IndexOf("- name: Test the Vulkan hardware lane on lavapipe", StringComparison.Ordinal);
        Assert.True(install >= 0 && install < portable && portable < hardware);

        string portableStep = Step(workflow, portable);
        Assert.Contains(
            "--filter \"FullyQualifiedName~AcDream.App.Tests.Rendering.Gpu.Vk&Lane!=Vulkan\"",
            portableStep,
            StringComparison.Ordinal);

        string hardwareStep = Step(workflow, hardware);
        Assert.DoesNotContain("FullyQualifiedName", hardwareStep, StringComparison.Ordinal);
        Assert.DoesNotContain(WitnessMethod, hardwareStep, StringComparison.Ordinal);
        Assert.Contains("--filter \"Lane=Vulkan\"", hardwareStep, StringComparison.Ordinal);
    }

    private static string Step(string workflow, int start)
    {
        int next = workflow.IndexOf("      - name:", start + 1, StringComparison.Ordinal);
        return next < 0 ? workflow[start..] : workflow[start..next];
    }

    private static int Count(string value, string needle)
    {
        int count = 0;
        for (int index = 0; (index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0;)
        {
            count++;
            index += needle.Length;
        }
        return count;
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
