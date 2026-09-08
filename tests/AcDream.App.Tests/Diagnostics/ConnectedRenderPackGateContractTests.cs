using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Scene;

namespace AcDream.App.Tests.Diagnostics;

public sealed class ConnectedRenderPackGateContractTests
{
    private const string LifecycleScript = "run-connected-world-lifecycle-gate.ps1";
    private const string SoakScript = "run-connected-r6-soak.ps1";
    private const string PackageLifecycleScript =
        "run-connected-render-pack-package-lifecycle.ps1";
    private const string RemotePlayerScript =
        "run-connected-render-pack-remote-player-gate.ps1";

    [Fact]
    public void ScreenshotJsonCarriesAuthoritativeCasterClassCounters()
    {
        DirectionalShadowTransformChurnDiagnostics churn = default;
        churn = churn with
        {
            CasterClasses = new DirectionalShadowCasterClassDiagnostics(
                TerrainCommands: 1,
                OutdoorStatics: 2,
                Buildings: 3,
                AnimatedStatics: 4,
                LocalPlayers: 5,
                RemotePlayers: 6,
                NonPlayerCreatures: 7,
                OtherLiveDynamics: 8,
                EquippedChildren: 9),
        };

        using JsonDocument json = JsonDocument.Parse(
            JsonSerializer.Serialize(churn));
        JsonElement classes = json.RootElement.GetProperty("CasterClasses");

        Assert.Equal(1, classes.GetProperty("TerrainCommands").GetInt32());
        Assert.Equal(2, classes.GetProperty("OutdoorStatics").GetInt32());
        Assert.Equal(3, classes.GetProperty("Buildings").GetInt32());
        Assert.Equal(4, classes.GetProperty("AnimatedStatics").GetInt32());
        Assert.Equal(5, classes.GetProperty("LocalPlayers").GetInt32());
        Assert.Equal(6, classes.GetProperty("RemotePlayers").GetInt32());
        Assert.Equal(7, classes.GetProperty("NonPlayerCreatures").GetInt32());
        Assert.Equal(8, classes.GetProperty("OtherLiveDynamics").GetInt32());
        Assert.Equal(9, classes.GetProperty("EquippedChildren").GetInt32());
    }
    private const string CommonScript = "connected-render-pack-gate-common.ps1";

    [Fact]
    public void ConnectedGatesExposeTheSameRetailDefaultAndOptionalOverrides()
    {
        foreach (string scriptName in new[] { LifecycleScript, SoakScript })
        {
            string source = ReadTool(scriptName);
            Assert.Contains(
                "[ValidateSet('retail', 'low', 'medium', 'high', 'auto')]",
                source,
                StringComparison.Ordinal);
            Assert.Contains(
                "[string]$RenderPackPreset = 'retail'",
                source,
                StringComparison.Ordinal);
            Assert.Contains(
                "[hashtable]$RenderPackSettingOverrides = @{}",
                source,
                StringComparison.Ordinal);
            Assert.Contains(
                ". (Join-Path $PSScriptRoot 'connected-render-pack-gate-common.ps1')",
                source,
                StringComparison.Ordinal);
            Assert.Contains(
                "-Preset $RenderPackPreset",
                source,
                StringComparison.Ordinal);
            Assert.Contains(
                "-SettingOverrides $RenderPackSettingOverrides",
                source,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SharedSeamPinsSchemaAndOwnsTheCompleteConnectedEnvironmentTransaction()
    {
        string source = ReadTool(CommonScript);
        foreach (string variable in new[]
        {
            "ACDREAM_CONFIG_DIR", "ACDREAM_DATA_DIR", "ACDREAM_CACHE_DIR",
            "ACDREAM_DAT_DIR", "ACDREAM_PAK_PATH", "ACDREAM_LIVE", "ACDREAM_TEST_HOST",
            "ACDREAM_TEST_PORT", "ACDREAM_TEST_USER", "ACDREAM_TEST_PASS",
            "ACDREAM_RETAIL_UI", "ACDREAM_FRAME_PROF", "ACDREAM_FRAME_HISTORY",
            "ACDREAM_UNCAPPED_RENDER", "ACDREAM_DEVTOOLS", "ACDREAM_UI_PROBE_DUMP",
            "ACDREAM_UI_PROBE_SCRIPT", "ACDREAM_AUTOMATION_ARTIFACT_DIR",
            "ACDREAM_DUMP_MOVE_TRUTH", "ACDREAM_NO_AUDIO", "ACDREAM_WB_DIAG",
            "ACDREAM_RENDER_BACKEND", "ACDREAM_NET_DROP_PCT",
            "ACDREAM_NET_DROP_SEED", "ACDREAM_NET_DROP_DIR",
            "ACDREAM_COLLISION_SHADOW_EVERY", "ACDREAM_COLLISION_SHADOW_DIR",
            "ACDREAM_AUTOMATION_EXACT_FRAMEBUFFER", "ACDREAM_DAY_GROUP",
            "ACDREAM_WORLD_TIME", "ACDREAM_SKY_PHASE_SECONDS",
            "ACDREAM_ORBIT_DISTANCE_METERS", "ACDREAM_ORBIT_YAW_DEGREES",
            "ACDREAM_ORBIT_PITCH_DEGREES", "ACDREAM_VULKAN_DEVICE",
            "ACDREAM_VULKAN_FORCE_UNSUPPORTED", "ACDREAM_VULKAN_PROBE",
            "ACDREAM_VULKAN_PROBE_FRAMES",
        })
            Assert.Contains($"'{variable}'", source, StringComparison.Ordinal);
        Assert.Contains("$State.PreviousEnvironment.GetEnumerator()", source, StringComparison.Ordinal);
        Assert.Contains("Get-ChildItem Env:", source, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath \"Env:$name\"", source, StringComparison.Ordinal);
        Assert.Contains("Assert-ConnectedGateContainedPath", source, StringComparison.Ordinal);
        Assert.Contains("Assert-ConnectedGateNoReparsePoint", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShellRoundTripRestoresEveryConnectedVariableIncludingCredentials()
    {
        string root = Path.Combine(Path.GetTempPath(), $"acdream-connected-pack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string common = PsQuote(Path.Combine(FindRepoRoot(), "tools", CommonScript));
            string rootQuoted = PsQuote(root);
            string command = $@"
. {common}
foreach ($name in $script:ConnectedGateEnvironmentNames) {{
  [Environment]::SetEnvironmentVariable($name, ""sentinel-$name"", 'Process')
}}
[Environment]::SetEnvironmentVariable('ACDREAM_FUTURE_GATE_KNOB', 'sentinel-future', 'Process')
$state = New-ConnectedRenderPackGateState -Root {rootQuoted} -Preset medium
$statusFile = Join-Path $state.StateDirectory 'status\fixture.jsonl'
$sessionConfigPath = New-ConnectedGraphicalSessionConfig -State $state -Account 'fixture-account' -Plugins @('fixture.render') -StatusFile $statusFile
$sessionConfig = Get-Content -Raw -LiteralPath $sessionConfigPath | ConvertFrom-Json
$isolated=@($state.PreviousEnvironment.Keys | Where-Object {{
  $_ -notin @('ACDREAM_CONFIG_DIR', 'ACDREAM_DATA_DIR', 'ACDREAM_CACHE_DIR') -and
  [Environment]::GetEnvironmentVariable($_, 'Process') -ne $null
}})
foreach ($name in $script:ConnectedGateEnvironmentNames) {{
  [Environment]::SetEnvironmentVariable($name, ""mutated-$name"", 'Process')
}}
[Environment]::SetEnvironmentVariable('ACDREAM_FUTURE_GATE_KNOB', 'mutated-future', 'Process')
Restore-ConnectedRenderPackGateEnvironment $state
$mismatches=@($script:ConnectedGateEnvironmentNames | Where-Object {{
  [Environment]::GetEnvironmentVariable($_, 'Process') -cne ""sentinel-$_""
}})
$unsafeLeafRejected=$false
try {{ Assert-ConnectedGateSafeLeafName '../escape' }} catch {{ $unsafeLeafRejected=$true }}
$escapeRejected=$false
try {{ Assert-ConnectedGateContainedPath {rootQuoted} (Join-Path {rootQuoted} '..\escape') }} catch {{ $escapeRejected=$true }}
Remove-ConnectedGraphicalSessionConfig -State $state -Path $sessionConfigPath
[pscustomobject]@{{
  Password=$env:ACDREAM_TEST_PASS; User=$env:ACDREAM_TEST_USER;
  Config=$env:ACDREAM_CONFIG_DIR; History=$env:ACDREAM_FRAME_HISTORY;
  Future=$env:ACDREAM_FUTURE_GATE_KNOB; Isolated=$isolated; Mismatches=$mismatches;
  UnsafeLeafRejected=$unsafeLeafRejected; EscapeRejected=$escapeRejected;
  SessionConfigRemoved=(-not (Test-Path -LiteralPath $sessionConfigPath));
  SessionCharacterIndex=$sessionConfig.sessions[0].character.index;
  SessionCredentialProvider=$sessionConfig.sessions[0].credential.provider;
  SessionCredentialReference=$sessionConfig.sessions[0].credential.reference;
  SessionPlugins=@($sessionConfig.sessions[0].plugins);
  SessionStatusFile=$sessionConfig.sessions[0].statusFile;
  Settings=(Get-Content -Raw -LiteralPath (Join-Path $state.ConfigDirectory 'settings.json') | ConvertFrom-Json).display.renderPack.presetId
}} | ConvertTo-Json -Compress";
            using JsonDocument result = JsonDocument.Parse(RunPowerShell(command));
            JsonElement rootElement = result.RootElement;
            Assert.Equal("sentinel-ACDREAM_TEST_PASS", rootElement.GetProperty("Password").GetString());
            Assert.Equal("sentinel-ACDREAM_TEST_USER", rootElement.GetProperty("User").GetString());
            Assert.Equal("sentinel-ACDREAM_CONFIG_DIR", rootElement.GetProperty("Config").GetString());
            Assert.Equal("sentinel-ACDREAM_FRAME_HISTORY", rootElement.GetProperty("History").GetString());
            Assert.Equal("sentinel-future", rootElement.GetProperty("Future").GetString());
            Assert.Empty(rootElement.GetProperty("Isolated").EnumerateArray());
            Assert.Empty(rootElement.GetProperty("Mismatches").EnumerateArray());
            Assert.True(rootElement.GetProperty("UnsafeLeafRejected").GetBoolean());
            Assert.True(rootElement.GetProperty("EscapeRejected").GetBoolean());
            Assert.True(rootElement.GetProperty("SessionConfigRemoved").GetBoolean());
            Assert.Equal(0, rootElement.GetProperty("SessionCharacterIndex").GetInt32());
            Assert.Equal("Environment", rootElement.GetProperty("SessionCredentialProvider").GetString());
            Assert.Equal("ACDREAM_TEST_PASS", rootElement.GetProperty("SessionCredentialReference").GetString());
            Assert.Equal(
                new[] { "fixture.render" },
                rootElement.GetProperty("SessionPlugins").EnumerateArray()
                    .Select(static value => value.GetString()!).ToArray());
            Assert.Equal(
                Path.GetFullPath(Path.Combine(root, "isolated-state", "status", "fixture.jsonl")),
                rootElement.GetProperty("SessionStatusFile").GetString());
            Assert.Equal("medium", rootElement.GetProperty("Settings").GetString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ConnectedGraphicalGatesSelectTheFirstCharacterThroughSessionConfig()
    {
        foreach (string scriptName in new[] { LifecycleScript, SoakScript })
        {
            string source = ReadTool(scriptName);
            Assert.Contains("New-ConnectedGraphicalSessionConfig", source, StringComparison.Ordinal);
            Assert.Contains("-ArgumentList @('--session-config'", source, StringComparison.Ordinal);
            Assert.Contains("Remove-ConnectedGraphicalSessionConfig", source, StringComparison.Ordinal);
        }

        string common = ReadTool(CommonScript);
        Assert.Contains("character = [ordered]@{ index = $CharacterIndex }", common, StringComparison.Ordinal);
        Assert.Contains("provider = 'Environment'", common, StringComparison.Ordinal);
        Assert.Contains("reference = $CredentialEnvironmentVariable", common, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectedPackageLifecycleRunsSixFreshGraphicalHostScenariosFailClosed()
    {
        string source = ReadTool(PackageLifecycleScript);
        Assert.Contains("Get-ConnectedGateBinaryIdentity", source, StringComparison.Ordinal);
        Assert.Contains("New-ConnectedRenderPackGateState -Root $root -Preset retail", source, StringComparison.Ordinal);
        Assert.Contains("-Plugins @($packageId)", source, StringComparison.Ordinal);
        Assert.Contains("-StatusFile $status", source, StringComparison.Ordinal);
        Assert.Contains("pluginLoaded", source, StringComparison.Ordinal);
        Assert.Contains("pluginFailed", source, StringComparison.Ordinal);
        Assert.Contains("'01-installed-v1-selected'", source, StringComparison.Ordinal);
        Assert.Contains("'02-updated-v2-stale-selection'", source, StringComparison.Ordinal);
        Assert.Contains("'03-updated-v2-selected'", source, StringComparison.Ordinal);
        Assert.Contains("'04-package-removed'", source, StringComparison.Ordinal);
        Assert.Contains("'05-registration-failed'", source, StringComparison.Ordinal);
        Assert.Contains("'06-corrected-and-recovered'", source, StringComparison.Ordinal);
        Assert.Contains("BinaryMatchesSource", source, StringComparison.Ordinal);
        Assert.Contains("TrackedSourceStatus", source, StringComparison.Ordinal);
        Assert.Contains("Restore-ConnectedRenderPackGateEnvironment", source, StringComparison.Ordinal);

        string route = ReadTool("connected-render-pack-package-lifecycle.route.txt");
        AssertAppearsInOrder(
            route,
            "wait world-ready 90000",
            "wait world-visible 30000",
            "screenshot package-lifecycle 15000",
            "checkpoint package-lifecycle");
    }

    [Fact]
    public void ConnectedRemotePlayerGateUsesDistinctCharactersAndRequiresCasterEvidence()
    {
        string source = ReadTool(RemotePlayerScript);
        Assert.Contains("PrimaryCharacterIndex = 0", source, StringComparison.Ordinal);
        Assert.Contains("ObserverCharacterIndex = 0", source, StringComparison.Ordinal);
        Assert.Contains("[string]$ObserverAccount = $env:ACDREAM_TEST_OBSERVER_USER", source, StringComparison.Ordinal);
        Assert.Contains("[string]$ObserverPassword = $env:ACDREAM_TEST_OBSERVER_PASS", source, StringComparison.Ordinal);
        Assert.Contains("Remote-player shadow evidence requires distinct ACE accounts", source, StringComparison.Ordinal);
        Assert.Contains("Get-ConnectedGateBinaryIdentity", source, StringComparison.Ordinal);
        Assert.Contains("-CharacterIndex $ObserverCharacterIndex", source, StringComparison.Ordinal);
        Assert.Contains("-CharacterIndex $PrimaryCharacterIndex", source, StringComparison.Ordinal);
        Assert.Contains("[int]$classes.RemotePlayers -lt 1", source, StringComparison.Ordinal);
        Assert.Contains("live: session failed:", source, StringComparison.Ordinal);
        Assert.Contains("DistinctAccountsConfigured", source, StringComparison.Ordinal);
        Assert.Contains("Publish-ClientSignal $observerRoot 'primary-before'", source, StringComparison.Ordinal);
        Assert.Contains("Publish-ClientSignal $primaryRoot 'observer-moved'", source, StringComparison.Ordinal);
        Assert.Contains("both clients entered world with the same character identity", source, StringComparison.Ordinal);
        Assert.Contains("Restore-ConnectedRenderPackGateEnvironment $primaryState", source, StringComparison.Ordinal);
        Assert.Contains("Restore-ConnectedRenderPackGateEnvironment $observerState", source, StringComparison.Ordinal);

        int reportStart = source.IndexOf("$report = [pscustomobject][ordered]@{", StringComparison.Ordinal);
        Assert.True(reportStart >= 0);
        string reportSource = source[reportStart..];
        Assert.DoesNotContain("$Account", reportSource, StringComparison.Ordinal);
        Assert.DoesNotContain("$Password", reportSource, StringComparison.Ordinal);
        Assert.DoesNotContain("$ObserverAccount", reportSource, StringComparison.Ordinal);
        Assert.DoesNotContain("$ObserverPassword", reportSource, StringComparison.Ordinal);

        string observer = ReadTool("connected-render-pack-remote-observer.route.txt");
        AssertAppearsInOrder(
            observer,
            "command /teleloc 0x09040008",
            "wait materialized 1 90000",
            "checkpoint remote-observer-ready",
            "wait signal primary-before 120000",
            "input down MovementForward",
            "input up MovementForward",
            "checkpoint remote-observer-moved");

        string primary = ReadTool("connected-render-pack-remote-primary.route.txt");
        AssertAppearsInOrder(
            primary,
            "command /teleloc 0x09040008",
            "wait materialized 1 90000",
            "screenshot remote-player-before 15000",
            "wait signal observer-moved 120000",
            "screenshot remote-player-after 15000",
            "checkpoint remote-primary");
    }

    [Fact]
    public void EveryConnectedScreenshotMustProveExactFailureFreeActivation()
    {
        string common = ReadTool(CommonScript);
        Assert.Contains("screenshots\\$name.metadata.json", common, StringComparison.Ordinal);
        Assert.Contains("@('PackId', [string]$State.PackId)", common, StringComparison.Ordinal);
        Assert.Contains("@('PresetId', [string]$State.PresetId)", common, StringComparison.Ordinal);
        Assert.Contains("[int]$actual.State -ne [int]$State.ExpectedState", common, StringComparison.Ordinal);
        Assert.Contains(
            "[string]::IsNullOrWhiteSpace([string]$actual.FailureReason)",
            common,
            StringComparison.Ordinal);
        Assert.Contains("SchemaVersion", common, StringComparison.Ordinal);
        Assert.Contains("ActivationGeneration", common, StringComparison.Ordinal);
        Assert.Contains("EffectiveQuality", common, StringComparison.Ordinal);
        Assert.Contains("retained GPU byte ledgers disagree", common, StringComparison.Ordinal);
        Assert.Contains("expected zero pack work", common, StringComparison.Ordinal);
        Assert.Contains("recorded no complete pack graph work", common, StringComparison.Ordinal);
        Assert.Contains("positive shadow strength but no shadow casters", common, StringComparison.Ordinal);

        string lifecycle = ReadTool(LifecycleScript);
        Assert.Contains("-ScreenshotNames @($name)", lifecycle, StringComparison.Ordinal);
        Assert.Contains("-Label $Label", lifecycle, StringComparison.Ordinal);

        string soak = ReadTool(SoakScript);
        Assert.Contains("-ScreenshotNames $expectedCheckpointNames", soak, StringComparison.Ordinal);
        Assert.Contains("-Label $runName", soak, StringComparison.Ordinal);
    }

    [Fact]
    public void LifecycleGateExecutesAtomicTransitionsResizeEnvironmentAndFreshContextRow()
    {
        string lifecycle = ReadTool(LifecycleScript);
        Assert.Contains("if ($RenderPackPreset -eq 'medium')", lifecycle, StringComparison.Ordinal);
        Assert.Contains("connected-render-pack-transitions.route.txt", lifecycle, StringComparison.Ordinal);
        Assert.Contains("Get-ConnectedRenderPackExpectation -Preset high", lifecycle, StringComparison.Ordinal);
        Assert.Contains("Get-ConnectedRenderPackExpectation -Preset retail", lifecycle, StringComparison.Ordinal);
        Assert.Contains("ScreenshotStateOverrides", lifecycle, StringComparison.Ordinal);
        Assert.Contains("Get-FreshContextRecreationGate $capped $uncapped", lifecycle, StringComparison.Ordinal);
        Assert.Contains("StartTimeUtc", lifecycle, StringComparison.Ordinal);
        Assert.Contains("resized screenshot was", lifecycle, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("authored time change did not alter published sun elevation", lifecycle, StringComparison.Ordinal);
        Assert.Contains("first weather edge did not publish Overcast", lifecycle, StringComparison.Ordinal);
        Assert.Contains("$selected.ShadowTransformChurn.CasterClasses", lifecycle, StringComparison.Ordinal);
        foreach (string casterClass in new[]
        {
            "TerrainCommands",
            "OutdoorStatics",
            "Buildings",
            "AnimatedStatics",
            "LocalPlayers",
            "NonPlayerCreatures",
            "EquippedChildren",
        })
        {
            Assert.Contains(casterClass, lifecycle, StringComparison.Ordinal);
        }
        Assert.Contains("second live client", lifecycle, StringComparison.Ordinal);
        Assert.Contains("not hostile monster", lifecycle, StringComparison.Ordinal);
        Assert.Contains("no authoritative tree discriminator", lifecycle, StringComparison.Ordinal);

        string route = ReadTool("connected-render-pack-transitions.route.txt");
        AssertAppearsInOrder(
            route,
            "renderpack select high",
            "wait render-pack high 90000",
            "renderpack disable",
            "wait render-pack retail 90000",
            "renderpack reenable",
            "wait render-pack high 90000",
            "resize 1024 768",
            "wait framebuffer 1024 768 30000",
            "input press AcdreamCycleTimeOfDay",
            "input press AcdreamCycleWeather",
            "input press AcdreamCycleWeather",
            "checkpoint atmospheric_transitions");
        Assert.Contains("transition_selected_high", route, StringComparison.Ordinal);
        Assert.Contains("transition_disabled_retail", route, StringComparison.Ordinal);
        Assert.Contains("transition_reenabled_high", route, StringComparison.Ordinal);
        Assert.Contains("transition_resized_high", route, StringComparison.Ordinal);
        Assert.Contains("transition_overcast_high", route, StringComparison.Ordinal);
        Assert.Contains("transition_rain_high", route, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutableMetadataGateAcceptsAutoQualityAndRejectsLedgerOrVersionDrift()
    {
        string root = Path.Combine(Path.GetTempPath(), $"acdream-connected-metadata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "screenshots"));
        string path = Path.Combine(root, "screenshots", "checkpoint.metadata.json");
        try
        {
            var document = new
            {
                SchemaVersion = 1,
                RenderPack = new
                {
                    PackId = "acdream.atmospheric",
                    PackVersion = "1.0.0",
                    PresetId = "auto",
                    State = 2,
                    ActivationGeneration = 3,
                    EffectiveQuality = "medium",
                    FailureReason = (string?)null,
                    RetainedGpuBytes = 123L,
                    TransientGpuBytes = 4L,
                    ImageCount = 4,
                    BufferCount = 2,
                    DrawCalls = 6,
                    DispatchCalls = 0,
                    ShadowCasterCount = 22,
                    CascadeDrawCount = 3,
                    CpuClassificationCalls = 0,
                    SharedWorldTransformUsedInstances = 68_395u,
                    Outdoor = true,
                    DirectionalShadowStrength = 0.75,
                    Passes = new[]
                    {
                        new { PassId = "directional-shadow", GpuMilliseconds = 0.2, DrawCalls = 2, DispatchCalls = 0 },
                    },
                    Performance = new { ResidentGpuBytes = 123L, TransientGpuBytes = 4L },
                },
            };
            File.WriteAllText(path, JsonSerializer.Serialize(document));
            JsonElement valid = RunConnectedMetadataGate(root);
            Assert.Empty(valid.GetProperty("Failures").EnumerateArray());

            JsonNode invalid = JsonNode.Parse(File.ReadAllText(path))!;
            invalid["SchemaVersion"] = 2;
            invalid["RenderPack"]!["PackVersion"] = "9.9.9";
            invalid["RenderPack"]!["EffectiveQuality"] = "ultra";
            invalid["RenderPack"]!["RetainedGpuBytes"] = 999;
            File.WriteAllText(path, invalid.ToJsonString());
            JsonElement rejected = RunConnectedMetadataGate(root);
            string failures = rejected.GetProperty("Failures").ToString();
            Assert.Contains("schema was 2", failures, StringComparison.Ordinal);
            Assert.Contains("PackVersion '9.9.9'", failures, StringComparison.Ordinal);
            Assert.Contains("effective quality 'ultra'", failures, StringComparison.Ordinal);
            Assert.Contains("retained GPU byte ledgers disagree", failures, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ExecutableMetadataGateRejectsActiveNoWorkAndRetailPackWork()
    {
        string root = Path.Combine(Path.GetTempPath(), $"acdream-connected-work-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "screenshots"));
        string path = Path.Combine(root, "screenshots", "checkpoint.metadata.json");
        try
        {
            var activeNoWork = new
            {
                SchemaVersion = 1,
                RenderPack = new
                {
                    PackId = "acdream.atmospheric",
                    PackVersion = "1.0.0",
                    PresetId = "auto",
                    State = 2,
                    ActivationGeneration = 1,
                    EffectiveQuality = "low",
                    FailureReason = (string?)null,
                    RetainedGpuBytes = 0L,
                    TransientGpuBytes = 0L,
                    ImageCount = 0,
                    BufferCount = 0,
                    DrawCalls = 0,
                    DispatchCalls = 0,
                    ShadowCasterCount = 0,
                    CascadeDrawCount = 0,
                    CpuClassificationCalls = 0,
                    SharedWorldTransformUsedInstances = 0u,
                    Outdoor = true,
                    DirectionalShadowStrength = 0.5,
                    Passes = Array.Empty<object>(),
                    Performance = new { ResidentGpuBytes = 0L, TransientGpuBytes = 0L },
                },
            };
            File.WriteAllText(path, JsonSerializer.Serialize(activeNoWork));
            JsonElement activeRejected = RunConnectedMetadataGate(root);
            string activeFailures = activeRejected.GetProperty("Failures").ToString();
            Assert.Contains("recorded no complete pack graph work", activeFailures, StringComparison.Ordinal);
            Assert.Contains("positive shadow strength but no shadow casters", activeFailures, StringComparison.Ordinal);
            Assert.Contains("positive shadow strength but no combined shared-world-transform usage", activeFailures, StringComparison.Ordinal);

            var retailWork = new
            {
                SchemaVersion = 1,
                RenderPack = new
                {
                    PackId = "retail",
                    PackVersion = (string?)null,
                    PresetId = "off",
                    State = 0,
                    ActivationGeneration = 0,
                    EffectiveQuality = "off",
                    FailureReason = (string?)null,
                    RetainedGpuBytes = 64L,
                    TransientGpuBytes = 0L,
                    ImageCount = 1,
                    BufferCount = 0,
                    DrawCalls = 1,
                    DispatchCalls = 0,
                    ShadowCasterCount = 0,
                    CascadeDrawCount = 0,
                    CpuClassificationCalls = 0,
                    SharedWorldTransformUsedInstances = 1u,
                    Outdoor = false,
                    DirectionalShadowStrength = 0.0,
                    Passes = new[]
                    {
                        new { PassId = "unexpected", GpuMilliseconds = 0.1, DrawCalls = 1, DispatchCalls = 0 },
                    },
                    Performance = new { ResidentGpuBytes = 64L, TransientGpuBytes = 0L },
                },
            };
            File.WriteAllText(path, JsonSerializer.Serialize(retailWork));
            JsonElement retailRejected = RunConnectedMetadataGate(root, "retail");
            string retailFailures = retailRejected.GetProperty("Failures").ToString();
            Assert.Contains("expected zero pack work", retailFailures, StringComparison.Ordinal);
            Assert.Contains("recorded pack passes, expected none", retailFailures, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void BothGatesFailClosedOnUnprovableBinaryIdentity()
    {
        string common = ReadTool(CommonScript);
        Assert.Contains("Measured binary commit $binaryCommit differs", common, StringComparison.Ordinal);
        Assert.Contains("status --short --untracked-files=all", common, StringComparison.Ordinal);
        Assert.Contains("Connected closeout evidence cannot prove binary/source identity", common, StringComparison.Ordinal);
        foreach (string scriptName in new[] { LifecycleScript, SoakScript })
            Assert.Contains("Get-ConnectedGateBinaryIdentity", ReadTool(scriptName), StringComparison.Ordinal);
    }

    [Fact]
    public void BothGatesRestoreEnvironmentAndRecordRequestedSelection()
    {
        foreach (string scriptName in new[] { LifecycleScript, SoakScript })
        {
            string source = ReadTool(scriptName);
            int selectionStart = source.IndexOf(
                "$renderPackGate = New-ConnectedRenderPackGateState",
                StringComparison.Ordinal);
            Assert.True(selectionStart >= 0, $"{scriptName} does not initialize isolated state.");
            string selectionScope = source[selectionStart..];

            AssertAppearsInOrder(
                selectionScope,
                "$renderPackGate = New-ConnectedRenderPackGateState",
                "try {",
                "RenderPackSelection = (Get-ConnectedRenderPackGateReport $renderPackGate)",
                "finally {",
                "Restore-ConnectedRenderPackGateEnvironment $renderPackGate");
            Assert.Contains(
                "Add-ConnectedRenderPackMetadataFailures",
                source,
                StringComparison.Ordinal);
            Assert.Contains("Start-Process -FilePath $exe", source, StringComparison.Ordinal);
        }
    }

    private static string ReadTool(string fileName) => File.ReadAllText(Path.Combine(
        FindRepoRoot(),
        "tools",
        fileName));

    private static string PsQuote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string RunPowerShell(string command)
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
        start.ArgumentList.Add(command);
        using Process process = Process.Start(start)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "PowerShell did not exit.");
        Assert.True(process.ExitCode == 0, $"PowerShell failed.\n{stdout}\n{stderr}");
        return stdout.Trim();
    }

    private static JsonElement RunConnectedMetadataGate(
        string artifactRoot,
        string requestedPreset = "auto")
    {
        string common = PsQuote(Path.Combine(FindRepoRoot(), "tools", CommonScript));
        string root = PsQuote(artifactRoot);
        string preset = PsQuote(requestedPreset);
        string packId = PsQuote(requestedPreset == "retail" ? "retail" : "acdream.atmospheric");
        string packVersion = requestedPreset == "retail" ? "$null" : "'1.0.0'";
        string presetId = PsQuote(requestedPreset == "retail" ? "off" : requestedPreset);
        int expectedState = requestedPreset == "retail" ? 0 : 2;
        string command = $@"
. {common}
$state=[pscustomobject]@{{RequestedPreset={preset};PackId={packId};PackVersion={packVersion};PresetId={presetId};ExpectedState={expectedState};ExpectedSchemaVersion=1}}
$failures=[Collections.Generic.List[string]]::new()
Add-ConnectedRenderPackMetadataFailures -ArtifactDirectory {root} -ScreenshotNames @('checkpoint') -State $state -Failures $failures -Label synthetic
[pscustomobject]@{{Failures=@($failures)}} | ConvertTo-Json -Depth 5 -Compress";
        using JsonDocument document = JsonDocument.Parse(RunPowerShell(command));
        return document.RootElement.Clone();
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
