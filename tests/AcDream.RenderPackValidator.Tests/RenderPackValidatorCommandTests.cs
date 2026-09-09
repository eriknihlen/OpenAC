using System.Text.Json;
using AcDream.Plugin.Abstractions.Rendering;
using AcDream.Tools.RenderPackValidator;

namespace AcDream.RenderPackValidator.Tests;

public sealed class RenderPackValidatorCommandTests
{
    [Fact]
    public void PublicShaderAbiConstantsDescribeTheCurrentAbi()
    {
        Assert.Equal(3, RenderPackShaderAbi.UniformDescriptorSet);
        Assert.Equal(5, RenderPackShaderAbi.AtmosphericFrameBinding);
        Assert.Equal(160, RenderPackShaderAbi.AtmosphericFrameSizeBytesV1);
        Assert.Equal(192, RenderPackShaderAbi.AtmosphericFrameSizeBytesV2);
        Assert.Equal(192, RenderPackShaderAbi.AtmosphericFrameSizeBytes);
        Assert.Equal(2, RenderPackShaderAbi.ShaderAbiVersion);
        Assert.Equal(6, RenderPackShaderAbi.DirectionalShadowBinding);
        Assert.Equal(336, RenderPackShaderAbi.DirectionalShadowSizeBytes);
        Assert.Equal(7, RenderPackShaderAbi.PackPassBinding);
        Assert.Equal(64, RenderPackShaderAbi.PackPassSizeBytes);
        Assert.Equal(8, RenderPackShaderAbi.PackSettingsBinding);
        Assert.Equal(256, RenderPackShaderAbi.PackSettingsSizeBytes);
        Assert.Equal(64, RenderPackShaderAbi.PackSettingScalarCapacity);
        Assert.Equal(2, RenderPackShaderAbi.SampledTextureDescriptorSet);
        Assert.Equal(0, RenderPackShaderAbi.SampledTextureBinding);
        Assert.Equal(4, RenderPackShaderAbi.SampledPassInputCapacity);
        Assert.Equal(96, RenderPackShaderAbi.PushConstantSizeBytes);
        Assert.Equal(16 * 1024 * 1024, RenderPackShaderAbi.MaximumShaderAssetBytes);
    }

    [Fact]
    public void ExternalNoOpSample_ValidatesWithoutAppOrVulkanDependency()
    {
        string root = FindRepositoryRoot();
        string projectPath = Path.Combine(
            root,
            "samples",
            "AcDream.RenderPacks.NoOp",
            "AcDream.RenderPacks.NoOp.csproj");
        string project = File.ReadAllText(projectPath);
        Assert.Contains("AcDream.Plugin.Abstractions", project);
        Assert.DoesNotContain("AcDream.App", project);
        Assert.DoesNotContain("Silk.NET", project);
        string outputDirectory = Path.Combine(
            Path.GetDirectoryName(projectPath)!,
            "bin",
            "Release",
            "net10.0");
        var output = new StringWriter();
        var error = new StringWriter();

        int result = RenderPackValidatorCommand.Run(
            [outputDirectory],
            output,
            error);

        Assert.Equal(0, result);
        Assert.Contains("OK: sample.no-op-render-pack 1.0.0", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void ExternalAtmosphericTierTwoSample_ValidatesPackagedShadersWithoutRendererDependency()
    {
        string root = FindRepositoryRoot();
        string projectPath = Path.Combine(
            root,
            "samples",
            "AcDream.RenderPacks.AtmosphericTier2",
            "AcDream.RenderPacks.AtmosphericTier2.csproj");
        string project = File.ReadAllText(projectPath);
        Assert.Contains("AcDream.Plugin.Abstractions", project);
        Assert.DoesNotContain("AcDream.App", project);
        Assert.DoesNotContain("Silk.NET", project);

        string outputDirectory = Path.Combine(
            Path.GetDirectoryName(projectPath)!,
            "bin",
            "Release",
            "net10.0");
        Assert.False(File.Exists(Path.Combine(
            outputDirectory,
            "AcDream.Plugin.Abstractions.dll")));
        Assert.False(File.Exists(Path.Combine(outputDirectory, "AcDream.App.dll")));

        var output = new StringWriter();
        var error = new StringWriter();
        int result = RenderPackValidatorCommand.Run(
            [outputDirectory],
            output,
            error);

        Assert.Equal(0, result);
        Assert.Contains(
            "OK: sample.atmospheric-tier2 1.0.0 (API 1, 4 preset(s))",
            output.ToString());
        Assert.Contains(
            "Validated 1 render pack(s) from 'sample.atmospheric-tier2'",
            output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void ExternalShadowsOnlyTierTwoSample_ValidatesAsAComposablePack()
    {
        string root = FindRepositoryRoot();
        string projectPath = Path.Combine(
            root,
            "samples",
            "AcDream.RenderPacks.ShadowsOnlyTier2",
            "AcDream.RenderPacks.ShadowsOnlyTier2.csproj");
        string project = File.ReadAllText(projectPath);
        Assert.Contains("AcDream.Plugin.Abstractions", project);
        Assert.DoesNotContain("AcDream.App", project);
        Assert.DoesNotContain("Silk.NET", project);

        string outputDirectory = Path.Combine(
            Path.GetDirectoryName(projectPath)!,
            "bin",
            "Release",
            "net10.0");
        Assert.False(File.Exists(Path.Combine(
            outputDirectory,
            "AcDream.Plugin.Abstractions.dll")));
        Assert.False(File.Exists(Path.Combine(outputDirectory, "AcDream.App.dll")));

        var output = new StringWriter();
        var error = new StringWriter();
        int result = RenderPackValidatorCommand.Run(
            [outputDirectory],
            output,
            error);

        Assert.Equal(0, result);
        Assert.Contains(
            "OK: sample.shadows-only-tier2 1.0.0 (API 1, 1 preset(s))",
            output.ToString());
        Assert.Contains(
            "Validated 1 render pack(s) from 'sample.shadows-only-tier2'",
            output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void AtmosphericSampleDescriptorValidatesSelectedCelestialContract()
    {
        RenderPackSdkValidationResult result =
            RenderPackSdkValidator.ValidateDescriptor(AtmosphericDescriptor());

        Assert.True(result.Success, result.Reason);
    }

    [Fact]
    public void MalformedManifest_FailsBeforeEntryDllInspection()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, "plugin.json"),
            JsonSerializer.Serialize(new
            {
                id = "Bad Id",
                displayName = "Bad pack",
                version = "1.0.0",
                entryDll = "missing.dll",
                apiVersion = 1,
                kinds = new[] { "renderPack" },
            }));
        var output = new StringWriter();
        var error = new StringWriter();

        int result = RenderPackValidatorCommand.Run(
            [directory.Path],
            output,
            error);

        Assert.Equal(1, result);
        Assert.Contains("stable lowercase logical id", error.ToString());
        Assert.DoesNotContain("does not exist", error.ToString());
    }

    [Fact]
    public void ManifestWithoutRenderKind_IsRejectedPrecisely()
    {
        const string json = """
            {
              "id": "sample.gameplay",
              "displayName": "Gameplay only",
              "version": "1.0.0",
              "entryDll": "missing.dll",
              "apiVersion": 1
            }
            """;

        ValidationOutcome result = PackManifest.Parse(json);

        Assert.False(result.Success);
        Assert.Contains("does not declare the renderPack kind", result.Reason);
    }

    [Fact]
    public void DescriptorReadBeforeWrite_IsRejectedPrecisely()
    {
        RenderPackDescriptor descriptor = Descriptor(
            resources:
            [
                new RenderResourceDeclaration(
                    "intermediate",
                    RenderResourceKind.Image2D,
                    RenderFormatClass.HdrColor,
                    new RenderExtentDeclaration(
                        RenderExtentMode.RelativeToMainWorld,
                        0.5,
                        0.5),
                    0,
                    RenderResourceUsage.Sampled | RenderResourceUsage.ColorAttachment,
                    RenderResourceLifetime.ActivePack,
                    1024),
            ],
            passes:
            [
                new RenderPassDeclaration(
                    "bad-pass",
                    RenderPassHook.ToneMap,
                    "shader.vert.spv",
                    "shader.frag.spv",
                    [],
                    ["intermediate"],
                    []),
            ]);

        RenderPackSdkValidationResult result =
            RenderPackSdkValidator.ValidateDescriptor(descriptor);

        Assert.False(result.Success);
        Assert.Equal(
            "Pass 'bad-pass' reads resource 'intermediate' before it is written.",
            result.Reason);
    }

    [Fact]
    public void InvalidShaderAsset_IsRejectedWithoutGpuDependency()
    {
        RenderPackDescriptor descriptor = Descriptor(
            passes:
            [
                new RenderPassDeclaration(
                    "pass",
                    RenderPassHook.ToneMap,
                    "shader.vert.spv",
                    "shader.frag.spv",
                    [],
                    [],
                    []),
            ]);

        RenderPackSdkValidationResult result = RenderPackSdkValidator.ValidateAssets(
            descriptor,
            new ByteAssets([0, 1, 2, 3]));

        Assert.False(result.Success);
        Assert.Contains("is not valid SPIR-V", result.Reason);
    }

    [Fact]
    public void ShaderBinaryInterface_IsValidatedByTheStandaloneSdk()
    {
        RenderPackDescriptor descriptor = Descriptor(
            passes:
            [
                new RenderPassDeclaration(
                    "pass",
                    RenderPassHook.ToneMap,
                    "reserved-set.vert.spv",
                    "unused.frag.spv",
                    [],
                    [],
                    []),
            ]);
        string root = FindRepositoryRoot();
        byte[] reservedSetShader = File.ReadAllBytes(Path.Combine(
            root,
            "src",
            "AcDream.App",
            "Rendering",
            "Shaders",
            "spv",
            "directional_shadow_world_opaque.vert.spv"));

        RenderPackSdkValidationResult result = RenderPackSdkValidator.ValidateAssets(
            descriptor,
            new MappedAssets(new Dictionary<string, byte[]>
            {
                ["reserved-set.vert.spv"] = reservedSetShader,
            }));

        Assert.False(result.Success);
        Assert.Contains("set 0 binding 0", result.Reason);
    }

    [Fact]
    public void DuplicateRendererOwnedResourceSemantic_IsRejectedByTheSdk()
    {
        RenderPackDescriptor descriptor = Descriptor(
            resources:
            [
                Image("first") with { Semantic = RenderResourceSemantic.BloomPing },
                Image("second") with { Semantic = RenderResourceSemantic.BloomPing },
            ]);

        RenderPackSdkValidationResult result =
            RenderPackSdkValidator.ValidateDescriptor(descriptor);

        Assert.False(result.Success);
        Assert.Contains("duplicate resource semantic 'BloomPing'", result.Reason);
    }

    [Fact]
    public void PartialFixedAtmosphericExecutor_IsRejectedByTheSdk()
    {
        RenderPassDeclaration partial = new(
            "rays",
            RenderPassHook.ToneMap,
            "shader.vert.spv",
            "shader.frag.spv",
            [],
            [],
            [])
        {
            Semantic = RenderPassSemantic.SunRays,
        };

        RenderPackSdkValidationResult result =
            RenderPackSdkValidator.ValidateDescriptor(Descriptor(passes: [partial]));

        Assert.False(result.Success);
        Assert.Contains("does not declare required pass semantic", result.Reason);
    }

    [Fact]
    public void FixedAtmosphericResourceShape_IsEnforcedByTheSdk()
    {
        RenderPackDescriptor descriptor = AtmosphericDescriptor();
        descriptor = descriptor with
        {
            Resources = descriptor.Resources.Select(resource =>
                resource.Semantic == RenderResourceSemantic.SunOcclusionMask
                    ? resource with { Format = RenderFormatClass.HdrColor }
                    : resource).ToArray(),
        };

        RenderPackSdkValidationResult result =
            RenderPackSdkValidator.ValidateDescriptor(descriptor);

        Assert.False(result.Success);
        Assert.Contains("Resource semantic 'SunOcclusionMask'", result.Reason);
        Assert.Contains("kind, format, extent, usage, and lifetime", result.Reason);
    }

    [Fact]
    public void FixedAtmosphericVariantShape_IsEnforcedByTheSdk()
    {
        RenderPackDescriptor descriptor = AtmosphericDescriptor();
        descriptor = descriptor with
        {
            PipelineVariants = descriptor.PipelineVariants.Select(variant =>
                variant.Semantic == RenderPipelineVariantSemantic.WorldDirectionalShadowReceiver
                    ? variant with { BaseSemantic = RenderPipelineBaseSemantic.Terrain }
                    : variant).ToArray(),
        };

        RenderPackSdkValidationResult result =
            RenderPackSdkValidator.ValidateDescriptor(descriptor);

        Assert.False(result.Success);
        Assert.Contains(
            "Pipeline variant semantic 'WorldDirectionalShadowReceiver'",
            result.Reason);
        Assert.Contains("base, material, and input contract", result.Reason);
    }

    [Fact]
    public void SelectedCelestialSemanticRequiresAuthoredCelestialCapabilityInTheSdk()
    {
        RenderPackDescriptor descriptor = AtmosphericDescriptor();
        descriptor = descriptor with
        {
            RequiredCapabilities = descriptor.RequiredCapabilities
                .Where(static capability =>
                    capability != RenderCapability.AuthoredCelestialDirectionalLight)
                .ToArray(),
        };

        RenderPackSdkValidationResult result =
            RenderPackSdkValidator.ValidateDescriptor(descriptor);

        Assert.False(result.Success);
        Assert.Equal(
            $"Pack '{descriptor.Id}' declares semantic "
            + $"'{RenderSemanticInput.SelectedCelestialDirectionalLight}' but does not "
            + $"require capability '{RenderCapability.AuthoredCelestialDirectionalLight}'.",
            result.Reason);
    }

    [Fact]
    public void DirectionalShadowDepthRejectsSunDirectionAliasInTheSdk()
    {
        RenderPackDescriptor descriptor = AtmosphericDescriptor();
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

        RenderPackSdkValidationResult result =
            RenderPackSdkValidator.ValidateDescriptor(descriptor);

        Assert.False(result.Success);
        Assert.Equal(
            $"Directional-shadow pass '{shadowPass.Id}' must declare "
            + $"'{RenderSemanticInput.SelectedCelestialDirectionalLight}' and must not "
            + "alias the sun-specific atmospheric direction.",
            result.Reason);
    }

    [Fact]
    public void StandaloneSdkRequiresSixMember336ByteDirectionalShadowBlock()
    {
        RenderPackDescriptor atmospheric = AtmosphericDescriptor();
        PipelineVariantDeclaration variant = atmospheric.PipelineVariants.Single(static value =>
            value.Semantic
                == RenderPipelineVariantSemantic.WorldOpaqueDirectionalShadowCaster);
        RenderPackDescriptor descriptor = Descriptor() with
        {
            PipelineVariants = [variant],
        };
        string shaderDirectory = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "AcDream.App",
            "Rendering",
            "Shaders",
            "spv");
        byte[] validVertex = File.ReadAllBytes(Path.Combine(
            shaderDirectory,
            "directional_shadow_world_opaque.vert.spv"));
        byte[] validFragment = File.ReadAllBytes(Path.Combine(
            shaderDirectory,
            "directional_shadow_world_opaque.frag.spv"));

        Assert.Equal(336, RenderPackShaderAbi.DirectionalShadowSizeBytes);
        Assert.Equal(
            6,
            BlockMemberCount(
                validVertex,
                RenderPackShaderAbi.UniformDescriptorSet,
                RenderPackShaderAbi.DirectionalShadowBinding));
        Assert.Equal(
            320u,
            BlockMemberOffset(
                validVertex,
                RenderPackShaderAbi.UniformDescriptorSet,
                RenderPackShaderAbi.DirectionalShadowBinding,
                member: 5));
        RenderPackSdkValidationResult baseline = RenderPackSdkValidator.ValidateAssets(
            descriptor,
            VariantAssets(variant, validVertex, validFragment));
        Assert.True(baseline.Success, baseline.Reason);

        byte[] wrongOffset = validVertex.ToArray();
        MutateBlockMemberOffset(
            wrongOffset,
            RenderPackShaderAbi.UniformDescriptorSet,
            RenderPackShaderAbi.DirectionalShadowBinding,
            member: 5,
            replacement: 304);
        RenderPackSdkValidationResult offsetResult = RenderPackSdkValidator.ValidateAssets(
            descriptor,
            VariantAssets(variant, wrongOffset, validFragment));
        Assert.False(offsetResult.Success);
        Assert.Contains("336-byte ABI v1", offsetResult.Reason, StringComparison.Ordinal);

        byte[] fiveMembers = RemoveLastBlockMember(
            validVertex,
            RenderPackShaderAbi.UniformDescriptorSet,
            RenderPackShaderAbi.DirectionalShadowBinding);
        RenderPackSdkValidationResult memberCountResult = RenderPackSdkValidator.ValidateAssets(
            descriptor,
            VariantAssets(variant, fiveMembers, validFragment));
        Assert.False(memberCountResult.Success);
        Assert.Contains("336-byte ABI v1", memberCountResult.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FixedAtmosphericReplayRequiresEveryHeadlineCasterInTheSdk()
    {
        RenderPackDescriptor descriptor = AtmosphericDescriptor();
        SceneReplayDeclaration replay = descriptor.SceneReplays.Single();
        descriptor = descriptor with
        {
            SceneReplays =
            [
                replay with
                {
                    CasterClasses = replay.CasterClasses
                        & ~RenderCasterClass.AnimatedAlphaCutout,
                },
            ],
        };

        RenderPackSdkValidationResult result =
            RenderPackSdkValidator.ValidateDescriptor(descriptor);

        Assert.False(result.Success);
        Assert.Contains("all five headline caster classes", result.Reason);
        Assert.Contains("four maximum cascade views", result.Reason);
    }

    [Fact]
    public void BufferResource_IsRejectedAsReservedInV1()
    {
        RenderPackDescriptor descriptor = Descriptor(
            resources:
            [
                new RenderResourceDeclaration(
                    "future-buffer",
                    RenderResourceKind.Buffer,
                    RenderFormatClass.StructuredData,
                    Extent: null,
                    SizeBytes: 1024,
                    RenderResourceUsage.Sampled,
                    RenderResourceLifetime.ActivePack,
                    EstimatedResidentBytes: 1024),
            ]);

        RenderPackSdkValidationResult result =
            RenderPackSdkValidator.ValidateDescriptor(descriptor);

        Assert.False(result.Success);
        Assert.Contains("reserved and unbindable", result.Reason);
    }

    [Fact]
    public void MoreThanFourSampledInputs_IsRejectedByShaderAbi()
    {
        RenderResourceDeclaration first = Image("first");
        RenderResourceDeclaration second = Image("second");
        RenderPackDescriptor descriptor = Descriptor(
            requiredCapabilities:
            [
                RenderCapability.MainWorldColorIntermediate,
                RenderCapability.SceneDepthSampling,
                RenderCapability.SceneNormalSampling,
            ],
            resources: [first, second],
            passes:
            [
                new RenderPassDeclaration(
                    "produce-first",
                    RenderPassHook.AtmosphereBeforeToneMap,
                    "shader.vert.spv",
                    "shader.frag.spv",
                    [],
                    [],
                    ["first"]),
                new RenderPassDeclaration(
                    "produce-second",
                    RenderPassHook.AtmosphereBeforeToneMap,
                    "shader.vert.spv",
                    "shader.frag.spv",
                    [],
                    [],
                    ["second"]),
                new RenderPassDeclaration(
                    "consume",
                    RenderPassHook.ToneMap,
                    "shader.vert.spv",
                    "shader.frag.spv",
                    [
                        RenderSemanticInput.WorldColor,
                        RenderSemanticInput.SceneDepth,
                        RenderSemanticInput.SceneNormals,
                    ],
                    ["first", "second"],
                    []),
            ]);

        RenderPackSdkValidationResult result =
            RenderPackSdkValidator.ValidateDescriptor(descriptor);

        Assert.False(result.Success);
        Assert.Contains("at most four (TextureIndexA-D)", result.Reason);
    }

    [Fact]
    public void MoreThanSixtyFourSettings_IsRejectedByShaderAbi()
    {
        RenderSettingDeclaration[] settings = Enumerable.Range(0, 65)
            .Select(index => new RenderSettingDeclaration(
                $"setting-{index}",
                $"Setting {index}",
                RenderSettingKind.Boolean,
                "false",
                Minimum: null,
                Maximum: null,
                Step: null,
                Choices: []))
            .ToArray();
        RenderPackDescriptor descriptor = Descriptor(settings: settings);

        RenderPackSdkValidationResult result =
            RenderPackSdkValidator.ValidateDescriptor(descriptor);

        Assert.False(result.Success);
        Assert.Contains("at most 64 shader setting slots", result.Reason);
    }

    [Fact]
    public void SixtyFourSettings_IsTheAcceptedShaderAbiBoundary()
    {
        RenderSettingDeclaration[] settings = Enumerable.Range(0, 64)
            .Select(index => BooleanSetting(index))
            .ToArray();

        RenderPackSdkValidationResult result =
            RenderPackSdkValidator.ValidateDescriptor(Descriptor(settings: settings));

        Assert.True(result.Success, result.Reason);
    }

    [Fact]
    public void SettingValuesUseTheInvariantKindSpecificGrammar()
    {
        RenderSettingDeclaration[] invalidSettings =
        [
            new("boolean", "Boolean", RenderSettingKind.Boolean, "1", null, null, null, []),
            new("integer", "Integer", RenderSettingKind.Integer, "1.5", null, null, null, []),
            new("large-integer", "Large integer", RenderSettingKind.Integer, "16777217", null, null, null, []),
            new("float", "Float", RenderSettingKind.Float, "1,5", null, null, null, []),
            new("large-float", "Large float", RenderSettingKind.Float, "1e100", null, null, null, []),
            new("choice", "Choice", RenderSettingKind.Choice, "missing", null, null, null, ["first"]),
        ];

        foreach (RenderSettingDeclaration setting in invalidSettings)
        {
            RenderPackSdkValidationResult result = RenderPackSdkValidator.ValidateDescriptor(
                Descriptor(settings: [setting]));
            Assert.False(result.Success);
            Assert.Contains($"'{setting.Id}'", result.Reason);
        }
    }

    [Theory]
    [InlineData("1.1")]
    [InlineData("2.25")]
    [InlineData("-0.25")]
    public void SettingValuesMustRespectDeclaredRangeAndStep(string value)
    {
        RenderSettingDeclaration setting = new(
            "exposure", "Exposure", RenderSettingKind.Float, value,
            Minimum: 0, Maximum: 2, Step: 0.25, Choices: []);

        RenderPackSdkValidationResult result =
            RenderPackSdkValidator.ValidateDescriptor(Descriptor(settings: [setting]));

        Assert.False(result.Success);
        Assert.Contains("invalid default value", result.Reason);
    }

    [Fact]
    public void DuplicatePresetSettingOverrideIsRejectedCaseInsensitively()
    {
        RenderSettingDeclaration setting = new(
            "exposure", "Exposure", RenderSettingKind.Float, "1",
            Minimum: 0, Maximum: 2, Step: 0.25, Choices: []);
        RenderPackDescriptor descriptor = Descriptor(settings: [setting]);
        descriptor = descriptor with
        {
            QualityPresets =
            [
                descriptor.QualityPresets[0] with
                {
                    SettingOverrides =
                    [
                        new RenderQualitySettingOverride("exposure", "1.25"),
                        new RenderQualitySettingOverride("EXPOSURE", "1.5"),
                    ],
                },
            ],
        };

        RenderPackSdkValidationResult result =
            RenderPackSdkValidator.ValidateDescriptor(descriptor);

        Assert.False(result.Success);
        Assert.Contains("more than once", result.Reason);
    }

    [Fact]
    public void MissingFeatureSummaryIsRejectedByTheExternalSdkValidator()
    {
        RenderPackSdkValidationResult result = RenderPackSdkValidator.ValidateDescriptor(
            Descriptor() with { FeatureSummary = string.Empty });

        Assert.False(result.Success);
        Assert.Equal("Pack 'sample.test-pack' has no feature summary.", result.Reason);
    }

    [Fact]
    public void AtmosphericEffectCurvesRejectNullNonMonotonicAndOutOfRangeValues()
    {
        RenderPackDescriptor descriptor = AtmosphericDescriptor();
        AtmospherePolicyDeclaration policy = descriptor.AtmospherePolicy!;
        AtmospherePolicyDeclaration[] malformed =
        [
            policy with { DirectionalShadowLightElevationResponse = null! },
            policy with
            {
                DirectionalShadowLightElevationResponse =
                [
                    new SunElevationResponsePoint(10, 0),
                    new SunElevationResponsePoint(10, 1),
                ],
            },
            policy with
            {
                VolumetricShaftSunElevationResponse =
                [
                    new SunElevationResponsePoint(-10, 0),
                    new SunElevationResponsePoint(10, 1.01),
                ],
            },
            policy with
            {
                VolumetricShaftSunElevationResponse =
                [
                    new SunElevationResponsePoint(double.NaN, 0),
                    new SunElevationResponsePoint(10, 1),
                ],
            },
        ];

        foreach (AtmospherePolicyDeclaration value in malformed)
        {
            RenderPackSdkValidationResult result = RenderPackSdkValidator.ValidateDescriptor(
                descriptor with { AtmospherePolicy = value });
            Assert.False(result.Success);
            Assert.Contains("curve", result.Reason, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DirectionalShadowCurveMustRemainZeroAtAndBelowAuthoredHorizon()
    {
        RenderPackDescriptor descriptor = AtmosphericDescriptor();
        AtmospherePolicyDeclaration policy = descriptor.AtmospherePolicy!;
        IReadOnlyList<SunElevationResponsePoint>[] invalidCurves =
        [
            [
                new SunElevationResponsePoint(-90, 0.1),
                new SunElevationResponsePoint(0, 0),
                new SunElevationResponsePoint(90, 1),
            ],
            [
                new SunElevationResponsePoint(-90, 0),
                new SunElevationResponsePoint(90, 1),
            ],
            [
                new SunElevationResponsePoint(0, 0.1),
                new SunElevationResponsePoint(90, 1),
            ],
        ];

        foreach (IReadOnlyList<SunElevationResponsePoint> invalidCurve in invalidCurves)
        {
            RenderPackSdkValidationResult result = RenderPackSdkValidator.ValidateDescriptor(
                descriptor with
                {
                    AtmospherePolicy = policy with
                    {
                        DirectionalShadowLightElevationResponse = invalidCurve,
                    },
                });

            Assert.False(result.Success);
            Assert.Contains("zero at and below the 0-degree authored horizon", result.Reason,
                StringComparison.Ordinal);
        }

        Assert.True(RenderPackSdkValidator.ValidateDescriptor(descriptor).Success);
    }

    private static RenderPackDescriptor Descriptor(
        IReadOnlyList<RenderCapability>? requiredCapabilities = null,
        IReadOnlyList<RenderResourceDeclaration>? resources = null,
        IReadOnlyList<RenderPassDeclaration>? passes = null,
        IReadOnlyList<RenderSettingDeclaration>? settings = null) => new(
            "sample.test-pack",
            "Test pack",
            new Version(1, 0, 0),
            RenderPackApi.Current,
            RenderPackTier.Tier1,
            requiredCapabilities ?? [],
            [],
            resources ?? [],
            passes ?? [],
            [],
            [],
            [
                new RenderQualityPreset(
                    "default",
                    "Default",
                    [],
                    [],
                    [],
                    0,
                    0,
                    0,
                    0,
                    0),
            ],
            settings ?? [],
            null)
        {
            FeatureSummary = "Test render pack.",
        };

    private static RenderPackDescriptor AtmosphericDescriptor()
    {
        RenderResourceDeclaration Image(
            string id,
            RenderResourceSemantic semantic,
            RenderFormatClass format) => new(
                id,
                RenderResourceKind.Image2D,
                format,
                new RenderExtentDeclaration(RenderExtentMode.RelativeToMainWorld, 0.5, 0.5),
                0,
                RenderResourceUsage.Sampled | RenderResourceUsage.ColorAttachment,
                RenderResourceLifetime.ActivePack,
                1024)
            {
                Semantic = semantic,
            };

        RenderPassDeclaration Pass(
            string id,
            RenderPassSemantic semantic,
            RenderPassHook hook,
            IReadOnlyList<RenderSemanticInput> inputs,
            IReadOnlyList<string> reads,
            IReadOnlyList<string> writes) => new(
                id,
                hook,
                $"{id}.vert.spv",
                $"{id}.frag.spv",
                inputs,
                reads,
                writes)
            {
                Semantic = semantic,
            };

        PipelineVariantDeclaration Variant(
            string id,
            RenderPipelineVariantSemantic semantic,
            RenderPipelineBaseSemantic baseSemantic,
            RenderMaterialClass materials,
            IReadOnlyList<RenderSemanticInput> inputs) => new(
                id,
                baseSemantic,
                $"{id}.vert.spv",
                $"{id}.frag.spv",
                materials,
                inputs)
            {
                Semantic = semantic,
            };

        RenderResourceDeclaration[] resources =
        [
            Image("world-hdr", RenderResourceSemantic.MainWorldHdr, RenderFormatClass.HdrColor),
            Image("bloom-a", RenderResourceSemantic.BloomPing, RenderFormatClass.HdrColor),
            Image("bloom-b", RenderResourceSemantic.BloomPong, RenderFormatClass.HdrColor),
            Image("sun-mask", RenderResourceSemantic.SunOcclusionMask, RenderFormatClass.SingleChannel),
            Image("sun-rays", RenderResourceSemantic.SunRays, RenderFormatClass.HdrColor),
            new RenderResourceDeclaration(
                "shadow-depth",
                RenderResourceKind.Image2DArray,
                RenderFormatClass.DirectionalDepth,
                new RenderExtentDeclaration(RenderExtentMode.AbsolutePixels, 1024, 1024, 4),
                0,
                RenderResourceUsage.Sampled | RenderResourceUsage.DepthAttachment,
                RenderResourceLifetime.ActivePack,
                4096)
            {
                Semantic = RenderResourceSemantic.DirectionalShadowDepth,
            },
            Image("volumetric", RenderResourceSemantic.VolumetricShafts, RenderFormatClass.HdrColor),
        ];
        RenderPassDeclaration[] passes =
        [
            Pass("shadow-pass", RenderPassSemantic.DirectionalShadowDepth,
                RenderPassHook.ShadowDepthBeforeWorld,
                [RenderSemanticInput.CameraMatrices,
                    RenderSemanticInput.SelectedCelestialDirectionalLight,
                    RenderSemanticInput.ShadowCasterTransforms, RenderSemanticInput.ActiveDayGroup,
                    RenderSemanticInput.Weather], [], ["shadow-depth"]),
            Pass("occlusion-pass", RenderPassSemantic.SunOcclusion,
                RenderPassHook.AtmosphereBeforeToneMap,
                [RenderSemanticInput.SceneDepth, RenderSemanticInput.SunScreenPosition,
                    RenderSemanticInput.ActiveDayGroup, RenderSemanticInput.Weather],
                [], ["sun-mask"]),
            Pass("rays-pass", RenderPassSemantic.SunRays,
                RenderPassHook.AtmosphereBeforeToneMap,
                [RenderSemanticInput.SunScreenPosition, RenderSemanticInput.FrameTime],
                ["sun-mask"], ["sun-rays"]),
            Pass("volumetric-pass", RenderPassSemantic.VolumetricShafts,
                RenderPassHook.AtmosphereBeforeToneMap,
                [RenderSemanticInput.SceneDepth, RenderSemanticInput.CameraMatrices,
                    RenderSemanticInput.SunDirection, RenderSemanticInput.DirectionalShadowMaps,
                    RenderSemanticInput.ActiveDayGroup, RenderSemanticInput.Weather],
                ["shadow-depth"], ["volumetric"]),
            Pass("downsample-pass", RenderPassSemantic.BloomDownsample,
                RenderPassHook.AtmosphereBeforeToneMap,
                [RenderSemanticInput.WorldColor], ["sun-rays", "volumetric"], ["bloom-a"]),
            Pass("blur-h-pass", RenderPassSemantic.BloomBlurHorizontal,
                RenderPassHook.AtmosphereBeforeToneMap,
                [RenderSemanticInput.FrameTime], ["bloom-a"], ["bloom-b"]),
            Pass("blur-v-pass", RenderPassSemantic.BloomBlurVertical,
                RenderPassHook.AtmosphereBeforeToneMap,
                [RenderSemanticInput.FrameTime], ["bloom-b"], ["bloom-a"]),
            Pass("filmic-pass", RenderPassSemantic.FilmicComposite,
                RenderPassHook.ToneMap,
                [RenderSemanticInput.WorldColor, RenderSemanticInput.FrameTime],
                ["bloom-a", "sun-rays", "volumetric"], []),
        ];
        const RenderCasterClass allCasters = RenderCasterClass.Terrain
            | RenderCasterClass.OpaqueWorld
            | RenderCasterClass.AlphaCutoutWorld
            | RenderCasterClass.AnimatedOpaque
            | RenderCasterClass.AnimatedAlphaCutout;
        PipelineVariantDeclaration[] variants =
        [
            Variant("terrain-caster",
                RenderPipelineVariantSemantic.TerrainDirectionalShadowCaster,
                RenderPipelineBaseSemantic.Terrain, RenderMaterialClass.Opaque,
                [RenderSemanticInput.CameraMatrices]),
            Variant("world-caster",
                RenderPipelineVariantSemantic.WorldOpaqueDirectionalShadowCaster,
                RenderPipelineBaseSemantic.WorldMesh,
                RenderMaterialClass.Opaque | RenderMaterialClass.AnimatedOpaque,
                [RenderSemanticInput.CameraMatrices, RenderSemanticInput.ShadowCasterTransforms]),
            Variant("cutout-caster",
                RenderPipelineVariantSemantic.WorldAlphaCutoutDirectionalShadowCaster,
                RenderPipelineBaseSemantic.WorldMesh,
                RenderMaterialClass.AlphaCutout | RenderMaterialClass.AnimatedAlphaCutout,
                [RenderSemanticInput.CameraMatrices, RenderSemanticInput.ShadowCasterTransforms]),
            Variant("terrain-receiver",
                RenderPipelineVariantSemantic.TerrainDirectionalShadowReceiver,
                RenderPipelineBaseSemantic.Terrain, RenderMaterialClass.Opaque,
                [RenderSemanticInput.DirectionalShadowMaps,
                    RenderSemanticInput.SelectedCelestialDirectionalLight]),
            Variant("world-receiver",
                RenderPipelineVariantSemantic.WorldDirectionalShadowReceiver,
                RenderPipelineBaseSemantic.WorldMesh,
                RenderMaterialClass.Opaque | RenderMaterialClass.AlphaCutout
                    | RenderMaterialClass.AnimatedOpaque
                    | RenderMaterialClass.AnimatedAlphaCutout,
                [RenderSemanticInput.DirectionalShadowMaps,
                    RenderSemanticInput.SelectedCelestialDirectionalLight]),
        ];

        return new RenderPackDescriptor(
            "sample.atmospheric",
            "Atmospheric sample",
            new Version(1, 0, 0),
            RenderPackApi.Current,
            RenderPackTier.Tier2Plus,
            [
                RenderCapability.MainWorldColorIntermediate,
                RenderCapability.FullscreenPasses,
                RenderCapability.SceneDepthSampling,
                RenderCapability.AuthoredSunDirection,
                RenderCapability.AuthoredCelestialDirectionalLight,
                RenderCapability.AuthoredSunScreenPosition,
                RenderCapability.AuthoredWeather,
                RenderCapability.AnimatedCasterTransforms,
                RenderCapability.DirectionalShadowMaps,
            ],
            [],
            resources,
            passes,
            [new SceneReplayDeclaration(
                "outdoor-casters",
                RenderSceneReplaySemantic.OutdoorDirectionalShadowCasters,
                allCasters,
                4)],
            variants,
            [new RenderQualityPreset("default", "Default", [], [], [], 0, 0, 0, 0, 0)],
            [],
            new AtmospherePolicyDeclaration(
                [
                    new SunElevationResponsePoint(-90, 0),
                    new SunElevationResponsePoint(90, 1),
                ],
                [])
            {
                DirectionalShadowLightElevationResponse =
                [
                    new SunElevationResponsePoint(-90, 0),
                    new SunElevationResponsePoint(0, 0),
                    new SunElevationResponsePoint(90, 1),
                ],
                VolumetricShaftSunElevationResponse =
                [
                    new SunElevationResponsePoint(-90, 0),
                    new SunElevationResponsePoint(90, 1),
                ],
            })
        {
            FeatureSummary = "Atmospheric validator test render pack.",
        };
    }

    private static RenderResourceDeclaration Image(string id) => new(
        id,
        RenderResourceKind.Image2D,
        RenderFormatClass.HdrColor,
        new RenderExtentDeclaration(
            RenderExtentMode.RelativeToMainWorld,
            0.5,
            0.5),
        0,
        RenderResourceUsage.Sampled | RenderResourceUsage.ColorAttachment,
        RenderResourceLifetime.ActivePack,
        1024);

    private static RenderSettingDeclaration BooleanSetting(int index) => new(
        $"setting-{index}",
        $"Setting {index}",
        RenderSettingKind.Boolean,
        "false",
        Minimum: null,
        Maximum: null,
        Step: null,
        Choices: []);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Could not find repository root.");
    }

    private sealed class ByteAssets(byte[] value) : IRenderPackAssets
    {
        public Stream OpenRead(string assetKey) =>
            new MemoryStream(value, writable: false);
    }

    private sealed class MappedAssets(IReadOnlyDictionary<string, byte[]> values)
        : IRenderPackAssets
    {
        public Stream OpenRead(string assetKey) =>
            new MemoryStream(values[assetKey], writable: false);
    }

    private static MappedAssets VariantAssets(
        PipelineVariantDeclaration variant,
        byte[] vertex,
        byte[] fragment) => new(new Dictionary<string, byte[]>
        {
            [variant.VertexShaderAsset] = vertex,
            [variant.FragmentShaderAsset] = fragment,
        });

    private static void MutateBlockMemberOffset(
        byte[] spirv,
        uint set,
        uint binding,
        int member,
        uint replacement)
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
                words[index + 4] = replacement;
                Buffer.BlockCopy(words, 0, spirv, 0, spirv.Length);
                return;
            }
        }
        throw new InvalidOperationException("Descriptor block member offset was not found.");
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
        var sets = new Dictionary<uint, uint>();
        var bindings = new Dictionary<uint, uint>();
        for (int index = 5; index < words.Length; index += checked((int)(words[index] >> 16)))
        {
            int count = checked((int)(words[index] >> 16));
            if ((words[index] & 0xffff) != 71 || count < 4)
                continue;
            if (words[index + 2] == 34) sets[words[index + 1]] = words[index + 3];
            if (words[index + 2] == 33) bindings[words[index + 1]] = words[index + 3];
        }
        uint variable = sets.Keys.Single(id =>
            sets[id] == set && bindings.GetValueOrDefault(id) == binding);
        uint pointer = 0;
        for (int index = 5; index < words.Length; index += checked((int)(words[index] >> 16)))
        {
            int count = checked((int)(words[index] >> 16));
            if ((words[index] & 0xffff) == 59 && count >= 4 && words[index + 2] == variable)
            {
                pointer = words[index + 1];
                break;
            }
        }
        if (pointer == 0)
            throw new InvalidOperationException("Descriptor variable was not found.");
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
        var words = new uint[bytes.Length / sizeof(uint)];
        Buffer.BlockCopy(bytes, 0, words, 0, bytes.Length);
        return words;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"acdream-render-pack-sdk-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
