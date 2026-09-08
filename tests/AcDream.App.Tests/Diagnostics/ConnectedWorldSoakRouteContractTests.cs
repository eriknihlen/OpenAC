namespace AcDream.App.Tests.Diagnostics;

public sealed class ConnectedWorldSoakRouteContractTests
{
    private static readonly string[] ExpectedCheckpointNames =
    [
        "caul-baseline",
        "sawato-baseline",
        "rynthid",
        "aerlinthe",
        "sawato-return",
        "holtburg",
        "caul-return",
        "sawato-plateau",
        "caul-plateau",
    ];

    [Fact]
    public void RouteContainsExactlyNineOrderedCheckpointBarriers()
    {
        string routePath = Path.Combine(
            FindRepoRoot(),
            "tools",
            "connected-r6-soak.route.txt");
        string[] checkpoints = File.ReadAllLines(routePath)
            .Select(static line => line.Trim())
            .Where(static line => line.StartsWith(
                "checkpoint ",
                StringComparison.Ordinal))
            .Select(static line => line["checkpoint ".Length..])
            .ToArray();

        Assert.Equal(ExpectedCheckpointNames, checkpoints);

        string[] teleports = File.ReadAllLines(routePath)
            .Select(static line => line.Trim())
            .Where(static line => line.StartsWith(
                "command /teleloc ",
                StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(9, teleports.Length);
        Assert.All(teleports, static line => Assert.EndsWith(
            " 1 0 0 0",
            line,
            StringComparison.Ordinal));

        string[] screenshots = File.ReadAllLines(routePath)
            .Select(static line => line.Trim())
            .Where(static line => line.StartsWith(
                "screenshot ",
                StringComparison.Ordinal))
            .Select(static line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1])
            .ToArray();
        Assert.Equal(ExpectedCheckpointNames, screenshots);
    }

    [Fact]
    public void DenseTownRoutePinsArwicViewAndScreenshot()
    {
        string[] route = File.ReadAllLines(Path.Combine(
                FindRepoRoot(),
                "tools",
                "connected-dense-town.route.txt"))
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();

        AssertAppearsInOrder(
            string.Join('\n', route),
            "command /telepoi Arwic",
            "wait materialized 1 60000",
            "input down MovementTurnRight",
            "sleep 6000",
            "input up MovementTurnRight",
            "screenshot arwic-dense 10000",
            "checkpoint arwic-dense");
    }

    [Fact]
    public void PerformanceRoutesPinNoonBeforeTheirFirstTeleport()
    {
        foreach (string routeName in new[]
        {
            "connected-r6-soak.route.txt",
            "connected-dense-town.route.txt",
        })
        {
            string source = File.ReadAllText(Path.Combine(
                FindRepoRoot(),
                "tools",
                routeName));
            int firstTeleport = source.IndexOf("command /tele", StringComparison.Ordinal);
            Assert.True(firstTeleport > 0, $"{routeName} has no teleport command.");
            string preTeleport = source[..firstTeleport];
            Assert.Equal(
                3,
                CountOccurrences(
                    preTeleport,
                    "input press AcdreamCycleTimeOfDay"));
        }
    }

    [Fact]
    public void GateConsumesCanonicalTimelineWithoutWeakeningResidencyFormulas()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "tools",
            "run-connected-r6-soak.ps1"));

        AssertAppearsInOrder(
            source,
            "$expectedCheckpointNames = @(",
            "'caul-baseline'",
            "'sawato-baseline'",
            "'rynthid'",
            "'aerlinthe'",
            "'sawato-return'",
            "'holtburg'",
            "'caul-return'",
            "'sawato-plateau'",
            "'caul-plateau'");
        AssertAppearsInOrder(
            source,
            "$canonicalCheckpoints = @(Read-CanonicalCheckpoints)",
            "Add-CanonicalCheckpointFailures $process",
            "Add-RelativeGateFailures");
        Assert.Contains("CanonicalCheckpoints = @($canonicalCheckpoints)", source);
        Assert.Contains("CheckpointTimeline = $checkpointTimeline", source);
        Assert.Contains(
            "Join-Path $artifactDir \"checkpoint-$expectedName.json\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "missing render-world synchronization fields",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "canonical cache '$field' grew",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if ($workloadChanged) { $warnings.Add($message) }",
            source,
            StringComparison.Ordinal);

        string[] zeroFields =
        [
            "pendingLiveTeardowns",
            "pendingLandblockRetirements",
            "stagedMeshUploads",
            "stagedMeshBytes",
            "compositeWarmupPending",
        ];
        foreach (string field in zeroFields)
            Assert.Contains($"'{field}'", source, StringComparison.Ordinal);

        string[] revealFields =
        [
            "materialized",
            "completed",
            "worldViewportObserved",
            "cancelled",
            "invariantFailureCount",
            "isReady",
        ];
        foreach (string field in revealFields)
            Assert.Contains($"'{field}'", source, StringComparison.Ordinal);

        string[] streamingZeroFields =
        [
            "deferredCompletions",
            "deferredAdoptedCpuBytes",
            "pendingPublications",
            "pendingRetirements",
            "workerCompletionBacklog",
            "destinationBacklog",
            "controlBacklog",
            "unloadBacklog",
            "nearBacklog",
            "farBacklog",
        ];
        foreach (string field in streamingZeroFields)
            Assert.Contains($"'{field}'", source, StringComparison.Ordinal);

        string[] streamingEvidenceFields =
        [
            "lifetimeFrameOverrunCount",
            "lifetimeOversizedProgressCount",
            "maximumFrameMilliseconds",
            "maximumFrameStage",
            "maximumOperationMilliseconds",
            "maximumOperationStage",
        ];
        foreach (string field in streamingEvidenceFields)
            Assert.Contains($"'{field}'", source, StringComparison.Ordinal);

        Assert.Equal(1, CountOccurrences(
            source,
            "$privateLimit = [Math]::Max(192.0, $pair.First.PrivateMiB * 0.20)"));
        Assert.Equal(1, CountOccurrences(
            source,
            "$workingLimit = [Math]::Max(192.0, $pair.First.WorkingSetMiB * 0.20)"));
        Assert.Equal(1, CountOccurrences(
            source,
            "$updateLimit = $pair.First.UpdateP95Ms * 1.5 + 0.5"));
        Assert.Equal(1, CountOccurrences(
            source,
            "$allocLimit = $pair.First.AllocP50Kb * 1.5 + 16.0"));
    }

    [Fact]
    public void UncappedRenderDefaultsToCappedAndIsGatedByAParameter()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "tools",
            "run-connected-r6-soak.ps1"));

        Assert.Contains("[switch]$Uncapped", source, StringComparison.Ordinal);
        Assert.Contains(
            "$env:ACDREAM_UNCAPPED_RENDER = if ($Uncapped) { '1' } else { $null }",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$env:ACDREAM_UNCAPPED_RENDER = '1'",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AtmosphericOfflineGateDefaultsToCappedAndExposesUncappedMeasurement()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "tools",
            "run-offline-pixel-gate.ps1"));

        Assert.Contains("[switch]$Uncapped", source, StringComparison.Ordinal);
        Assert.Contains(
            "$env:ACDREAM_UNCAPPED_RENDER = if ($Uncapped) { '1' } else { $null }",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$previousUncappedRender = $env:ACDREAM_UNCAPPED_RENDER",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$env:ACDREAM_UNCAPPED_RENDER = $previousUncappedRender",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$env:ACDREAM_AUTOMATION_EXACT_FRAMEBUFFER = '1'",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$previousExactFramebuffer = $env:ACDREAM_AUTOMATION_EXACT_FRAMEBUFFER",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$env:ACDREAM_AUTOMATION_EXACT_FRAMEBUFFER = $previousExactFramebuffer",
            source,
            StringComparison.Ordinal);
        Assert.Contains("-WindowStyle Hidden", source, StringComparison.Ordinal);
        Assert.DoesNotContain("-WindowStyle Minimized", source, StringComparison.Ordinal);
        Assert.Contains(
            "[int]$RequiredRenderPackSamples = 0",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$probeCommands.Add('renderpack reset-performance')",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if ($RenderPackPreset -ne 'auto')",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "wait render-pack-samples $RequiredRenderPackSamples $RenderPackSampleTimeoutMs",
            source,
            StringComparison.Ordinal);
        int reset = source.IndexOf(
            "$probeCommands.Add('renderpack reset-performance')",
            StringComparison.Ordinal);
        int wait = source.IndexOf(
            "wait render-pack-samples $RequiredRenderPackSamples $RenderPackSampleTimeoutMs",
            StringComparison.Ordinal);
        int screenshot = source.IndexOf(
            "$probeCommands.Add('screenshot world-offline 30000')",
            StringComparison.Ordinal);
        int closeClient = source.IndexOf(
            "$probeCommands.Add('close-client')",
            StringComparison.Ordinal);
        Assert.True(
            reset >= 0 && wait > reset && screenshot > wait
            && closeClient > screenshot);
        Assert.Contains("$proc.WaitForExit(15000)", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Get-Process -Name AcDream.App",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(".CloseMainWindow()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AtmosphericPreviewLaunchesAcdreamWithDisposableStateAndNoLiveCredentials()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "tools",
            "launch-atmospheric-preview.ps1"));

        Assert.Contains("AcDream.App.exe", source, StringComparison.Ordinal);
        Assert.Contains("packId = 'acdream.atmospheric'", source, StringComparison.Ordinal);
        Assert.Contains("artifacts\\atmospheric-rendering\\visible-", source, StringComparison.Ordinal);
        Assert.Contains("$env:ACDREAM_CONFIG_DIR = $config", source, StringComparison.Ordinal);
        Assert.Contains("$env:ACDREAM_DATA_DIR = $data", source, StringComparison.Ordinal);
        Assert.Contains("$env:ACDREAM_CACHE_DIR = $cache", source, StringComparison.Ordinal);
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
        Assert.Contains(
            "$env:ACDREAM_NO_AUDIO = if ($audioEnabled) { $null } else { '1' }",
            source,
            StringComparison.Ordinal);
        Assert.Contains("-RedirectStandardOutput $stdoutLog", source, StringComparison.Ordinal);
        Assert.Contains("-RedirectStandardError $stderrLog", source, StringComparison.Ordinal);
        Assert.DoesNotContain("-WindowStyle Hidden", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StationaryDwellSamplesAfterTheLivenessDeadline()
    {
        string script = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "tools",
            "run-connected-r6-soak.ps1"));
        string route = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "tools",
            "connected-r6-soak.route.txt"));

        AssertAppearsInOrder(
            script,
            "$checkpointPattern =",
            "Wait-ForLogPattern $process $stdoutLog $checkpointPattern",
            "$stationaryProfiles = Get-FrameProfiles $stdoutLog",
            "Capture-Sample $process $destination.Name 'stationary'");
        Assert.Equal(9, CountOccurrences(route, "sleep 27000"));
        Assert.Equal(9, CountOccurrences(route, "sleep 3000"));
    }

    [Fact]
    public void LaunchConfigurationIsDisclosedToTheArtifactDirectoryBeforeLaunch()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "tools",
            "run-connected-r6-soak.ps1"));

        AssertAppearsInOrder(
            source,
            "$artifactDir = \"$prefix.artifacts\"",
            "$null = New-Item -ItemType Directory -Force -Path $artifactDir",
            "$renderPackGate = New-ConnectedRenderPackGateState",
            "Get-ChildItem Env: | Where-Object { $_.Name -like 'ACDREAM_*' }",
            "Join-Path $artifactDir 'env-disclosure.json'",
            "try {");
        Assert.Contains(
            "EnvDisclosure = Join-Path $artifactDir 'env-disclosure.json'",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$sensitive = $_.Name -match '(?i)(PASS|PASSWORD|TOKEN|SECRET|KEY|USER|ACCOUNT)'",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Value = if ($sensitive) { '<redacted>' } else { $_.Value }",
            source,
            StringComparison.Ordinal);
        // ACDREAM_DUMP_MOVE_TRUTH stays SET (the exercise gate greps its
        // output) — it is now disclosed, not removed.
        Assert.Contains("$env:ACDREAM_DUMP_MOVE_TRUTH = '1'", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MoveTruthGrepPatternIsBackedByALiveEmitter()
    {
        string script = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "tools",
            "run-connected-r6-soak.ps1"));
        Assert.Contains("'move-truth OUT'", script, StringComparison.Ordinal);

        string emitter = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src", "AcDream.App", "Input",
            "MovementTruthDiagnosticController.cs"));
        Assert.Contains("move-truth OUT", emitter, StringComparison.Ordinal);

        string options = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "AcDream.App", "RuntimeOptions.cs"));
        Assert.Contains(
            @"env(""ACDREAM_DUMP_MOVE_TRUTH"")",
            options,
            StringComparison.Ordinal);
        string window = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "AcDream.App", "Rendering", "GameWindow.cs"));
        Assert.Contains(
            "options.DumpMoveTruth",
            window,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BaselineIdentityComesFromTheMeasuredBinaryRatherThanOnlyTheCheckout()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "tools",
            "run-connected-r6-soak.ps1"));

        string common = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "tools", "connected-render-pack-gate-common.ps1"));
        AssertAppearsInOrder(
            source,
            "$binaryIdentity = Get-ConnectedGateBinaryIdentity",
            "$sourceCommit = $binaryIdentity.SourceCommit",
            "$binaryCommit = $binaryIdentity.BinaryCommit",
            "$commit = $binaryCommit");
        Assert.Contains("[Diagnostics.FileVersionInfo]::GetVersionInfo($Executable).ProductVersion", common);
        Assert.Contains("status --short --untracked-files=all", common, StringComparison.Ordinal);
        Assert.Contains("BinaryProductVersion = $binaryProductVersion", source);
        Assert.Contains("BinaryCommit = $binaryCommit", source);
        Assert.Contains("BinaryMatchesSource = $binaryMatchesSource", source);
        Assert.Contains("TrackedSourceStatus = @($sourceStatus)", source);
        Assert.Contains(
            "Measured binary commit $binaryCommit differs from checked-out source commit $sourceCommit",
            common,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SoakExportsAndSummarizesFrameHistory()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "tools",
            "run-connected-r6-soak.ps1"));

        Assert.Contains(
            "$env:ACDREAM_FRAME_HISTORY = $frameHistory",
            source,
            StringComparison.Ordinal);
        AssertAppearsInOrder(
            source,
            "& dotnet $cliDll summarize-frame-history",
            "$frameHistory",
            "$checkpointTimeline",
            "$markerLog",
            "$frameSummary");
        Assert.Contains("FrameHistory = $frameHistory", source, StringComparison.Ordinal);
        Assert.Contains("FrameSummary = $frameSummary", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ContentionCaptureIsExplicitAndIncludesStackEvents()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "tools",
            "run-connected-r6-soak.ps1"));

        Assert.Contains("[switch]$CaptureContention", source, StringComparison.Ordinal);
        Assert.Contains(
            "'Microsoft-Windows-DotNETRuntime:0x4000:5'",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ContentionTrace = if ($CaptureContention) { $contentionTrace } else { $null }",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeCountersCaptureProcessWideAllocationByDefault()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "tools",
            "run-connected-r6-soak.ps1"));

        Assert.Contains("[switch]$SkipRuntimeCounters", source, StringComparison.Ordinal);
        AssertAppearsInOrder(
            source,
            "if (-not $SkipRuntimeCounters) {",
            "'--counters', 'System.Runtime'",
            "'--refresh-interval', '1'",
            "'--format', 'csv'",
            "'--output', $runtimeCounters");
        Assert.Contains(
            "RuntimeCounters = if (-not $SkipRuntimeCounters) { $runtimeCounters } else { $null }",
            source,
            StringComparison.Ordinal);
        AssertAppearsInOrder(
            source,
            "Wait-ForLogPattern $process $stdoutLog 'live: in world'",
            "if (-not $SkipRuntimeCounters) {",
            "Start-Process -FilePath 'dotnet-counters'",
            "if ($CaptureContention) {",
            "Start-Process -FilePath 'dotnet-trace'");
    }

    [Fact]
    public void LoginGateDoesNotRequireTheOptionalFirstPositionRecenterDiagnostic()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "tools",
            "run-connected-r6-soak.ps1"));

        Assert.Contains(
            "Wait-ForLogPattern $process $stdoutLog 'live: in world'",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Wait-ForLogPattern $process $stdoutLog 'live: first player position'",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DenseTownModeUsesTheDedicatedOneCheckpointRoute()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "tools",
            "run-connected-r6-soak.ps1"));

        Assert.Contains("[switch]$DenseTown", source, StringComparison.Ordinal);
        AssertAppearsInOrder(
            source,
            "if ($DenseTown) {",
            "Name = 'Arwic dense'",
            "$expectedCheckpointNames = @('arwic-dense')",
            "$routeFileName = 'connected-dense-town.route.txt'",
            "$runName = 'connected-dense-town'");
        Assert.Contains("if (-not $DenseTown) {", source, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int cursor = 0;
        while ((cursor = source.IndexOf(value, cursor, StringComparison.Ordinal)) >= 0)
        {
            count++;
            cursor += value.Length;
        }

        return count;
    }

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
