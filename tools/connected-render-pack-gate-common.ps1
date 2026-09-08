
$script:ConnectedGateEnvironmentNames = @(
    'ACDREAM_AUTOMATION_ARTIFACT_DIR', 'ACDREAM_CACHE_DIR',
    'ACDREAM_COLLISION_SHADOW_DIR', 'ACDREAM_COLLISION_SHADOW_EVERY',
    'ACDREAM_CONFIG_DIR', 'ACDREAM_DATA_DIR', 'ACDREAM_DAT_DIR',
    'ACDREAM_DEVTOOLS', 'ACDREAM_DUMP_MOVE_TRUTH', 'ACDREAM_FRAME_HISTORY',
    'ACDREAM_FRAME_PROF', 'ACDREAM_LIVE', 'ACDREAM_NET_DROP_DIR',
    'ACDREAM_NET_DROP_PCT', 'ACDREAM_NET_DROP_SEED', 'ACDREAM_NO_AUDIO',
    'ACDREAM_PAK_PATH', 'ACDREAM_RENDER_BACKEND', 'ACDREAM_RETAIL_UI',
    'ACDREAM_AUTOMATION_EXACT_FRAMEBUFFER', 'ACDREAM_DAY_GROUP',
    'ACDREAM_WORLD_TIME', 'ACDREAM_SKY_PHASE_SECONDS',
    'ACDREAM_ORBIT_DISTANCE_METERS', 'ACDREAM_ORBIT_YAW_DEGREES',
    'ACDREAM_ORBIT_PITCH_DEGREES', 'ACDREAM_VULKAN_DEVICE',
    'ACDREAM_VULKAN_FORCE_UNSUPPORTED', 'ACDREAM_VULKAN_PROBE',
    'ACDREAM_VULKAN_PROBE_FRAMES', 'ACDREAM_TEST_HOST',
    'ACDREAM_TEST_PASS', 'ACDREAM_TEST_PORT', 'ACDREAM_TEST_USER',
    'ACDREAM_UI_PROBE_DUMP', 'ACDREAM_UI_PROBE_SCRIPT',
    'ACDREAM_UNCAPPED_RENDER', 'ACDREAM_WB_DIAG'
)

function Assert-ConnectedGateSafeLeafName {
    param([Parameter(Mandatory = $true)][string]$Name)
    if ($Name -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,79}$') {
        throw "Unsafe screenshot leaf name '$Name'."
    }
}

function Assert-ConnectedGateContainedPath {
    param([Parameter(Mandatory = $true)][string]$Root,
          [Parameter(Mandatory = $true)][string]$Path)
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $pathFull = [IO.Path]::GetFullPath($Path)
    $prefix = $rootFull + [IO.Path]::DirectorySeparatorChar
    if (-not $pathFull.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path '$pathFull' is not contained by gate root '$rootFull'."
    }
    return $pathFull
}

function Assert-ConnectedGateNoReparsePoint {
    param([Parameter(Mandatory = $true)][string]$Path)
    if ((Get-Item -LiteralPath $Path).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Gate path must not be a reparse point: '$Path'."
    }
}

function Get-ConnectedRenderPackExpectation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('retail', 'low', 'medium', 'high', 'auto')]
        [string]$Preset
    )
    return [pscustomobject][ordered]@{
        RequestedPreset = $Preset
        PackId = if ($Preset -eq 'retail') { 'retail' } else { 'acdream.atmospheric' }
        PackVersion = if ($Preset -eq 'retail') { $null } else { '1.0.0' }
        PresetId = if ($Preset -eq 'retail') { 'off' } else { $Preset }
        ExpectedState = if ($Preset -eq 'retail') { 0 } else { 2 }
        ExpectedSchemaVersion = 1
    }
}

function New-ConnectedRenderPackGateState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)]
        [ValidateSet('retail', 'low', 'medium', 'high', 'auto')]
        [string]$Preset,
        [hashtable]$SettingOverrides = @{}
    )

    if ($Preset -eq 'retail' -and $SettingOverrides.Count -ne 0) {
        throw 'Render-pack setting overrides require an enhanced render-pack preset.'
    }
    $rootFull = [IO.Path]::GetFullPath($Root)
    if (-not (Test-Path -LiteralPath $rootFull -PathType Container)) {
        throw "Connected gate root does not exist: '$rootFull'."
    }
    Assert-ConnectedGateNoReparsePoint $rootFull
    $stateDirectory = Assert-ConnectedGateContainedPath $rootFull (Join-Path $rootFull 'isolated-state')
    if (Test-Path -LiteralPath $stateDirectory) {
        throw "Isolated gate state already exists: '$stateDirectory'."
    }

    $transactionNames = @(
        $script:ConnectedGateEnvironmentNames
        Get-ChildItem Env: |
            Where-Object { $_.Name -like 'ACDREAM_*' } |
            Select-Object -ExpandProperty Name
    ) | Sort-Object -Unique
    $previous = [ordered]@{}
    foreach ($name in $transactionNames) {
        $previous[$name] = [Environment]::GetEnvironmentVariable(
            $name, [EnvironmentVariableTarget]::Process)
        Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
    }

    $configDirectory = Join-Path $stateDirectory 'config'
    $dataDirectory = Join-Path $stateDirectory 'data'
    $cacheDirectory = Join-Path $stateDirectory 'cache'
    $null = New-Item -ItemType Directory -Path $configDirectory, $dataDirectory, $cacheDirectory
    foreach ($path in @($stateDirectory, $configDirectory, $dataDirectory, $cacheDirectory)) {
        Assert-ConnectedGateNoReparsePoint $path
    }

    $packId = if ($Preset -eq 'retail') { 'retail' } else { 'acdream.atmospheric' }
    $packVersion = if ($Preset -eq 'retail') { $null } else { '1.0.0' }
    $presetId = if ($Preset -eq 'retail') { 'off' } else { $Preset }
    $expectedState = if ($Preset -eq 'retail') { 0 } else { 2 }
    $orderedOverrides = [ordered]@{}
    foreach ($key in @($SettingOverrides.Keys | Sort-Object)) {
        if ([string]::IsNullOrWhiteSpace([string]$key)) {
            throw 'Render-pack setting override IDs cannot be empty.'
        }
        $orderedOverrides[[string]$key] = [string]$SettingOverrides[$key]
    }

    [ordered]@{
        display = [ordered]@{ renderPack = [ordered]@{
            packId = $packId; packVersion = $packVersion; presetId = $presetId
            settingOverrides = $orderedOverrides
        } }
        version = 3
    } | ConvertTo-Json -Depth 8 |
        Set-Content -Encoding utf8 -LiteralPath (Join-Path $configDirectory 'settings.json')

    [Environment]::SetEnvironmentVariable('ACDREAM_CONFIG_DIR', $configDirectory, 'Process')
    [Environment]::SetEnvironmentVariable('ACDREAM_DATA_DIR', $dataDirectory, 'Process')
    [Environment]::SetEnvironmentVariable('ACDREAM_CACHE_DIR', $cacheDirectory, 'Process')

    return [pscustomobject][ordered]@{
        RequestedPreset = $Preset; PackId = $packId; PackVersion = $packVersion
        PresetId = $presetId; ExpectedState = $expectedState; ExpectedSchemaVersion = 1
        SettingOverrides = $orderedOverrides; StateDirectory = $stateDirectory
        ConfigDirectory = $configDirectory; DataDirectory = $dataDirectory
        CacheDirectory = $cacheDirectory; PreviousEnvironment = $previous
    }
}

function Restore-ConnectedRenderPackGateEnvironment {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][object]$State)
    foreach ($entry in $State.PreviousEnvironment.GetEnumerator()) {
        if ($null -eq $entry.Value) {
            Remove-Item -LiteralPath "Env:$($entry.Key)" -ErrorAction SilentlyContinue
        }
        else {
            [Environment]::SetEnvironmentVariable(
                [string]$entry.Key, $entry.Value, [EnvironmentVariableTarget]::Process)
        }
    }
}

function New-ConnectedGraphicalSessionConfig {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object]$State,
        [Parameter(Mandatory = $true)][string]$Account,
        [string]$HostName = '127.0.0.1',
        [ValidateRange(1, 65535)][int]$Port = 9000,
        [ValidateRange(0, [int]::MaxValue)][int]$CharacterIndex = 0,
        [string]$CredentialEnvironmentVariable = 'ACDREAM_TEST_PASS',
        [string[]]$Plugins = @(),
        [string]$StatusFile
    )

    if ([string]::IsNullOrWhiteSpace($Account)) {
        throw 'Connected graphical session account cannot be empty.'
    }
    if ([string]::IsNullOrWhiteSpace($HostName)) {
        throw 'Connected graphical session host cannot be empty.'
    }
    if ($CredentialEnvironmentVariable -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
        throw "Unsafe credential environment-variable name '$CredentialEnvironmentVariable'."
    }

    $path = Assert-ConnectedGateContainedPath $State.StateDirectory (
        Join-Path $State.StateDirectory 'graphical-session.json')
    if (Test-Path -LiteralPath $path) {
        throw "Connected graphical session config already exists: '$path'."
    }

    $session = [ordered]@{
        id = 'connected-gate'
        endpoint = [ordered]@{ host = $HostName; port = $Port }
        account = $Account
        character = [ordered]@{ index = $CharacterIndex }
        credential = [ordered]@{
            provider = 'Environment'
            reference = $CredentialEnvironmentVariable
        }
    }
    if ($PSBoundParameters.ContainsKey('Plugins')) {
        $orderedPlugins = @($Plugins | ForEach-Object {
            if ([string]::IsNullOrWhiteSpace($_)) {
                throw 'Connected graphical session plugin IDs cannot be empty.'
            }
            $_
        } | Sort-Object -Unique)
        $session['plugins'] = $orderedPlugins
    }
    if ($PSBoundParameters.ContainsKey('StatusFile')) {
        if ([string]::IsNullOrWhiteSpace($StatusFile)) {
            throw 'Connected graphical session status file cannot be empty.'
        }
        $session['statusFile'] = Assert-ConnectedGateContainedPath `
            $State.StateDirectory $StatusFile
    }

    [ordered]@{
        version = 1
        sessions = @($session)
    } | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $path -Encoding utf8
    return $path
}

function Remove-ConnectedGraphicalSessionConfig {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object]$State,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $containedPath = Assert-ConnectedGateContainedPath $State.StateDirectory $Path
    if (Test-Path -LiteralPath $containedPath -PathType Leaf) {
        Remove-Item -LiteralPath $containedPath -Force
    }
}

function Get-ConnectedGateBinaryIdentity {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Repository,
          [Parameter(Mandatory = $true)][string]$Executable,
          [switch]$SkipBuild)
    $sourceCommit = (& git -C $Repository rev-parse HEAD).Trim().ToLowerInvariant()
    $trackedStatus = @(& git -C $Repository status --short --untracked-files=all)
    $productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($Executable).ProductVersion
    $match = [regex]::Match([string]$productVersion, '(?i)(?<![0-9a-f])([0-9a-f]{40})(?![0-9a-f])')
    $binaryCommit = if ($match.Success) { $match.Groups[1].Value.ToLowerInvariant() } else { $null }
    if ($null -eq $binaryCommit) {
        throw "Measured binary ProductVersion does not contain a full source commit: '$productVersion'."
    }
    if ($binaryCommit -cne $sourceCommit) {
        throw "Measured binary commit $binaryCommit differs from checked-out source commit $sourceCommit."
    }
    if ($trackedStatus.Count -ne 0) {
        throw 'Connected closeout evidence cannot prove binary/source identity while source changes are present.'
    }
    return [pscustomobject][ordered]@{
        SourceCommit = $sourceCommit; SourceTrackedStatus = $trackedStatus
        BinaryProductVersion = $productVersion; BinaryCommit = $binaryCommit
        BinaryMatchesSource = $true; SkipBuild = [bool]$SkipBuild
    }
}

function Add-ConnectedRenderPackMetadataFailures {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
        [Parameter(Mandatory = $true)][string[]]$ScreenshotNames,
        [Parameter(Mandatory = $true)][object]$State,
        [Parameter(Mandatory = $true)][System.Collections.IList]$Failures,
        [Parameter(Mandatory = $true)][string]$Label
    )
    try {
        Assert-ConnectedGateNoReparsePoint $ArtifactDirectory
        $screenshotsDirectory = Assert-ConnectedGateContainedPath $ArtifactDirectory (
            Join-Path $ArtifactDirectory 'screenshots')
        if (Test-Path -LiteralPath $screenshotsDirectory -PathType Container) {
            Assert-ConnectedGateNoReparsePoint $screenshotsDirectory
        }
    }
    catch {
        $null = $Failures.Add("${Label}: unsafe artifact directory: $($_.Exception.Message)")
        return
    }
    foreach ($name in $ScreenshotNames) {
        try { Assert-ConnectedGateSafeLeafName $name }
        catch { $null = $Failures.Add("${Label}: $($_.Exception.Message)"); continue }
        $metadataPath = Assert-ConnectedGateContainedPath $ArtifactDirectory (
            Join-Path $ArtifactDirectory "screenshots\$name.metadata.json")
        if (-not (Test-Path -LiteralPath $metadataPath)) {
            $null = $Failures.Add("${Label}: render-pack metadata is missing for screenshot '$name'")
            continue
        }
        try {
            $metadata = Get-Content -Raw -LiteralPath $metadataPath | ConvertFrom-Json
            if ([int]$metadata.SchemaVersion -ne [int]$State.ExpectedSchemaVersion) {
                $null = $Failures.Add("${Label}: screenshot '$name' schema was $($metadata.SchemaVersion), expected $($State.ExpectedSchemaVersion)")
            }
            $actual = $metadata.RenderPack
            if ($null -eq $actual) {
                $null = $Failures.Add("${Label}: screenshot '$name' has no RenderPack metadata")
                continue
            }
            foreach ($comparison in @(
                @('PackId', [string]$State.PackId),
                @('PackVersion', [string]$State.PackVersion),
                @('PresetId', [string]$State.PresetId))) {
                $property = [string]$comparison[0]; $expected = [string]$comparison[1]
                if ([string]$actual.$property -cne $expected) {
                    $null = $Failures.Add("${Label}: screenshot '$name' $property '$($actual.$property)', expected '$expected'")
                }
            }
            if ([int]$actual.State -ne [int]$State.ExpectedState) {
                $null = $Failures.Add("${Label}: screenshot '$name' state was $($actual.State), expected $($State.ExpectedState)")
            }
            $expectedGeneration = if ($State.RequestedPreset -eq 'retail') { 0 } else { 1 }
            if ([int]$actual.ActivationGeneration -lt $expectedGeneration) {
                $null = $Failures.Add("${Label}: screenshot '$name' activation generation was $($actual.ActivationGeneration), expected at least $expectedGeneration")
            }
            $allowedQuality = if ($State.RequestedPreset -eq 'auto') { @('low', 'medium', 'high') }
                elseif ($State.RequestedPreset -eq 'retail') { @('off') }
                else { @([string]$State.RequestedPreset) }
            if ([string]$actual.EffectiveQuality -cnotin $allowedQuality) {
                $null = $Failures.Add("${Label}: screenshot '$name' effective quality '$($actual.EffectiveQuality)' is invalid for '$($State.RequestedPreset)'")
            }
            if (-not [string]::IsNullOrWhiteSpace([string]$actual.FailureReason)) {
                $null = $Failures.Add("${Label}: screenshot '$name' render-pack failure: $($actual.FailureReason)")
            }
            if ([long]$actual.RetainedGpuBytes -ne [long]$actual.Performance.ResidentGpuBytes) {
                $null = $Failures.Add("${Label}: screenshot '$name' retained GPU byte ledgers disagree")
            }
            if ([long]$actual.TransientGpuBytes -ne [long]$actual.Performance.TransientGpuBytes) {
                $null = $Failures.Add("${Label}: screenshot '$name' transient GPU byte ledgers disagree")
            }
            $worldTransformUsage =
                $actual.PSObject.Properties['SharedWorldTransformUsedInstances']
            if ($null -eq $worldTransformUsage) {
                $null = $Failures.Add(
                    "${Label}: screenshot '$name' has no combined shared-world-transform usage")
            }
            if ($State.RequestedPreset -eq 'retail') {
                foreach ($property in @(
                    'RetainedGpuBytes',
                    'TransientGpuBytes',
                    'ImageCount',
                    'BufferCount',
                    'DrawCalls',
                    'DispatchCalls',
                    'ShadowCasterCount',
                    'CascadeDrawCount',
                    'CpuClassificationCalls',
                    'SharedWorldTransformUsedInstances')) {
                    if ([long]$actual.$property -ne 0) {
                        $null = $Failures.Add(
                            "${Label}: retail screenshot '$name' $property was $($actual.$property), expected zero pack work")
                    }
                }
                if (@($actual.Passes).Count -ne 0) {
                    $null = $Failures.Add(
                        "${Label}: retail screenshot '$name' recorded pack passes, expected none")
                }
            }
            else {
                if ([long]$actual.RetainedGpuBytes -le 0 -or
                    [int]$actual.ImageCount -le 0 -or
                    ([int]$actual.DrawCalls + [int]$actual.DispatchCalls) -le 0 -or
                    @($actual.Passes).Count -le 0) {
                    $null = $Failures.Add(
                        "${Label}: enhanced screenshot '$name' was active but recorded no complete pack graph work")
                }

                if ([bool]$actual.Outdoor -and
                    [double]$actual.DirectionalShadowStrength -gt 0) {
                    $expectedCascades = @{
                        low = 2
                        medium = 3
                        high = 4
                    }[[string]$actual.EffectiveQuality]
                    if ([int]$actual.ShadowCasterCount -le 0) {
                        $null = $Failures.Add(
                            "${Label}: enhanced outdoor screenshot '$name' had positive shadow strength but no shadow casters")
                    }
                    if ($null -ne $worldTransformUsage -and
                        [long]$worldTransformUsage.Value -le 0) {
                        $null = $Failures.Add(
                            "${Label}: enhanced outdoor screenshot '$name' had positive shadow strength but no combined shared-world-transform usage")
                    }
                    if ($null -eq $expectedCascades -or
                        [int]$actual.CascadeDrawCount -ne [int]$expectedCascades) {
                        $null = $Failures.Add(
                            "${Label}: enhanced outdoor screenshot '$name' rendered $($actual.CascadeDrawCount) cascades, expected $expectedCascades for '$($actual.EffectiveQuality)'")
                    }
                }
            }
        }
        catch {
            $null = $Failures.Add("${Label}: render-pack metadata for screenshot '$name' is invalid: $($_.Exception.Message)")
        }
    }
}

function Get-ConnectedRenderPackGateReport {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][object]$State)
    return [pscustomobject][ordered]@{
        RequestedPreset = $State.RequestedPreset; PackId = $State.PackId
        PackVersion = $State.PackVersion; PresetId = $State.PresetId
        ExpectedActivationState = $State.ExpectedState
        ExpectedMetadataSchemaVersion = $State.ExpectedSchemaVersion
        SettingOverrides = $State.SettingOverrides
        IsolatedStateDirectory = $State.StateDirectory
    }
}
