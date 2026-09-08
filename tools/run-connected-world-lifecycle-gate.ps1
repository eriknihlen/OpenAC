[CmdletBinding()]
param(
    [string]$Repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$Account = $env:ACDREAM_TEST_USER,
    [string]$Password = $env:ACDREAM_TEST_PASS,
    [string]$PreparedAssetPath,
    [string]$AceLogPath = 'C:\ACE\Server\ACE_Log.txt',
    [switch]$SkipBuild,
    [int]$SessionTimeoutSeconds = 420,
    [int]$CollisionShadowEvery = 0,
    [ValidateSet('retail', 'low', 'medium', 'high', 'auto')]
    [string]$RenderPackPreset = 'retail',
    [hashtable]$RenderPackSettingOverrides = @{}
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'connected-render-pack-gate-common.ps1')

if ([string]::IsNullOrWhiteSpace($Account)) { $Account = 'testaccount' }
if ([string]::IsNullOrWhiteSpace($Password)) { $Password = 'testpassword' }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$root = Join-Path $Repository "logs\connected-world-gate-$stamp"
$null = New-Item -ItemType Directory -Force -Path $root
$reportPath = Join-Path $root 'report.json'
$exe = Join-Path $Repository 'src\AcDream.App\bin\Release\net10.0\AcDream.App.exe'
$failures = [System.Collections.Generic.List[string]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()
$sessions = [System.Collections.Generic.List[object]]::new()
$startedUtc = [DateTime]::UtcNow

function Get-PatternCount([string]$Path, [string]$Pattern) {
    if (-not (Test-Path -LiteralPath $Path)) { return 0 }
    return @(Get-Content -LiteralPath $Path -ErrorAction SilentlyContinue |
        Select-String -SimpleMatch $Pattern).Count
}

function Wait-ForPattern(
    [Diagnostics.Process]$Client,
    [string]$Path,
    [string]$Pattern,
    [int]$TimeoutSeconds)
{
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $Client.Refresh()
        if ($Client.HasExited) {
            throw "client exited with code $($Client.ExitCode) while waiting for '$Pattern'"
        }
        if ((Get-PatternCount $Path $Pattern) -gt 0) { return }
        Start-Sleep -Milliseconds 250
    }
    throw "timed out after $TimeoutSeconds seconds waiting for '$Pattern'"
}

function Wait-ForFileAppendPattern(
    [string]$Path,
    [long]$StartOffset,
    [string]$Pattern,
    [int]$TimeoutSeconds)
{
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $Path) {
            $stream = [System.IO.File]::Open(
                $Path,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                [System.IO.FileShare]::ReadWrite)
            try {
                if ($stream.Length -gt $StartOffset) {
                    $null = $stream.Seek($StartOffset, [System.IO.SeekOrigin]::Begin)
                    $reader = [System.IO.StreamReader]::new($stream)
                    try { $appended = $reader.ReadToEnd() }
                    finally { $reader.Dispose() }
                    if ([Text.RegularExpressions.Regex]::IsMatch(
                        $appended,
                        $Pattern,
                        [Text.RegularExpressions.RegexOptions]::CultureInvariant)) { return }
                }
            }
            finally { $stream.Dispose() }
        }
        Start-Sleep -Milliseconds 100
    }
    throw "timed out after $TimeoutSeconds seconds waiting for ACE log '$Pattern'"
}

function Close-ClientGracefully([Diagnostics.Process]$Client) {
    $Client.Refresh()
    if ($Client.HasExited) { return $true }
    if (-not $Client.CloseMainWindow()) { return $false }
    if (-not $Client.WaitForExit(45000)) { return $false }
    $Client.WaitForExit()
    return $true
}

function Test-Png([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    $info = Get-Item -LiteralPath $Path
    if ($info.Length -lt 1024) { return $false }
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 8) { return $false }
    $signature = @(137, 80, 78, 71, 13, 10, 26, 10)
    for ($i = 0; $i -lt $signature.Count; $i++) {
        if ($bytes[$i] -ne $signature[$i]) { return $false }
    }
    return $true
}

function Add-LogFailures([string]$Label, [string]$Stdout, [string]$Stderr) {
    $fatalPatterns = @(
        'event=invariant-failure',
        'Unhandled exception',
        'AccessViolation',
        'OutOfMemoryException',
        'WeenieError',
        'device removed',
        'GPU reset',
        'live: disconnected',
        '[shutdown]',
        'ObjectDisposedException',
        'screenshot-failed',
        'graceful logout confirmation timed out',
        'graceful logout failed',
        'transport disconnect failed'
    )
    foreach ($pattern in $fatalPatterns) {
        $count = (Get-PatternCount $Stdout $pattern) + (Get-PatternCount $Stderr $pattern)
        if ($count -gt 0) { $failures.Add("${Label}: '$pattern' appeared $count time(s)") }
    }

    $missingLandblocks = Get-PatternCount $Stdout 'LandblockLoader.Load returned null'
    if ($missingLandblocks -gt 0) {
        $warnings.Add("${Label}: $missingLandblocks expected world-edge landblock miss(es)")
    }
}

function Read-Checkpoints([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return @() }
    return @(Get-Content -LiteralPath $Path | ForEach-Object { $_ | ConvertFrom-Json })
}

function Validate-Checkpoint([string]$SessionLabel, [object]$Checkpoint) {
    $name = $Checkpoint.name
    $reveal = $Checkpoint.reveal
    $environmentOwnership = $Checkpoint.environmentOwnership
    $transitOwnership = $Checkpoint.transitOwnership
    $resources = $Checkpoint.resources
    if (-not $reveal.readiness.isReady) {
        $failures.Add("${SessionLabel}/${name}: reveal was not ready")
    }
    if (-not $reveal.worldViewportObserved) {
        $failures.Add("${SessionLabel}/${name}: normal world viewport was never observed")
    }
    if ($reveal.invariantFailureCount -ne 0) {
        $failures.Add("${SessionLabel}/${name}: reveal has $($reveal.invariantFailureCount) invariant failure(s)")
    }
    if (-not $reveal.readiness.isUnhydratable) {
        if (-not $reveal.readiness.isRenderNeighborhoodReady) {
            $failures.Add("${SessionLabel}/${name}: render neighborhood was not ready")
        }
        if (-not $reveal.readiness.areCompositeTexturesReady) {
            $failures.Add("${SessionLabel}/${name}: composite textures were not ready")
        }
        if (-not $reveal.readiness.isCollisionReady) {
            $failures.Add("${SessionLabel}/${name}: collision was not ready")
        }
    }
    if (-not $environmentOwnership.isInitialized) {
        $failures.Add("${SessionLabel}/${name}: Runtime world environment is not initialized")
    }
    if (($environmentOwnership.dayGroupDefinitionCount -le 0) -or
        ($environmentOwnership.activeDayGroupCount -ne 1)) {
        $failures.Add((
            "${SessionLabel}/${name}: Runtime environment ownership is {0}/{1}, expected definitions with one active group" -f
                $environmentOwnership.dayGroupDefinitionCount,
                $environmentOwnership.activeDayGroupCount))
    }
    foreach ($field in @(
        'bufferedTeleportDestinationCount',
        'pendingTeleportStartCount',
        'activeTeleportCount',
        'acceptedTeleportDestinationCount',
        'activeRevealCount',
        'pendingDestinationReadinessCount',
        'hostProjectionCount',
        'pendingHostAcknowledgementCount')) {
        if ([int]$transitOwnership.$field -ne 0) {
            $failures.Add((
                "${SessionLabel}/${name}: transitOwnership.$field={0}, expected zero at a stable checkpoint" -f
                    $transitOwnership.$field))
        }
    }
    if ($resources.pendingLiveTeardowns -ne 0) {
        $failures.Add("${SessionLabel}/${name}: $($resources.pendingLiveTeardowns) live teardown(s) pending")
    }
    if ($resources.pendingLandblockRetirements -ne 0) {
        $failures.Add("${SessionLabel}/${name}: $($resources.pendingLandblockRetirements) landblock retirement(s) pending")
    }
    if ($resources.stagedMeshUploads -ne 0) {
        $failures.Add("${SessionLabel}/${name}: $($resources.stagedMeshUploads) staged mesh upload(s) remain at stable checkpoint")
    }
    if ($resources.compositeWarmupPending -ne 0) {
        $failures.Add("${SessionLabel}/${name}: $($resources.compositeWarmupPending) composite warmup item(s) remain")
    }
    if ($resources.loadedLandblocks -le 0 -or $resources.worldEntities -le 0) {
        $failures.Add("${SessionLabel}/${name}: world ownership is empty at a visible checkpoint")
    }
    if ($null -eq $resources.lastFrameProfile) {
        $failures.Add("${SessionLabel}/${name}: no frame-profiler sample was available")
    }
    if ($CollisionShadowEvery -gt 0) {
        $shadow = $resources.PSObject.Properties['collisionShadow']
        if ($null -eq $shadow) {
            $failures.Add("${SessionLabel}/${name}: collision shadow counters are missing")
        }
        else {
            foreach ($field in @('mismatches', 'faults')) {
                $property = $shadow.Value.PSObject.Properties[$field]
                if ($null -eq $property) {
                    $failures.Add("${SessionLabel}/${name}: collisionShadow.$field is missing")
                }
                elseif ([long]$property.Value -ne 0) {
                    $failures.Add(
                        "${SessionLabel}/${name}: collisionShadow.$field=$($property.Value), expected zero")
                }
            }
        }
    }
}

function Invoke-Session(
    [string]$Label,
    [string]$RoutePath,
    [bool]$Uncapped,
    [string[]]$ExpectedCheckpoints,
    [string[]]$ExpectedScreenshots,
    [hashtable]$ScreenshotStateOverrides = @{})
{
    $sessionDir = Join-Path $root $Label
    $artifactDir = Join-Path $sessionDir 'artifacts'
    $null = New-Item -ItemType Directory -Force -Path $artifactDir
    $stdout = Join-Path $sessionDir 'stdout.log'
    $stderr = Join-Path $sessionDir 'stderr.log'
    $timeline = Join-Path $artifactDir 'world-lifecycle.checkpoints.jsonl'
    $collisionShadowDir = Join-Path $artifactDir 'collision-shadow'
    $client = $null
    $clientPort = $null
    $graceful = $false
    $exitCode = $null
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    $aceLogOffset = (Get-Item -LiteralPath $AceLogPath).Length

    $env:ACDREAM_DAT_DIR = "$env:USERPROFILE\Documents\Asheron's Call"
    $env:ACDREAM_LIVE = '1'
    $env:ACDREAM_TEST_HOST = '127.0.0.1'
    $env:ACDREAM_TEST_PORT = '9000'
    $env:ACDREAM_TEST_USER = $Account
    $env:ACDREAM_TEST_PASS = $Password
    $env:ACDREAM_RETAIL_UI = '1'
    $env:ACDREAM_FRAME_PROF = '1'
    $env:ACDREAM_UNCAPPED_RENDER = if ($Uncapped) { '1' } else { $null }
    $env:ACDREAM_DEVTOOLS = '0'
    $env:ACDREAM_UI_PROBE_DUMP = '0'
    $env:ACDREAM_UI_PROBE_SCRIPT = $RoutePath
    $env:ACDREAM_AUTOMATION_ARTIFACT_DIR = $artifactDir
    $env:ACDREAM_DUMP_MOVE_TRUTH = $null
    $env:ACDREAM_WB_DIAG = $null
    $env:ACDREAM_RENDER_BACKEND = $null
    $env:ACDREAM_NET_DROP_PCT = $null
    $env:ACDREAM_NET_DROP_SEED = $null
    $env:ACDREAM_NET_DROP_DIR = $null
    $env:ACDREAM_COLLISION_SHADOW_EVERY =
        if ($CollisionShadowEvery -gt 0) { "$CollisionShadowEvery" } else { $null }
    $env:ACDREAM_COLLISION_SHADOW_DIR =
        if ($CollisionShadowEvery -gt 0) { $collisionShadowDir } else { $null }

    try {
        $client = Start-Process -FilePath $exe -WorkingDirectory $Repository `
            -ArgumentList @('--session-config', ('"' + $sessionConfigPath + '"')) `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
        Wait-ForPattern $client $stdout '[UI-PROBE] UI probe script complete' $SessionTimeoutSeconds

        $clientPorts = @(Get-NetUDPEndpoint -OwningProcess $client.Id -ErrorAction SilentlyContinue |
            Select-Object -First 1 -ExpandProperty LocalPort)
        if ($clientPorts.Count -ne 1) {
            throw "could not resolve the client's UDP endpoint for ACE disconnect verification"
        }
        $clientPort = [int]$clientPorts[0]

        $client.Refresh()
        $processSample = [pscustomobject][ordered]@{
            ProcessId = $client.Id
            StartTimeUtc = $client.StartTime.ToUniversalTime().ToString('O')
            WorkingSetMiB = [Math]::Round($client.WorkingSet64 / 1MB, 1)
            PrivateMiB = [Math]::Round($client.PrivateMemorySize64 / 1MB, 1)
            HandleCount = $client.HandleCount
            ThreadCount = $client.Threads.Count
            WindowTitle = $client.MainWindowTitle
        }

        $checkpoints = @(Read-Checkpoints $timeline)
        if ($checkpoints.Count -ne $ExpectedCheckpoints.Count) {
            $failures.Add("${Label}: expected $($ExpectedCheckpoints.Count) checkpoints, found $($checkpoints.Count)")
        }
        foreach ($name in $ExpectedCheckpoints) {
            $matches = @($checkpoints | Where-Object { $_.name -eq $name })
            if ($matches.Count -ne 1) {
                $failures.Add("${Label}: expected one checkpoint '$name', found $($matches.Count)")
            }
        }
        foreach ($checkpoint in $checkpoints) { Validate-Checkpoint $Label $checkpoint }
        if ($CollisionShadowEvery -gt 0 -and $checkpoints.Count -gt 0) {
            $lastShadow = $checkpoints[-1].resources.PSObject.Properties['collisionShadow']
            $samples = if ($null -ne $lastShadow) {
                $lastShadow.Value.PSObject.Properties['samples']
            }
            if ($null -eq $samples -or [long]$samples.Value -le 0) {
                $failures.Add("${Label}: collision shadow did not sample any query")
            }
            $mismatchArtifacts = @(
                Get-ChildItem -LiteralPath $collisionShadowDir `
                    -Filter 'collision-shadow-*.json' `
                    -File `
                    -ErrorAction SilentlyContinue)
            if ($mismatchArtifacts.Count -ne 0) {
                $failures.Add(
                    "${Label}: collision shadow emitted $($mismatchArtifacts.Count) mismatch artifact(s)")
            }
        }

        foreach ($name in $ExpectedScreenshots) {
            $png = Join-Path $artifactDir "screenshots\$name.png"
            if (-not (Test-Png $png)) { $failures.Add("${Label}: missing or invalid screenshot '$png'") }
        }
        foreach ($name in $ExpectedScreenshots) {
            $expectedState = if ($ScreenshotStateOverrides.ContainsKey($name)) {
                $ScreenshotStateOverrides[$name]
            }
            else { $renderPackGate }
            Add-ConnectedRenderPackMetadataFailures `
                -ArtifactDirectory $artifactDir `
                -ScreenshotNames @($name) `
                -State $expectedState `
                -Failures $failures `
                -Label $Label
        }

        $graceful = Close-ClientGracefully $client
        $client.Refresh()
        if ($client.HasExited) { $exitCode = [int]$client.ExitCode }
        if (-not $graceful) { $failures.Add("${Label}: client did not close through WM_CLOSE") }
        if ($null -ne $exitCode -and $exitCode -ne 0) {
            $failures.Add("${Label}: client exited with code $exitCode")
        }
        Add-LogFailures $Label $stdout $stderr
        if ((Get-PatternCount $stdout '[session] graceful logout confirmed') -ne 1) {
            $failures.Add("${Label}: server did not authoritatively confirm graceful character logout")
        }
        Wait-ForFileAppendPattern `
            $AceLogPath `
            $aceLogOffset `
            "Session .*\\127\.0\.0\.1:$clientPort dropped\..*Reason: PacketHeader Disconnect" `
            15

        $session = [pscustomobject][ordered]@{
            Label = $Label
            Uncapped = $Uncapped
            ElapsedSeconds = [Math]::Round($elapsed.Elapsed.TotalSeconds, 3)
            GracefulExit = $graceful
            ExitCode = $exitCode
            Process = $processSample
            Checkpoints = @($checkpoints)
            Stdout = $stdout
            Stderr = $stderr
            ArtifactDirectory = $artifactDir
        }
        $sessions.Add($session)
        return $session
    }
    catch {
        $failures.Add("${Label}: $($_.Exception.Message)")
        return $null
    }
    finally {
        if ($null -ne $client) {
            $client.Refresh()
            if (-not $client.HasExited) {
                $graceful = Close-ClientGracefully $client
                if (-not $graceful -and -not $client.HasExited) {
                    $failures.Add("${Label}: required forced termination after WM_CLOSE timeout")
                    Stop-Process -Id $client.Id -Force
                    $client.WaitForExit(10000)
                }
            }
            $client.Dispose()
        }
    }
}

function Add-AtmosphericTransitionSemanticGates([object]$Session) {
    if ($null -eq $Session) { return }
    $screenshots = Join-Path $Session.ArtifactDirectory 'screenshots'
    $rows = @{}
    foreach ($name in @(
        'transition_selected_high',
        'transition_disabled_retail',
        'transition_reenabled_high',
        'transition_resized_high',
        'transition_dusk_high',
        'transition_overcast_high',
        'transition_rain_high')) {
        $path = Join-Path $screenshots "$name.metadata.json"
        if (Test-Path -LiteralPath $path) {
            $rows[$name] = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
        }
    }
    if ($rows.Count -ne 7) { return }

    $selected = $rows.transition_selected_high.RenderPack
    $disabled = $rows.transition_disabled_retail.RenderPack
    $reenabled = $rows.transition_reenabled_high.RenderPack
    if ([long]$disabled.ActivationGeneration -le [long]$selected.ActivationGeneration -or
        [long]$reenabled.ActivationGeneration -le [long]$disabled.ActivationGeneration) {
        $failures.Add('atmospheric-transitions: select/disable/re-enable activation generations were not strictly monotonic')
    }
    if ([int]$selected.ShadowCasterCount -le 0) {
        $failures.Add('atmospheric-transitions: dense outdoor High row published no directional-shadow casters')
    }
    if ([int]$selected.ShadowTransformChurn.LiveDynamicRootChanges -le 0) {
        $failures.Add('atmospheric-transitions: moving High row published no live-dynamic caster transform')
    }
    if ([int]$selected.ShadowTransformChurn.EquippedChildChanges -le 0) {
        $failures.Add('atmospheric-transitions: moving High row published no equipped-child caster transform')
    }
    $casterClasses = $selected.ShadowTransformChurn.CasterClasses
    if ($null -eq $casterClasses) {
        $failures.Add('atmospheric-transitions: High row published no authoritative caster-class diagnostics')
    }
    else {
        foreach ($property in @(
            'TerrainCommands',
            'OutdoorStatics',
            'Buildings',
            'AnimatedStatics',
            'LocalPlayers',
            'RemotePlayers',
            'NonPlayerCreatures',
            'OtherLiveDynamics',
            'EquippedChildren')) {
            if ($property -notin @($casterClasses.PSObject.Properties.Name)) {
                $failures.Add(
                    "atmospheric-transitions: caster diagnostics omitted required '$property' metadata")
            }
            elseif ([int]$casterClasses.$property -lt 0) {
                $failures.Add(
                    "atmospheric-transitions: caster diagnostics published negative '$property' metadata")
            }
        }
        foreach ($required in @(
            @('TerrainCommands', 'terrain command'),
            @('OutdoorStatics', 'outdoor-static scenery'),
            @('Buildings', 'building'),
            @('LocalPlayers', 'local-player'),
            @('EquippedChildren', 'equipped-child'))) {
            $property = [string]$required[0]
            if ([int]$casterClasses.$property -le 0) {
                $failures.Add(
                    "atmospheric-transitions: dense outdoor High row published no $($required[1]) caster evidence")
            }
        }
    }

    $resized = $rows.transition_resized_high
    if ([int]$resized.Width -ne 1024 -or [int]$resized.Height -ne 768) {
        $failures.Add(
            "atmospheric-transitions: resized screenshot was $($resized.Width)x$($resized.Height), expected exact 1024x768 framebuffer")
    }
    $dusk = $rows.transition_dusk_high.RenderPack
    if ([Math]::Abs(
            [double]$dusk.SunElevationDegrees -
            [double]$reenabled.SunElevationDegrees) -lt 0.01) {
        $failures.Add('atmospheric-transitions: authored time change did not alter published sun elevation')
    }
    if ([string]$rows.transition_overcast_high.RenderPack.Weather -cnotmatch '(?i)^overcast$') {
        $failures.Add('atmospheric-transitions: first weather edge did not publish Overcast')
    }
    if ([string]$rows.transition_rain_high.RenderPack.Weather -cnotmatch '(?i)^rain$') {
        $failures.Add('atmospheric-transitions: second weather edge did not publish Rain')
    }
}

function Get-FreshContextRecreationGate(
    [object]$FirstSession,
    [object]$SecondSession)
{
    $definition = 'graceful full graphical-process teardown followed by a fresh process; the fresh process constructs a new Vulkan device/context/swapchain ownership graph'
    if ($null -eq $FirstSession -or $null -eq $SecondSession) {
        $failures.Add('new-context recreation could not be proven because a required session is missing')
        return [pscustomobject][ordered]@{
            Definition = $definition
            Passed = $false
            FirstProcess = $null
            SecondProcess = $null
        }
    }
    $firstIdentity = "$($FirstSession.Process.ProcessId)@$($FirstSession.Process.StartTimeUtc)"
    $secondIdentity = "$($SecondSession.Process.ProcessId)@$($SecondSession.Process.StartTimeUtc)"
    $passed = $FirstSession.GracefulExit -and
        $SecondSession.GracefulExit -and
        $firstIdentity -cne $secondIdentity
    if (-not $passed) {
        $failures.Add('new-context recreation did not prove graceful teardown and a distinct fresh process')
    }
    return [pscustomobject][ordered]@{
        Definition = $definition
        Passed = $passed
        FirstProcess = $firstIdentity
        SecondProcess = $secondIdentity
    }
}

function Add-SameLocationGates([object]$CappedSession) {
    if ($null -eq $CappedSession) { return }
    $first = @($CappedSession.Checkpoints | Where-Object { $_.name -eq 'aerlinthe_first' }) | Select-Object -First 1
    $revisit = @($CappedSession.Checkpoints | Where-Object { $_.name -eq 'aerlinthe_revisit' }) | Select-Object -First 1
    if ($null -eq $first -or $null -eq $revisit) { return }

    $managedLimit = [Math]::Max(256MB, [double]$first.resources.managedBytes * 0.40)
    $gpuLimit = [Math]::Max(512MB, [double]$first.resources.trackedGpuBytes * 0.50)
    if (($revisit.resources.managedBytes - $first.resources.managedBytes) -gt $managedLimit) {
        $failures.Add("Aerlinthe revisit: managed memory grew beyond the connected gate tolerance")
    }
    if (($revisit.resources.trackedGpuBytes - $first.resources.trackedGpuBytes) -gt $gpuLimit) {
        $failures.Add("Aerlinthe revisit: tracked GPU memory grew beyond the connected gate tolerance")
    }

    foreach ($property in @('particleOwners', 'effectOwners', 'lightOwners', 'scriptOwners', 'compositeTextureOwners', 'particleTextureOwners')) {
        $before = [double]$first.resources.$property
        $after = [double]$revisit.resources.$property
        $limit = [Math]::Max(64.0, $before * 0.50)
        if (($after - $before) -gt $limit) {
            $failures.Add("Aerlinthe revisit: owner '$property' grew $before -> $after")
        }
    }
}

$renderPackGate = New-ConnectedRenderPackGateState `
    -Root $root `
    -Preset $RenderPackPreset `
    -SettingOverrides $RenderPackSettingOverrides
if (-not [string]::IsNullOrWhiteSpace($PreparedAssetPath)) {
    $resolvedPreparedAssetPath = [IO.Path]::GetFullPath($PreparedAssetPath)
    if (-not (Test-Path -LiteralPath $resolvedPreparedAssetPath -PathType Leaf)) {
        throw "prepared asset package not found: $resolvedPreparedAssetPath"
    }
    $env:ACDREAM_PAK_PATH = $resolvedPreparedAssetPath
}
$sessionConfigPath = New-ConnectedGraphicalSessionConfig `
    -State $renderPackGate `
    -Account $Account
try {
    if (@(Get-Process -Name AcDream.App -ErrorAction SilentlyContinue).Count -gt 0) {
        throw 'an AcDream.App client is already running; close it gracefully before the gate'
    }
    if (@(Get-NetUDPEndpoint -LocalPort 9000 -ErrorAction SilentlyContinue).Count -eq 0) {
        throw 'local ACE is not listening on UDP port 9000'
    }
    if (-not (Test-Path -LiteralPath $AceLogPath)) {
        throw "ACE log was not found: $AceLogPath"
    }

    if (-not $SkipBuild) {
        & dotnet build (Join-Path $Repository 'AcDream.slnx') -c Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE" }
    }
    if (-not (Test-Path -LiteralPath $exe)) { throw "client executable not found: $exe" }

    $binaryIdentity = Get-ConnectedGateBinaryIdentity `
        -Repository $Repository -Executable $exe -SkipBuild:$SkipBuild

    $capped = Invoke-Session `
        'capped' `
        (Join-Path $Repository 'tools\connected-world-lifecycle.route.txt') `
        $false `
        @('capped_login', 'aerlinthe_first', 'rynthid', 'facility_hub', 'holtburg_after_dungeon', 'aerlinthe_revisit') `
        @('capped_login', 'aerlinthe_first', 'facility_hub', 'holtburg_after_dungeon', 'aerlinthe_revisit')

    Add-SameLocationGates $capped

    $uncapped = Invoke-Session `
        'uncapped-reconnect' `
        (Join-Path $Repository 'tools\connected-world-reconnect.route.txt') `
        $true `
        @('uncapped_reconnect') `
        @('uncapped_reconnect')

    $contextRecreation = Get-FreshContextRecreationGate $capped $uncapped

    $transitionSession = $null
    if ($RenderPackPreset -eq 'medium') {
        $highExpectation = Get-ConnectedRenderPackExpectation -Preset high
        $retailExpectation = Get-ConnectedRenderPackExpectation -Preset retail
        $transitionSession = Invoke-Session `
            'atmospheric-transitions' `
            (Join-Path $Repository 'tools\connected-render-pack-transitions.route.txt') `
            $false `
            @('atmospheric_transitions') `
            @(
                'transition_selected_high',
                'transition_disabled_retail',
                'transition_reenabled_high',
                'transition_resized_high',
                'transition_dusk_high',
                'transition_overcast_high',
                'transition_rain_high') `
            @{
                transition_selected_high = $highExpectation
                transition_disabled_retail = $retailExpectation
                transition_reenabled_high = $highExpectation
                transition_resized_high = $highExpectation
                transition_dusk_high = $highExpectation
                transition_overcast_high = $highExpectation
                transition_rain_high = $highExpectation
            }
        Add-AtmosphericTransitionSemanticGates $transitionSession
    }

    $report = [pscustomobject][ordered]@{
        Passed = $failures.Count -eq 0
        StartedUtc = $startedUtc.ToString('O')
        FinishedUtc = [DateTime]::UtcNow.ToString('O')
        Commit = $binaryIdentity.BinaryCommit
        SourceCommit = $binaryIdentity.SourceCommit
        BinaryProductVersion = $binaryIdentity.BinaryProductVersion
        BinaryCommit = $binaryIdentity.BinaryCommit
        BinaryMatchesSource = $binaryIdentity.BinaryMatchesSource
        SkipBuild = $binaryIdentity.SkipBuild
        SourceStatus = @(& git -C $Repository status --short)
        SessionName = $env:SESSIONNAME
        CollisionShadowEvery = $CollisionShadowEvery
        RenderPackSelection = (Get-ConnectedRenderPackGateReport $renderPackGate)
        ContextRecreation = $contextRecreation
        TransitionAutomation = [pscustomobject][ordered]@{
            Executed = $null -ne $transitionSession
            CanonicalRow = 'medium'
            Session = $transitionSession
            ProvenCasterRoutes = @(
                'terrain shadow-command publication',
                'outdoor-static scenery publication (including trees, without a tree discriminator)',
                'building caster publication',
                'local-player caster publication',
                'moving live-dynamic root transform publication',
                'equipped-child caster and moving-transform publication')
            RemainingCasterClassEvidence = @(
                'a second live client is still required to prove a nonzero remote-player caster count',
                'a deterministic populated connected row is still required to prove nonzero active animated-static and non-player creature counts',
                'create-object render metadata proves non-player creature, not hostile monster versus non-hostile NPC',
                'outdoor DAT scenery has no authoritative tree discriminator, so trees remain grouped with other outdoor statics')
        }
        VideoControllers = @(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue |
            ForEach-Object { [pscustomobject]@{
                Name = $_.Name
                DriverVersion = $_.DriverVersion
                AdapterRam = $_.AdapterRAM
            } })
        Failures = @($failures)
        Warnings = @($warnings)
        Sessions = @($sessions)
    }
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $reportPath -Encoding utf8

    Write-Output "REPORT=$reportPath"
    Write-Output "RESULT=$(if ($report.Passed) { 'PASS' } else { 'FAIL' })"
    foreach ($failure in $failures) { Write-Output "FAILURE=$failure" }
    foreach ($warning in $warnings) { Write-Output "WARNING=$warning" }
}
finally {
    try {
        Remove-ConnectedGraphicalSessionConfig `
            -State $renderPackGate -Path $sessionConfigPath
    }
    finally {
        Restore-ConnectedRenderPackGateEnvironment $renderPackGate
    }
}

if ($failures.Count -gt 0) { exit 1 }
