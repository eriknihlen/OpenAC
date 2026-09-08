using AcDream.App.Rendering.Packs;
using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.App.Tests.Rendering.Packs;

public sealed class RenderPackSpirvValidatorTests
{
    [Fact]
    public void BuiltInDescriptorValidatesSelectedCelestialContract()
    {
        RenderPackValidationResult result = RenderPackValidator.ValidateDescriptor(
            BuiltInAtmosphericRenderPack.Descriptor,
            RenderPackHostCapabilities.Conformance);

        Assert.True(result.Success, result.Reason);
    }

    [Fact]
    public void SelectedCelestialSemanticRequiresAuthoredCelestialCapability()
    {
        RenderPackDescriptor descriptor = BuiltInAtmosphericRenderPack.Descriptor;
        descriptor = descriptor with
        {
            RequiredCapabilities = descriptor.RequiredCapabilities
                .Where(static capability =>
                    capability != RenderCapability.AuthoredCelestialDirectionalLight)
                .ToArray(),
        };

        RenderPackValidationResult result = RenderPackValidator.ValidateDescriptor(
            descriptor,
            RenderPackHostCapabilities.Conformance);

        Assert.False(result.Success);
        Assert.Equal(
            $"Pack '{descriptor.Id}' declares semantic "
            + $"'{RenderSemanticInput.SelectedCelestialDirectionalLight}' but does not "
            + $"require capability '{RenderCapability.AuthoredCelestialDirectionalLight}'.",
            result.Reason);
    }


    [Fact]
    public void FoliageWindByWeatherRejectsAnUnknownWeatherKindName()
    {
        RenderPackDescriptor descriptor = BuiltInAtmosphericRenderPack.Descriptor;
        descriptor = descriptor with
        {
            AtmospherePolicy = descriptor.AtmospherePolicy! with
            {
                FoliageWindByWeather =
                [
                    new FoliageWindWeatherPoint("Cloudy", 0.45, 0.30),
                ],
            },
        };

        RenderPackValidationResult result = RenderPackValidator.ValidateDescriptor(
            descriptor,
            RenderPackHostCapabilities.Conformance);

        Assert.False(result.Success);
        Assert.Contains("unknown foliage-wind weather", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FoliageWindByWeatherRejectsANonExactCaseWeatherKindName()
    {
        RenderPackDescriptor descriptor = BuiltInAtmosphericRenderPack.Descriptor;
        descriptor = descriptor with
        {
            AtmospherePolicy = descriptor.AtmospherePolicy! with
            {
                FoliageWindByWeather = [new FoliageWindWeatherPoint("clear", 0.25, 0.15)],
            },
        };

        RenderPackValidationResult result = RenderPackValidator.ValidateDescriptor(
            descriptor,
            RenderPackHostCapabilities.Conformance);

        Assert.False(result.Success);
        Assert.Contains("unknown foliage-wind weather", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FoliageWindByWeatherRejectsADuplicateWeatherKind()
    {
        RenderPackDescriptor descriptor = BuiltInAtmosphericRenderPack.Descriptor;
        descriptor = descriptor with
        {
            AtmospherePolicy = descriptor.AtmospherePolicy! with
            {
                FoliageWindByWeather =
                [
                    new FoliageWindWeatherPoint("Clear", 0.25, 0.15),
                    new FoliageWindWeatherPoint("Clear", 0.30, 0.20),
                ],
            },
        };

        RenderPackValidationResult result = RenderPackValidator.ValidateDescriptor(
            descriptor,
            RenderPackHostCapabilities.Conformance);

        Assert.False(result.Success);
        Assert.Contains("more than once", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FoliageWindByWeatherAcceptsTheFiveDeclaredKindsOnce()
    {
        RenderPackDescriptor descriptor = BuiltInAtmosphericRenderPack.Descriptor;
        descriptor = descriptor with
        {
            AtmospherePolicy = descriptor.AtmospherePolicy! with
            {
                FoliageWindByWeather =
                [
                    new FoliageWindWeatherPoint("Clear", 0.25, 0.15),
                    new FoliageWindWeatherPoint("Overcast", 0.60, 0.35),
                    new FoliageWindWeatherPoint("Rain", 0.85, 0.60),
                    new FoliageWindWeatherPoint("Snow", 0.35, 0.20),
                    new FoliageWindWeatherPoint("Storm", 1.00, 0.75),
                ],
            },
        };

        RenderPackValidationResult result = RenderPackValidator.ValidateDescriptor(
            descriptor,
            RenderPackHostCapabilities.Conformance);

        Assert.True(result.Success, result.Reason);
    }

    [Fact]
    public void DirectionalShadowDepthRejectsSunDirectionAlias()
    {
        RenderPackDescriptor descriptor = BuiltInAtmosphericRenderPack.Descriptor;
        RenderPassDeclaration shadowPass = descriptor.Passes.Single(static pass =>
            pass.Semantic == RenderPassSemantic.DirectionalShadowDepth);
        descriptor = descriptor with
        {
            Passes = descriptor.Passes.Select(pass => ReferenceEquals(pass, shadowPass)
                ? pass with
                {
                    SemanticInputs = pass.SemanticInputs
                        .Append(RenderSemanticInput.SunDirection)
                        .ToArray(),
                }
                : pass).ToArray(),
        };

        RenderPackValidationResult result = RenderPackValidator.ValidateDescriptor(
            descriptor,
            RenderPackHostCapabilities.Conformance);

        Assert.False(result.Success);
        Assert.Equal(
            $"Directional-shadow pass '{shadowPass.Id}' must declare "
            + $"'{RenderSemanticInput.SelectedCelestialDirectionalLight}' and must not "
            + "alias the sun-specific atmospheric direction.",
            result.Reason);
    }

    [Fact]
    public void BuiltInPackPassesBinaryShaderInterfaceValidation()
    {
        RenderPackValidationResult result = RenderPackValidator.ValidateSelectedAssets(
            BuiltInAtmosphericRenderPack.Descriptor,
            BuiltInAtmosphericRenderPack.CreateAssets(SpirvDirectory()));

        Assert.True(result.Success, result.Reason);
    }

    [Fact]
    public void WorldDirectionalShadowReceiver_AllowsOnePassDetailCategoryBinding()
    {
        PipelineVariantDeclaration variant = BuiltInAtmosphericRenderPack.Descriptor
            .PipelineVariants.Single(static value =>
                value.Semantic
                    == RenderPipelineVariantSemantic.WorldDirectionalShadowReceiver);

        RenderPackSpirvValidationResult result =
            RenderPackSpirvValidator.ValidatePipelineVariantShader(
                Shader("mesh_atmospheric.vert.spv"),
                RenderPackShaderStage.Vertex,
                variant);

        Assert.True(result.Success, result.Reason);
    }

    [Fact]
    public void DirectionalShadowUniformRequiresSixMembersAndSelectedSourceAtOffset320()
    {
        byte[] valid = Shader("directional_shadow_world_opaque.vert.spv");
        PipelineVariantDeclaration variant = BuiltInAtmosphericRenderPack.Descriptor
            .PipelineVariants.Single(static value =>
                value.Semantic
                    == RenderPipelineVariantSemantic.WorldOpaqueDirectionalShadowCaster);

        Assert.Equal(336, RenderPackShaderAbi.DirectionalShadowSizeBytes);
        Assert.Equal(
            6,
            BlockMemberCount(
                valid,
                RenderPackShaderAbi.UniformDescriptorSet,
                RenderPackShaderAbi.DirectionalShadowBinding));
        Assert.Equal(
            320u,
            BlockMemberOffset(
                valid,
                RenderPackShaderAbi.UniformDescriptorSet,
                RenderPackShaderAbi.DirectionalShadowBinding,
                member: 5));
        RenderPackSpirvValidationResult baseline =
            RenderPackSpirvValidator.ValidatePipelineVariantShader(
                valid,
                RenderPackShaderStage.Vertex,
                variant);
        Assert.True(baseline.Success, baseline.Reason);

        byte[] wrongOffset = valid.ToArray();
        MutateBlockMemberOffset(
            wrongOffset,
            RenderPackShaderAbi.UniformDescriptorSet,
            RenderPackShaderAbi.DirectionalShadowBinding,
            member: 5,
            replacement: 304);
        RenderPackSpirvValidationResult offsetResult =
            RenderPackSpirvValidator.ValidatePipelineVariantShader(
                wrongOffset,
                RenderPackShaderStage.Vertex,
                variant);
        Assert.False(offsetResult.Success);
        Assert.Contains("336-byte ABI v1", offsetResult.Reason, StringComparison.Ordinal);

        byte[] fiveMembers = RemoveLastBlockMember(
            valid,
            RenderPackShaderAbi.UniformDescriptorSet,
            RenderPackShaderAbi.DirectionalShadowBinding);
        RenderPackSpirvValidationResult memberCountResult =
            RenderPackSpirvValidator.ValidatePipelineVariantShader(
                fiveMembers,
                RenderPackShaderStage.Vertex,
                variant);
        Assert.False(memberCountResult.Success);
        Assert.Contains("336-byte ABI v1", memberCountResult.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FullscreenShaderCannotReadReservedSetZero()
    {
        RenderPackSpirvValidationResult result =
            RenderPackSpirvValidator.ValidatePassShader(
                Shader("directional_shadow_world_opaque.vert.spv"),
                RenderPackShaderStage.Vertex,
                Pass());

        Assert.False(result.Success);
        Assert.Contains("set 0 binding 0", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FullscreenShaderCannotAliasRetailSetOne()
    {
        RenderPackSpirvValidationResult result =
            RenderPackSpirvValidator.ValidatePassShader(
                Shader("terrain_atmospheric.vert.spv"),
                RenderPackShaderStage.Vertex,
                Pass(inputs:
                [
                    RenderSemanticInput.Weather,
                    RenderSemanticInput.SelectedCelestialDirectionalLight,
                ]));

        Assert.False(result.Success);
        Assert.Contains("set 1 binding", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 4u)]
    [InlineData(6, 112u)]
    public void WrongAtmosphericUniformOffsetOrSizeIsRejected(int member, uint replacement)
    {
        byte[] spirv = Shader("atmospheric_sun_occlusion.frag.spv");
        MutateBlockMemberOffset(
            spirv,
            RenderPackShaderAbi.UniformDescriptorSet,
            RenderPackShaderAbi.AtmosphericFrameBinding,
            member,
            replacement);

        RenderPackSpirvValidationResult result =
            RenderPackSpirvValidator.ValidatePassShader(
                spirv,
                RenderPackShaderStage.Fragment,
                Pass(inputs:
                [
                    RenderSemanticInput.SceneDepth,
                    RenderSemanticInput.SunScreenPosition,
                    RenderSemanticInput.Weather,
                ]));

        Assert.False(result.Success);
        Assert.Contains("AtmosphericFrame", result.Reason, StringComparison.Ordinal);
    }


    [Fact]
    public void AtmosphericFrameAbiV2ModuleIsValid()
    {
        byte[] spirv = Shader("atmospheric_sun_occlusion.frag.spv");

        RenderPackSpirvValidationResult result = RenderPackSpirvValidator.ValidatePassShader(
            spirv,
            RenderPackShaderStage.Fragment,
            Pass(inputs:
            [
                RenderSemanticInput.SceneDepth,
                RenderSemanticInput.SunScreenPosition,
                RenderSemanticInput.Weather,
            ]));

        Assert.True(result.Success, result.Reason);
    }

    [Fact]
    public void AtmosphericFrameAbiV1ModuleIsStillValid()
    {
        byte[] v2 = Shader("atmospheric_sun_occlusion.frag.spv");
        byte[] v1 = RemoveLastBlockMember(
            RemoveLastBlockMember(
                v2,
                RenderPackShaderAbi.UniformDescriptorSet,
                RenderPackShaderAbi.AtmosphericFrameBinding),
            RenderPackShaderAbi.UniformDescriptorSet,
            RenderPackShaderAbi.AtmosphericFrameBinding);

        RenderPackSpirvValidationResult result = RenderPackSpirvValidator.ValidatePassShader(
            v1,
            RenderPackShaderStage.Fragment,
            Pass(inputs:
            [
                RenderSemanticInput.SceneDepth,
                RenderSemanticInput.SunScreenPosition,
                RenderSemanticInput.Weather,
            ]));

        Assert.True(result.Success, result.Reason);
    }

    [Fact]
    public void AtmosphericFrameEightMemberModuleIsRejected()
    {
        byte[] v2 = Shader("atmospheric_sun_occlusion.frag.spv");
        byte[] eightMembers = RemoveLastBlockMember(
            v2,
            RenderPackShaderAbi.UniformDescriptorSet,
            RenderPackShaderAbi.AtmosphericFrameBinding);

        RenderPackSpirvValidationResult result = RenderPackSpirvValidator.ValidatePassShader(
            eightMembers,
            RenderPackShaderStage.Fragment,
            Pass(inputs:
            [
                RenderSemanticInput.SceneDepth,
                RenderSemanticInput.SunScreenPosition,
                RenderSemanticInput.Weather,
            ]));

        Assert.False(result.Success);
        Assert.Contains("ABI v1", result.Reason, StringComparison.Ordinal);
        Assert.Contains("ABI v2", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void DeclaredStageAndMainEntryPointAreEnforced()
    {
        RenderPackSpirvValidationResult result =
            RenderPackSpirvValidator.ValidatePassShader(
                Shader("atmospheric_sun_rays.frag.spv"),
                RenderPackShaderStage.Vertex,
                Pass(inputs: [RenderSemanticInput.FrameTime], reads: ["mask"]));

        Assert.False(result.Success);
        Assert.Contains("entry point 'main'", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SampledTextureTableRequiresDeclaredSemanticOrResourceInput()
    {
        RenderPackSpirvValidationResult result =
            RenderPackSpirvValidator.ValidatePassShader(
                Shader("atmospheric_bloom_downsample.frag.spv"),
                RenderPackShaderStage.Fragment,
                Pass());

        Assert.False(result.Success);
        Assert.Contains("sampled without a declared", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void WritableRendererStorageIsRejectedEvenForAnAllowedBaseRole()
    {
        byte[] spirv = Shader("directional_shadow_world_opaque.vert.spv");
        RemoveNonWritableDecoration(spirv, set: 0, binding: 0);
        var variant = new PipelineVariantDeclaration(
            "world-caster",
            RenderPipelineBaseSemantic.WorldMesh,
            "world.vert.spv",
            "world.frag.spv",
            RenderMaterialClass.Opaque,
            [RenderSemanticInput.ShadowCasterTransforms])
        {
            Semantic = RenderPipelineVariantSemantic.WorldOpaqueDirectionalShadowCaster,
        };

        RenderPackSpirvValidationResult result =
            RenderPackSpirvValidator.ValidatePipelineVariantShader(
                spirv,
                RenderPackShaderStage.Vertex,
                variant);

        Assert.False(result.Success);
        Assert.Contains("storage writes are forbidden", result.Reason, StringComparison.Ordinal);
    }

    private static RenderPassDeclaration Pass(
        IReadOnlyList<RenderSemanticInput>? inputs = null,
        IReadOnlyList<string>? reads = null) => new(
            "pass",
            RenderPassHook.ToneMap,
            "pass.vert.spv",
            "pass.frag.spv",
            inputs ?? [],
            reads ?? [],
            []);

    private static byte[] Shader(string name) => File.ReadAllBytes(Path.Combine(SpirvDirectory(), name));

    private static string SpirvDirectory() => Path.Combine(
        RepositoryRoot(), "src", "AcDream.App", "Rendering", "Shaders", "spv");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static void MutateBlockMemberOffset(
        byte[] spirv,
        uint set,
        uint binding,
        int member,
        uint replacement)
    {
        uint[] words = Words(spirv);
        uint variable = DescriptorVariable(words, set, binding);
        uint pointer = VariableResultType(words, variable);
        uint structure = PointerPointee(words, pointer);
        for (int index = 5; index < words.Length; index += checked((int)(words[index] >> 16)))
        {
            int count = checked((int)(words[index] >> 16));
            uint opcode = words[index] & 0xffff;
            if (opcode == 72 && count >= 5
                && words[index + 1] == structure
                && words[index + 2] == (uint)member
                && words[index + 3] == 35)
            {
                words[index + 4] = replacement;
                CopyBack(words, spirv);
                return;
            }
        }
        throw new InvalidOperationException("Target block member offset was not found.");
    }

    private static void RemoveNonWritableDecoration(byte[] spirv, uint set, uint binding)
    {
        uint[] words = Words(spirv);
        uint variable = DescriptorVariable(words, set, binding);
        uint pointer = VariableResultType(words, variable);
        uint structure = PointerPointee(words, pointer);
        bool mutated = false;
        for (int index = 5; index < words.Length; index += checked((int)(words[index] >> 16)))
        {
            int count = checked((int)(words[index] >> 16));
            uint opcode = words[index] & 0xffff;
            if (opcode == 71 && count >= 3
                && words[index + 1] == variable
                && words[index + 2] == 24)
            {
                words[index + 2] = 23;
                mutated = true;
            }
            else if (opcode == 72 && count >= 4
                && words[index + 1] == structure
                && words[index + 3] == 24)
            {
                words[index + 3] = 23;
                mutated = true;
            }
        }
        if (!mutated)
            throw new InvalidOperationException("Target NonWritable decoration was not found.");
        CopyBack(words, spirv);
    }

    private static int BlockMemberCount(byte[] spirv, uint set, uint binding)
    {
        uint[] words = Words(spirv);
        uint structure = DescriptorStructure(words, set, binding);
        for (int index = 5; index < words.Length; index += checked((int)(words[index] >> 16)))
        {
            int count = checked((int)(words[index] >> 16));
            if ((words[index] & 0xffff) == 30 && count >= 2 && words[index + 1] == structure)
                return count - 2;
        }
        throw new InvalidOperationException("Descriptor block structure was not found.");
    }

    private static uint BlockMemberOffset(
        byte[] spirv,
        uint set,
        uint binding,
        int member)
    {
        uint[] words = Words(spirv);
        uint structure = DescriptorStructure(words, set, binding);
        for (int index = 5; index < words.Length; index += checked((int)(words[index] >> 16)))
        {
            int count = checked((int)(words[index] >> 16));
            if ((words[index] & 0xffff) == 72
                && count >= 5
                && words[index + 1] == structure
                && words[index + 2] == (uint)member
                && words[index + 3] == 35)
            {
                return words[index + 4];
            }
        }
        throw new InvalidOperationException("Descriptor block member offset was not found.");
    }

    private static byte[] RemoveLastBlockMember(byte[] spirv, uint set, uint binding)
    {
        uint[] words = Words(spirv);
        uint structure = DescriptorStructure(words, set, binding);
        for (int index = 5; index < words.Length; index += checked((int)(words[index] >> 16)))
        {
            int count = checked((int)(words[index] >> 16));
            if ((words[index] & 0xffff) != 30
                || count < 3
                || words[index + 1] != structure)
            {
                continue;
            }

            var mutated = words.ToList();
            mutated[index] = ((uint)(count - 1) << 16) | 30u;
            mutated.RemoveAt(index + count - 1);
            var bytes = new byte[mutated.Count * sizeof(uint)];
            Buffer.BlockCopy(mutated.ToArray(), 0, bytes, 0, bytes.Length);
            return bytes;
        }
        throw new InvalidOperationException("Descriptor block structure was not found.");
    }

    private static uint DescriptorStructure(uint[] words, uint set, uint binding)
    {
        uint variable = DescriptorVariable(words, set, binding);
        uint pointer = VariableResultType(words, variable);
        return PointerPointee(words, pointer);
    }

    private static uint DescriptorVariable(uint[] words, uint set, uint binding)
    {
        var sets = new Dictionary<uint, uint>();
        var bindings = new Dictionary<uint, uint>();
        for (int index = 5; index < words.Length; index += checked((int)(words[index] >> 16)))
        {
            int count = checked((int)(words[index] >> 16));
            uint opcode = words[index] & 0xffff;
            if (opcode != 71 || count < 4)
                continue;
            if (words[index + 2] == 34) sets[words[index + 1]] = words[index + 3];
            if (words[index + 2] == 33) bindings[words[index + 1]] = words[index + 3];
        }
        return sets.Keys.Single(id => sets[id] == set && bindings.GetValueOrDefault(id) == binding);
    }

    private static uint VariableResultType(uint[] words, uint variable)
    {
        for (int index = 5; index < words.Length; index += checked((int)(words[index] >> 16)))
        {
            int count = checked((int)(words[index] >> 16));
            if ((words[index] & 0xffff) == 59 && count >= 4 && words[index + 2] == variable)
                return words[index + 1];
        }
        throw new InvalidOperationException("Descriptor variable was not found.");
    }

    private static uint PointerPointee(uint[] words, uint pointer)
    {
        for (int index = 5; index < words.Length; index += checked((int)(words[index] >> 16)))
        {
            int count = checked((int)(words[index] >> 16));
            if ((words[index] & 0xffff) == 32 && count >= 4 && words[index + 1] == pointer)
                return words[index + 3];
        }
        throw new InvalidOperationException("Descriptor pointer type was not found.");
    }

    private static uint[] Words(byte[] bytes)
    {
        var words = new uint[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, words, 0, bytes.Length);
        return words;
    }

    private static void CopyBack(uint[] words, byte[] bytes) =>
        Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
}
