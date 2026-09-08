using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed class VulkanShaderManifestTests
{
    private static readonly IReadOnlyDictionary<string, string> RetailOracleSpirvSha256 =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["debug_line.frag.spv"] = "02fc04880bc5eb74353566f914675244038125c71443964decdc28e8199264df",
            ["debug_line.vert.spv"] = "f9c6a9b575bb07a426fb6ade677bca96a7752ca6b120e8f6451363ba73b51140",
            ["mesh_modern.frag.spv"] = "bd47fe8a33e0f1d025fe48fd95b034ba6fe591a6d20e88e636c86238d8773db1",
            ["mesh_modern.vert.spv"] = "9909ca4729dbe4fc7fbffb11c73481977d8f593d37f130fcdd44a7803b47944f",
            ["particle.frag.spv"] = "680da227704e0b3afa9b5226a7d73dd65aa9d8759d081cf4d5009d30e148726b",
            ["particle.vert.spv"] = "95ce6ecf834930a92da5c5fe9aef513b38b5ba104704b98c1606af71fe17eaf3",
            ["particle_mesh.frag.spv"] = "7696b1dc0613b5a724c55df465173f613ae047da9675895b149b7c71b009cc7c",
            ["particle_mesh.vert.spv"] = "043482b97c2ed036511692f89c75a0a6c298aba48cb519e5e3aff7fe7ba6371b",
            ["portal_depth.frag.spv"] = "96755196d4d0da7be4792107557465778be2ebefb5584834cc75bf90ec55a6cc",
            ["portal_depth.vert.spv"] = "51c60d0924d62c61548efcf5f9e7672a121b1b68ca0a06755e32f1a4d73a8acf",
            ["sky.frag.spv"] = "1c4ae77056837cbdc188f8cfcc4b0e8851647cdfaf398f25d8c8ff489ef84d57",
            ["sky.vert.spv"] = "7d67a9e3624d198b370d402b5c12e4ce925bf9b8e646ef5123636a86d5985ab5",
            ["terrain_modern.frag.spv"] = "7b3cdb01b837ed77ee20559a81c1ce5c9d5395300efcc072560ab0be3c5a1af9",
            ["terrain_modern.vert.spv"] = "8a73d89ef0e51e550327b9ff8c24857e309103b1d491030cf0d4d8594b45068c",
            ["ui_text.frag.spv"] = "37a281bf80441cb425eaa3ad8e0b3a43cfa21b74b60973ed4201718b9dc102df",
            ["ui_text.vert.spv"] = "018ac64477cf7d4c3fc0c5878951b148c7bfeb6ee3a7eebb02381d7904877798",
            ["vk_probe.frag.spv"] = "c2dedbcc6dcc89744707b4b47138f1c31b38ef9088e584f1da07dd6953586c42",
            ["vk_probe.vert.spv"] = "6c3260b45644033d607727cbd2e11fb4f60eb4a5b18bfd0997710f2ca518a023",
        };

    private sealed record StageEntry(string Stage, string SourceSha256, bool Compiled, string? Message);

    private sealed record ShaderEntry(string Name, bool VulkanReady, IReadOnlyList<StageEntry> Stages);

    private sealed record Manifest(string Note, IReadOnlyList<ShaderEntry> Shaders);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root from the test binary.");
    }

    private static string ShadersDirectory() =>
        Path.Combine(RepositoryRoot(), "src", "AcDream.App", "Rendering", "Shaders");

    private static string SpirvDirectory() => Path.Combine(ShadersDirectory(), "spv");

    private static Manifest ReadManifest()
    {
        string path = Path.Combine(SpirvDirectory(), "shaders.manifest.json");
        Assert.True(File.Exists(path), $"The shader manifest is missing at {path}. Run tools/compile-shaders.ps1.");
        return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException("The shader manifest could not be parsed.");
    }

    private static string Sha256OfSource(string path)
    {
        string text = File.ReadAllText(path);
        if (text.Contains("#include \"", StringComparison.Ordinal))
        {
            text = ExpandIncludes(
                text,
                Path.GetDirectoryName(path)!,
                []);
        }
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static string ExpandIncludes(
        string source,
        string sourceDirectory,
        HashSet<string> active)
    {
        var output = new StringBuilder();
        foreach (string line in source.Replace("\r\n", "\n").Split('\n'))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("#include \"", StringComparison.Ordinal)
                || !trimmed.EndsWith('"'))
            {
                output.AppendLine(line);
                continue;
            }
            string key = trimmed[10..^1];
            Assert.DoesNotContain("..", key, StringComparison.Ordinal);
            Assert.DoesNotContain('\\', key);
            string include = Path.GetFullPath(Path.Combine(sourceDirectory, key));
            Assert.StartsWith(
                Path.GetFullPath(sourceDirectory) + Path.DirectorySeparatorChar,
                include,
                StringComparison.Ordinal);
            Assert.True(File.Exists(include), $"GLSL include '{key}' is missing.");
            Assert.True(active.Add(include), $"GLSL include '{key}' is cyclic.");
            output.AppendLine($"// ---- begin include: {key} ----");
            output.Append(ExpandIncludes(File.ReadAllText(include), sourceDirectory, active));
            output.AppendLine($"// ---- end include: {key} ----");
            active.Remove(include);
        }
        return output.ToString();
    }

    [Fact]
    public void EveryGlslPairIsRecordedInTheManifest()
    {
        Manifest manifest = ReadManifest();
        string[] pairs = Directory
            .EnumerateFiles(ShadersDirectory(), "*.vert")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null && File.Exists(Path.Combine(ShadersDirectory(), $"{name}.frag")))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(pairs, manifest.Shaders.Select(shader => shader.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void PreCampaignRetailSpirvBinariesRemainByteExact()
    {
        foreach ((string fileName, string expectedSha256) in RetailOracleSpirvSha256)
        {
            string path = Path.Combine(SpirvDirectory(), fileName);
            Assert.True(File.Exists(path), $"Retail shader oracle '{fileName}' is missing.");
            string actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            Assert.Equal(expectedSha256, actual);
        }
    }

    [Fact]
    public void CommittedSpirvIsNotStaleAgainstItsGlslSource()
    {
        Manifest manifest = ReadManifest();
        var stale = new List<string>();

        foreach (ShaderEntry shader in manifest.Shaders)
        {
            foreach (StageEntry stage in shader.Stages)
            {
                string source = Path.Combine(ShadersDirectory(), $"{shader.Name}.{stage.Stage}");
                if (!File.Exists(source))
                {
                    stale.Add($"{shader.Name}.{stage.Stage}: the GLSL source no longer exists");
                    continue;
                }

                string actual = Sha256OfSource(source);
                if (!string.Equals(actual, stage.SourceSha256, StringComparison.Ordinal))
                    stale.Add($"{shader.Name}.{stage.Stage}: source changed since the .spv was built");
            }
        }

        Assert.True(
            stale.Count == 0,
            "Committed SPIR-V is out of date. Run tools/compile-shaders.ps1 and commit the result.\n  "
                + string.Join("\n  ", stale));
    }

    [Fact]
    public void EveryShaderTheManifestCallsReadyHasBothSpirvArtifacts()
    {
        Manifest manifest = ReadManifest();
        foreach (ShaderEntry shader in manifest.Shaders.Where(entry => entry.VulkanReady))
        {
            foreach (string stage in (string[])["vert", "frag"])
            {
                string path = Path.Combine(SpirvDirectory(), $"{shader.Name}.{stage}.spv");
                Assert.True(File.Exists(path), $"{shader.Name} is marked Vulkan-ready but {path} is missing.");
                long length = new FileInfo(path).Length;
                Assert.True(length > 0 && length % 4 == 0, $"{path} is not a whole number of SPIR-V words.");
            }
        }
    }

    [Fact]
    public void ShadersTheManifestCallsUnreadyHaveNoStaleSpirvLeftBehind()
    {
        Manifest manifest = ReadManifest();
        foreach (ShaderEntry shader in manifest.Shaders.Where(entry => !entry.VulkanReady))
        {
            foreach (string stage in (string[])["vert", "frag"])
            {
                string path = Path.Combine(SpirvDirectory(), $"{shader.Name}.{stage}.spv");
                // A leftover .spv from an earlier attempt would be loaded
                // happily by the device and would be a shader nobody can account
                // for.
                Assert.False(File.Exists(path), $"{shader.Name} is not Vulkan-ready but {path} exists.");
            }
        }
    }

    [Fact]
    public void EveryUnreadyShaderRecordsWhyItCannotBeCompiledYet()
    {
        Manifest manifest = ReadManifest();
        foreach (ShaderEntry shader in manifest.Shaders.Where(entry => !entry.VulkanReady))
        {
            Assert.Contains(shader.Stages, stage => !stage.Compiled && !string.IsNullOrWhiteSpace(stage.Message));
        }
    }

    [Fact]
    public void TheRhiVerificationShaderIsCompiled()
    {
        Manifest manifest = ReadManifest();
        ShaderEntry probe = Assert.Single(
            manifest.Shaders,
            shader => string.Equals(shader.Name, "vk_probe", StringComparison.Ordinal));

        Assert.True(probe.VulkanReady, "vk_probe must compile — the whole Vulkan backend draws with it.");
    }

    [Fact]
    public void PortalDepthVert_FarPunchConstant_MatchesRetailExactBits()
    {
        string source = File.ReadAllText(Path.Combine(ShadersDirectory(), "portal_depth.vert"));
        string line = source
            .Split('\n')
            .Select(l => l.Trim())
            .SingleOrDefault(l => l.StartsWith("clipPos.z = clipPos.w * ", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "portal_depth.vert no longer has a 'clipPos.z = clipPos.w * <literal>;' punch "
                + "line for T1 to read — did the far-Z punch assignment move or get restructured?");

        uint bits = ParsePunchLiteralBits(line);
        Assert.Equal(0x3F7FFFEFu, bits);
    }

    private static uint ParsePunchLiteralBits(string assignmentLine)
    {
        const string prefix = "clipPos.z = clipPos.w * ";
        string rhs = assignmentLine[prefix.Length..];
        int commentStart = rhs.IndexOf("//", StringComparison.Ordinal);
        if (commentStart >= 0)
            rhs = rhs[..commentStart];
        rhs = rhs.Trim().TrimEnd(';', ' ');

        Match hexMatch = Regex.Match(rhs, @"uintBitsToFloat\(\s*0x([0-9A-Fa-f]+)u?\s*\)");
        if (hexMatch.Success)
            return Convert.ToUInt32(hexMatch.Groups[1].Value, 16);

        string literal = rhs.TrimEnd('f', 'F');
        if (float.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            return unchecked((uint)BitConverter.SingleToInt32Bits(value));

        throw new InvalidOperationException(
            $"portal_depth.vert's punch literal '{rhs}' is neither a uintBitsToFloat(0x...) call "
            + "nor a plain float literal T1 knows how to reinterpret as bits.");
    }
}
