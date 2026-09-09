using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Gpu.Vk;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed class VulkanShaderDescriptorContractTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root from the test binary.");
    }

    private static string SpirvDirectory() =>
        Path.Combine(RepositoryRoot(), "src", "AcDream.App", "Rendering", "Shaders", "spv");

    private readonly record struct ShaderResource(
        string Module,
        uint Id,
        SpirvStorageClass StorageClass,
        uint? Set,
        uint? Binding);

    private enum SpirvStorageClass : uint
    {
        UniformConstant = 0,
        Uniform = 2,
        StorageBuffer = 12,
    }

    private static IReadOnlyList<ShaderResource> ReadResources(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= 20 && bytes.Length % 4 == 0, $"{path} is not a SPIR-V module.");
        var words = new uint[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, words, 0, bytes.Length);
        Assert.Equal(0x07230203u, words[0]);

        const uint OpDecorate = 71;
        const uint OpVariable = 59;
        const uint DecorationBinding = 33;
        const uint DecorationDescriptorSet = 34;

        var sets = new Dictionary<uint, uint>();
        var bindings = new Dictionary<uint, uint>();
        var variables = new List<(uint Id, SpirvStorageClass StorageClass)>();

        int index = 5;
        while (index < words.Length)
        {
            uint header = words[index];
            int wordCount = (int)(header >> 16);
            uint opcode = header & 0xFFFF;
            Assert.True(wordCount > 0, $"{path} has a zero-length instruction at word {index}.");

            if (opcode == OpDecorate && wordCount >= 4)
            {
                uint target = words[index + 1];
                uint decoration = words[index + 2];
                if (decoration == DecorationDescriptorSet)
                    sets[target] = words[index + 3];
                else if (decoration == DecorationBinding)
                    bindings[target] = words[index + 3];
            }
            else if (opcode == OpVariable && wordCount >= 4)
            {
                variables.Add((words[index + 2], (SpirvStorageClass)words[index + 3]));
            }

            index += wordCount;
        }

        string module = Path.GetFileName(path);
        return
        [
            .. variables.Select(v => new ShaderResource(
                module,
                v.Id,
                v.StorageClass,
                sets.TryGetValue(v.Id, out uint s) ? s : null,
                bindings.TryGetValue(v.Id, out uint b) ? b : null)),
        ];
    }

    private static IReadOnlyList<ShaderResource> AllResources() =>
    [
        .. Directory
            .EnumerateFiles(SpirvDirectory(), "*.spv")
            .OrderBy(path => path, StringComparer.Ordinal)
            .SelectMany(ReadResources),
    ];

    private static bool IsOptInPackShaderModule(string module) =>
        module.StartsWith("atmospheric_", StringComparison.Ordinal)
        || module.StartsWith("directional_shadow_", StringComparison.Ordinal)
        || module.StartsWith("mesh_atmospheric.", StringComparison.Ordinal)
        || module.StartsWith("terrain_atmospheric.", StringComparison.Ordinal);

    [Fact]
    public void TerrainVertexShaderDeclaresOnlySceneLightingInTheUniformSet()
    {
        uint[] uniformBindings =
        [
            .. ReadResources(Path.Combine(SpirvDirectory(), "terrain_modern.vert.spv"))
                .Where(r => r.StorageClass == SpirvStorageClass.Uniform)
                .Select(r =>
                {
                    Assert.Equal(GpuBindingModel.UniformSet, r.Set);
                    return r.Binding!.Value;
                })
                .Order(),
        ];

        Assert.Equal([GpuBindingModel.UniformSceneLighting], uniformBindings);
    }

    [Fact]
    public void EveryUniformBlockLandsAtADeclaredUniformBinding()
    {
        string[] violations =
        [
            .. AllResources()
                .Where(r => r.StorageClass == SpirvStorageClass.Uniform)
                .Where(r =>
                    r.Binding is null
                    || !(r.Set == GpuBindingModel.UniformSet
                            && VulkanPipelineLayouts.IsDeclaredUniformBinding(r.Binding.Value))
                        && !(r.Set == GpuBindingModel.RenderPackUniformSet
                            && VulkanPipelineLayouts.IsDeclaredPackUniformBinding(r.Binding.Value)))
                .Select(r =>
                    $"{r.Module}: uniform block %{r.Id} is at set {r.Set?.ToString() ?? "(none)"} "
                    + $"binding {r.Binding?.ToString() ?? "(none)"}; retail set 1 declares "
                    + $"[{string.Join(", ", VulkanPipelineLayouts.DeclaredUniformBindings)}] "
                    + "and opt-in set 3 declares [5, 6, 7, 8]."),
        ];

        Assert.Empty(violations);
    }

    [Fact]
    public void RetailShaderModulesNeverDeclareOptInSetThree()
    {
        string[] violations =
        [
            .. AllResources()
                .Where(r => r.Set == GpuBindingModel.RenderPackUniformSet)
                .Where(r => !IsOptInPackShaderModule(r.Module))
                .Select(r =>
                    $"{r.Module}: resource %{r.Id} declares opt-in set 3 binding {r.Binding}."),
        ];

        Assert.Empty(violations);
    }

    [Fact]
    public void EveryStorageBlockLandsInTheStorageSetWithinItsDeclaredRange()
    {
        string[] violations =
        [
            .. AllResources()
                .Where(r => r.StorageClass == SpirvStorageClass.StorageBuffer)
                .Where(r => r.Set != 0 || r.Binding is null || r.Binding >= GpuBindingModel.StorageBindingCount)
                .Select(r =>
                    $"{r.Module}: storage block %{r.Id} is at set {r.Set?.ToString() ?? "(none)"} "
                    + $"binding {r.Binding?.ToString() ?? "(none)"}; set 0 declares bindings "
                    + $"0..{GpuBindingModel.StorageBindingCount - 1}."),
        ];

        Assert.Empty(violations);
    }

    [Fact]
    public void EverySampledTextureIsTheSharedTable()
    {
        string[] violations =
        [
            .. AllResources()
                .Where(r => r.StorageClass == SpirvStorageClass.UniformConstant && r.Set is not null)
                .Where(r => r.Set != GpuBindingModel.TextureTableSet || r.Binding != GpuBindingModel.TextureTableBinding)
                .Select(r =>
                    $"{r.Module}: sampled resource %{r.Id} is at set {r.Set} binding "
                    + $"{r.Binding?.ToString() ?? "(none)"}; the only declared table is set "
                    + $"{GpuBindingModel.TextureTableSet} binding {GpuBindingModel.TextureTableBinding}."),
        ];

        Assert.Empty(violations);
    }
}
