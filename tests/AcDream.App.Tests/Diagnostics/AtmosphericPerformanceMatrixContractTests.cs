using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AcDream.App.Tests.Diagnostics;

public sealed class AtmosphericPerformanceMatrixContractTests
{
    [Fact]
    public void ScriptsParseWithoutLaunchingTheMatrix()
    {
        foreach (string script in new[]
        {
            ScriptPath(),
            Path.Combine(FindRepoRoot(), "tools", "run-offline-pixel-gate.ps1"),
            Path.Combine(FindRepoRoot(), "tools", "atmospheric-performance-matrix-common.ps1"),
        })
            AssertPowerShellParses(script);
    }

    private static void AssertPowerShellParses(string script)
    {
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
            $"PowerShell parser failed for {script} with exit code {process.ExitCode}.\n{stdout}\n{stderr}");
    }

    [Fact]
    public void MatrixPinsAllRequiredRowsAndExplicitFramePacingModes()
    {
        string source = ReadScript();

        Assert.Contains("[string]$FramePacing = 'both'", source, StringComparison.Ordinal);
        Assert.Contains(
            "[ValidateSet('capped', 'uncapped', 'both')]",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "[string[]]$PresetSet = @('retail', 'low', 'medium', 'high', 'auto')",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "[string[]]$ResolutionSet = @('1920x1080', '2560x1440', '3840x2160')",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$presets = @($PresetSet | ForEach-Object { $_.ToLowerInvariant() } | Select-Object -Unique)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$resolutions = @($ResolutionSet | Select-Object -Unique)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("default { @('capped', 'uncapped') }", source, StringComparison.Ordinal);
        Assert.Contains("if ($pacing -eq 'uncapped')", source, StringComparison.Ordinal);
        Assert.Contains("$arguments += '-Uncapped'", source, StringComparison.Ordinal);
        Assert.Contains(
            "'-RequiredRenderPackSamples', '2048'",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "'-RenderPackSampleTimeoutMs', '300000'",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ExplicitPresetPerformanceWindowResetAfterWarmup = $true",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomaticPerformanceWindowPolicy",
            source,
            StringComparison.Ordinal);
        Assert.Contains("'-AllowSafeRenderPackFallback'", source, StringComparison.Ordinal);

        AssertAppearsInOrder(
            source,
            "$rowDirectory = Assert-MatrixContainedPath $outputRoot (Join-Path $outputRoot $rowId)",
            "'-Out', $rowDirectory",
            "'-WarmupMs', \"$WarmupMs\"",
            "'-DayGroup', \"$DayGroup\"",
            "'-WorldDayFraction'",
            "'-SkyPhaseSeconds'",
            "'-MsaaSamples', '0'",
            "'-RenderPackPreset', $preset",
            "'-Resolution', $resolution",
            "'-OrbitDistanceMeters'",
            "'-SkipBuild'");

        string pixelGate = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "tools", "run-offline-pixel-gate.ps1"));
        Assert.Contains(
            "$probeCommands.Add('sleep 2000')",
            pixelGate,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DeclaredCeilingsAreExactAndNewIncrementalMetricsAreRequired()
    {
        string source = ReadScript();

        AssertPresetBudget(source, "low", "0.15", "0.50", "2.00", "3.00", "64L");
        AssertPresetBudget(source, "medium", "0.25", "0.75", "3.25", "4.50", "128L");
        AssertPresetBudget(source, "high", "0.35", "1.00", "4.50", "6.00", "256L");

        foreach (string field in new[]
        {
            "IncrementalCpuMillisecondsP50",
            "IncrementalCpuMillisecondsP95",
            "IncrementalCpuMillisecondsP99",
            "AbsoluteReceiverCpuMillisecondsP50",
            "AbsoluteReceiverCpuMillisecondsP95",
            "AbsoluteReceiverCpuMillisecondsP99",
            "InclusiveGpuMillisecondsP50",
            "InclusiveGpuMillisecondsP95",
            "InclusiveGpuMillisecondsP99",
            "ResidentGpuBytes",
        })
        {
            Assert.Contains($"'{field}'", source, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("'CpuMillisecondsP50'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("'CpuMillisecondsP99'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("'GpuMillisecondsP50'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("'GpuMillisecondsP99'", source, StringComparison.Ordinal);

        Assert.Contains(
            "$gpuBudgetApplies = $availability -eq 'Active' -and",
            source,
            StringComparison.Ordinal);
        Assert.Contains("$budget = $budgets[$effectiveQuality]", source, StringComparison.Ordinal);
        Assert.Contains(
            "$cpuP50 -gt $budget.IncrementalCpuMillisecondsP50",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$cpuP99 -gt $budget.IncrementalCpuMillisecondsP99",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$residentGpuBytes -gt $residentCeiling",
            source,
            StringComparison.Ordinal);
        Assert.Contains("$referencePixels = 1920L * 1080L", source, StringComparison.Ordinal);
        Assert.Contains(
            "[Math]::Ceiling([double]$budget.ResidentGpuBytes * $rowPixels / $referencePixels)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("$rowPixels -le $referencePixels", source, StringComparison.Ordinal);
        Assert.Contains(
            "$gpuP50 -gt $budget.InclusiveGpuMillisecondsP50At1080p",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$gpuP99 -gt $budget.InclusiveGpuMillisecondsP99At1080p",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MetadataAndSummariesCarryTheRequiredEvidenceWithoutSecrets()
    {
        string source = ReadScript();

        Assert.Contains(
            "$screenshotLeaf = 'world-offline'",
            source,
            StringComparison.Ordinal);
        foreach (string evidence in new[]
        {
            "CpuSampleCount",
            "GpuSampleCount",
            "ShadowCasterCount",
            "CascadeDrawCount",
            "DrawCalls",
            "DispatchCalls",
        })
        {
            Assert.Contains($"'{evidence}'", source, StringComparison.Ordinal);
        }

        Assert.Contains("atmospheric-performance-matrix.json", source, StringComparison.Ordinal);
        Assert.Contains("atmospheric-performance-matrix.md", source, StringComparison.Ordinal);
        Assert.Contains("DeclaredBudgets", source, StringComparison.Ordinal);
        Assert.Contains("Rows = @($rows)", source, StringComparison.Ordinal);
        Assert.Contains("Failures = @($matrixFailures)", source, StringComparison.Ordinal);

        Assert.DoesNotContain("ACDREAM_TEST_USER", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ACDREAM_TEST_PASS", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-ChildItem Env:", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutableMetadataOracleAcceptsCompleteShapeAndRejectsAdversarialEvidence()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"acdream-matrix-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string metadataPath = Path.Combine(directory, "capture.metadata.json");
            foreach (string preset in new[] { "low", "medium", "high" })
            {
                File.WriteAllText(metadataPath, CreateMetadata(preset));
                JsonElement valid = RunMetadataOracle(metadataPath, preset);
                Assert.True(valid.GetProperty("Passed").GetBoolean());
                Assert.Equal(9498, valid.GetProperty("ShadowCasterCount").GetInt32());
            }

            JsonNode automatic = JsonNode.Parse(CreateMetadata("high"))!;
            automatic["RenderPack"]!["PresetId"] = "auto";
            automatic["RenderPack"]!["ActivationGeneration"] = 4;
            File.WriteAllText(metadataPath, automatic.ToJsonString());
            JsonElement validAutomatic = RunMetadataOracle(metadataPath, "auto");
            Assert.True(validAutomatic.GetProperty("Passed").GetBoolean());
            Assert.Equal(
                "high",
                validAutomatic.GetProperty("EffectiveQuality").GetString());

            File.WriteAllText(metadataPath, CreateMetadata("high"));
            JsonNode invalid = JsonNode.Parse(File.ReadAllText(metadataPath))!;
            JsonNode pack = invalid["RenderPack"]!;
            pack["ShadowCasterCount"] = 0;
            pack["CascadeDrawCount"] = 3;
            pack["CpuClassificationCalls"] = 1;
            pack["RetainedGpuBytes"] = 99;
            pack["Performance"]!["CpuSampleCount"] = 2047;
            pack["Performance"]!["IncrementalCpuMillisecondsP95"] = -1.0;
            pack["Passes"]![1]!["DrawCalls"] = 4;
            File.WriteAllText(metadataPath, invalid.ToJsonString());

            JsonElement rejected = RunMetadataOracle(metadataPath, "high");
            Assert.False(rejected.GetProperty("Passed").GetBoolean());
            string failures = rejected.GetProperty("Failures").ToString();
            Assert.Contains("at least one shadow caster", failures, StringComparison.Ordinal);
            Assert.Contains("exactly 4 cascades", failures, StringComparison.Ordinal);
            Assert.Contains("zero CPU classifications", failures, StringComparison.Ordinal);
            Assert.Contains("GPU bytes disagree", failures, StringComparison.Ordinal);
            Assert.Contains("complete 2048-sample window", failures, StringComparison.Ordinal);
            Assert.Contains("finite and non-negative", failures, StringComparison.Ordinal);
            Assert.Contains("exactly 5 draws and zero dispatches", failures, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ExecutableMetadataOracleReportsOnlyStrictZeroWorkUnavailability()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"acdream-matrix-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string metadataPath = Path.Combine(directory, "capture.metadata.json");
            const string resourceReason =
                "Directional shadow rendering failed: Render pack preset 'low' needs 67465216 resident GPU bytes after materializing its scene-dependent shadow command buffers; the active pack budget is 67108864 bytes.";
            File.WriteAllText(metadataPath, CreateFallbackMetadata(resourceReason));

            JsonElement resourceUnavailable = RunMetadataOracle(
                metadataPath,
                "low",
                allowSafeFallback: true);
            Assert.True(resourceUnavailable.GetProperty("Passed").GetBoolean());
            Assert.Equal("Unavailable", resourceUnavailable.GetProperty("Outcome").GetString());
            Assert.Equal(
                "ResourceUnavailable",
                resourceUnavailable.GetProperty("UnavailableClassification").GetString());
            Assert.Equal(resourceReason, resourceUnavailable.GetProperty("FailureReason").GetString());

            JsonElement automaticUnavailable = RunMetadataOracle(
                metadataPath,
                "auto",
                allowSafeFallback: true);
            Assert.True(automaticUnavailable.GetProperty("Passed").GetBoolean());
            Assert.Equal(
                "ResourceUnavailable",
                automaticUnavailable.GetProperty("UnavailableClassification").GetString());

            JsonElement notOptedIn = RunMetadataOracle(metadataPath, "low");
            Assert.False(notOptedIn.GetProperty("Passed").GetBoolean());
            Assert.Contains(
                "not explicitly allowed",
                notOptedIn.GetProperty("Failures").ToString(),
                StringComparison.Ordinal);

            const string capabilityReason =
                "Preset 'low' requires unsupported capability 'MultiviewDirectionalShadowCascades'.";
            File.WriteAllText(metadataPath, CreateFallbackMetadata(capabilityReason));
            JsonElement capabilityUnavailable = RunMetadataOracle(
                metadataPath,
                "low",
                allowSafeFallback: true);
            Assert.True(capabilityUnavailable.GetProperty("Passed").GetBoolean());
            Assert.Equal(
                "CapabilityUnavailable",
                capabilityUnavailable.GetProperty("UnavailableClassification").GetString());

            const string performanceReason =
                "Automatic quality disabled render pack 'acdream.atmospheric' because Low remained over its declared performance budget for 180 stable samples: GPU p99 24.234 ms (budget 3.000 ms), CPU p99 0.630 ms (budget 0.500 ms), resident GPU bytes 41648404 (budget 67108864).";
            File.WriteAllText(metadataPath, CreateFallbackMetadata(performanceReason));
            JsonElement performanceUnavailable = RunMetadataOracle(
                metadataPath,
                "auto",
                allowSafeFallback: true);
            Assert.True(performanceUnavailable.GetProperty("Passed").GetBoolean());
            Assert.Equal(
                "PerformanceUnavailable",
                performanceUnavailable.GetProperty("UnavailableClassification").GetString());

            JsonElement explicitLowCannotUseAutoPerformanceFallback = RunMetadataOracle(
                metadataPath,
                "low",
                allowSafeFallback: true);
            Assert.False(
                explicitLowCannotUseAutoPerformanceFallback.GetProperty("Passed").GetBoolean());

            const string forgedWithinBudget =
                "Automatic quality disabled render pack 'acdream.atmospheric' because Low remained over its declared performance budget for 180 stable samples: GPU p99 2.000 ms (budget 3.000 ms), CPU p99 0.400 ms (budget 0.500 ms), resident GPU bytes 41648404 (budget 67108864).";
            File.WriteAllText(metadataPath, CreateFallbackMetadata(forgedWithinBudget));
            JsonElement forged = RunMetadataOracle(
                metadataPath,
                "auto",
                allowSafeFallback: true);
            Assert.False(forged.GetProperty("Passed").GetBoolean());

            const string arbitraryFailure =
                "Render pack 'acdream.atmospheric' could not be prepared: shader validation failed: unsupported binding.";
            File.WriteAllText(metadataPath, CreateFallbackMetadata(arbitraryFailure));
            JsonElement unexpected = RunMetadataOracle(metadataPath, "low", allowSafeFallback: true);
            Assert.False(unexpected.GetProperty("Passed").GetBoolean());
            Assert.Equal(
                "UnexpectedFailure",
                unexpected.GetProperty("UnavailableClassification").GetString());
            Assert.Contains(
                "not a strict resource/capability/Auto-performance unavailability",
                unexpected.GetProperty("Failures").ToString(),
                StringComparison.Ordinal);

            JsonNode unsafeFallback = JsonNode.Parse(CreateFallbackMetadata(resourceReason))!;
            unsafeFallback["RenderPack"]!["ShadowCasterCount"] = 1;
            unsafeFallback["RenderPack"]!["Performance"]!["GpuSampleCount"] = 1;
            File.WriteAllText(metadataPath, unsafeFallback.ToJsonString());
            JsonElement rejectedWork = RunMetadataOracle(metadataPath, "low", allowSafeFallback: true);
            Assert.False(rejectedWork.GetProperty("Passed").GetBoolean());
            string failures = rejectedWork.GetProperty("Failures").ToString();
            Assert.Contains("zero pack work and resources", failures, StringComparison.Ordinal);
            Assert.Contains("zero GpuSampleCount", failures, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string CreateMetadata(string preset)
    {
        int cascades = preset switch { "low" => 2, "medium" => 3, _ => 4 };
        const int shadowDraws = 5;
        var passIds = new List<string> { "atmospheric-world-receiver" };
        if (preset == "low")
            passIds.Add("directional-shadow-multiview");
        else for (int cascade = 0; cascade < cascades; cascade++)
            passIds.Add($"directional-shadow-cascade-{cascade}");
        passIds.AddRange([
            "atmospheric-sun-occlusion", "atmospheric-sun-rays",
            "atmospheric-volumetric-shafts", "atmospheric-bloom-downsample",
            "atmospheric-bloom-blur-horizontal", "atmospheric-bloom-blur-vertical",
            "atmospheric-filmic"]);
        object[] passes = passIds.Select((id, index) => new
        {
            PassId = id,
            GpuMilliseconds = 0.1,
            DrawCalls = id.StartsWith("directional-", StringComparison.Ordinal) ? shadowDraws :
                id is "atmospheric-world-receiver" or "atmospheric-volumetric-shafts" ? 0 : 1,
            DispatchCalls = 0,
        }).Cast<object>().ToArray();
        var performance = new
        {
            CpuSampleCount = 2048,
            AbsoluteReceiverCpuSampleCount = 2048,
            GpuSampleCount = 2048,
            IncrementalCpuMillisecondsP50 = 0.1,
            IncrementalCpuMillisecondsP95 = 0.2,
            IncrementalCpuMillisecondsP99 = 0.3,
            AbsoluteReceiverCpuMillisecondsP50 = 1.0,
            AbsoluteReceiverCpuMillisecondsP95 = 1.5,
            AbsoluteReceiverCpuMillisecondsP99 = 2.0,
            InclusiveGpuMillisecondsP50 = 2.0,
            InclusiveGpuMillisecondsP95 = 3.0,
            InclusiveGpuMillisecondsP99 = 4.0,
            ResidentGpuBytes = 1000L,
            TransientGpuBytes = 0L,
        };
        return JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            Width = 1920,
            Height = 1080,
            RenderPack = new
            {
                State = 2,
                PackId = "acdream.atmospheric",
                PackVersion = "1.0.0",
                PresetId = preset,
                EffectiveQuality = preset,
                FailureReason = (string?)null,
                ActivationGeneration = 1,
                RetainedGpuBytes = 1000L,
                TransientGpuBytes = 0L,
                ImageCount = 1,
                BufferCount = 1,
                DrawCalls = (preset == "low" ? shadowDraws : shadowDraws * cascades) + 6,
                DispatchCalls = 0,
                ShadowCasterCount = 9498,
                CascadeDrawCount = cascades,
                CpuClassificationCalls = 0,
                Passes = passes,
                Performance = performance,
            },
        });
    }

    private static string CreateFallbackMetadata(string failureReason) => JsonSerializer.Serialize(new
    {
        SchemaVersion = 1,
        Width = 1920,
        Height = 1080,
        RenderPack = new
        {
            State = 3,
            PackId = "retail",
            PackVersion = (string?)null,
            PresetId = "off",
            EffectiveQuality = "off",
            FailureReason = failureReason,
            ActivationGeneration = 2,
            RetainedGpuBytes = 0L,
            TransientGpuBytes = 0L,
            ImageCount = 0,
            BufferCount = 0,
            DrawCalls = 0,
            DispatchCalls = 0,
            ShadowCasterCount = 0,
            CascadeDrawCount = 0,
            CpuClassificationCalls = 0,
            Passes = Array.Empty<object>(),
            Performance = new
            {
                CpuSampleCount = 0,
                AbsoluteReceiverCpuSampleCount = 0,
                GpuSampleCount = 0,
                IncrementalCpuMillisecondsP50 = 0.0,
                IncrementalCpuMillisecondsP95 = 0.0,
                IncrementalCpuMillisecondsP99 = 0.0,
                AbsoluteReceiverCpuMillisecondsP50 = 0.0,
                AbsoluteReceiverCpuMillisecondsP95 = 0.0,
                AbsoluteReceiverCpuMillisecondsP99 = 0.0,
                InclusiveGpuMillisecondsP50 = 0.0,
                InclusiveGpuMillisecondsP95 = 0.0,
                InclusiveGpuMillisecondsP99 = 0.0,
                ResidentGpuBytes = 0L,
                TransientGpuBytes = 0L,
            },
        },
    });

    private static JsonElement RunMetadataOracle(
        string metadataPath,
        string preset,
        bool allowSafeFallback = false)
    {
        string helper = Path.Combine(FindRepoRoot(), "tools", "atmospheric-performance-matrix-common.ps1");
        string quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
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
        start.ArgumentList.Add(
            $". {quote(helper)}; Test-AtmosphericPerformanceMetadataEvidence " +
            $"-MetadataPath {quote(metadataPath)} -Preset {preset} -ExpectedWidth 1920 " +
            "-ExpectedHeight 1080 " +
            (allowSafeFallback ? "-AllowSafeFallback " : string.Empty) +
            "| ConvertTo-Json -Depth 8 -Compress");
        using Process process = Process.Start(start)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "PowerShell oracle did not exit.");
        Assert.True(process.ExitCode == 0, $"PowerShell oracle failed.\n{stdout}\n{stderr}");
        using JsonDocument document = JsonDocument.Parse(stdout);
        return document.RootElement.Clone();
    }

    private static void AssertPresetBudget(
        string source,
        string preset,
        string cpuP50,
        string cpuP99,
        string gpuP50,
        string gpuP99,
        string memory)
    {
        int start = source.IndexOf($"{preset} = [pscustomobject]", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing {preset} budget.");
        int end = source.IndexOf("    }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Malformed {preset} budget.");
        string budget = source[start..end];

        Assert.Contains($"IncrementalCpuMillisecondsP50 = {cpuP50}", budget);
        Assert.Contains($"IncrementalCpuMillisecondsP99 = {cpuP99}", budget);
        Assert.Contains($"InclusiveGpuMillisecondsP50At1080p = {gpuP50}", budget);
        Assert.Contains($"InclusiveGpuMillisecondsP99At1080p = {gpuP99}", budget);
        Assert.Contains($"ResidentGpuBytes = {memory} * 1024L * 1024L", budget);
    }

    private static string ReadScript() => File.ReadAllText(ScriptPath());

    private static string ScriptPath() => Path.Combine(
        FindRepoRoot(),
        "tools",
        "run-atmospheric-performance-matrix.ps1");

    private static void AssertAppearsInOrder(string source, params string[] values)
    {
        int cursor = -1;
        foreach (string value in values)
        {
            int next = source.IndexOf(value, cursor + 1, StringComparison.Ordinal);
            Assert.True(next >= 0, $"Missing expected source fragment: {value}");
            Assert.True(next > cursor, $"Out-of-order source fragment: {value}");
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
