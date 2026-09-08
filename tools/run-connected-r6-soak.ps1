[CmdletBinding()]
param(
    [string]$Repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$Account = $env:ACDREAM_TEST_USER,
    [string]$Password = $env:ACDREAM_TEST_PASS,
    [string]$PreparedAssetPath,
    [switch]$SkipBuild,
    [switch]$Uncapped,
    [switch]$DenseTown,
    [switch]$CaptureContention,
    [switch]$SkipRuntimeCounters,
    [int]$LoginTimeoutSeconds = 90,
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

if ($DenseTown) {
    [object[]]$destinations = @(
        [pscustomobject]@{
            Name = 'Arwic dense'
            Checkpoint = 'arwic-dense'
            Occurrence = 1
            Exercise = $false
        }
    )
    [string[]]$expectedCheckpointNames = @('arwic-dense')
    $routeFileName = 'connected-dense-town.route.txt'
    $runName = 'connected-dense-town'
}
else {
    [object[]]$destinations = @(
        [pscustomobject]@{ Name = 'Caul'; Checkpoint = 'caul-baseline'; Occurrence = 1; Exercise = $true },
        [pscustomobject]@{ Name = 'Sawato'; Checkpoint = 'sawato-baseline'; Occurrence = 2; Exercise = $false },
        [pscustomobject]@{ Name = 'Rynthid'; Checkpoint = 'rynthid'; Occurrence = 3; Exercise = $false },
        [pscustomobject]@{ Name = 'Aerlinthe'; Checkpoint = 'aerlinthe'; Occurrence = 4; Exercise = $false },
        [pscustomobject]@{ Name = 'Sawato revisit'; Checkpoint = 'sawato-return'; Occurrence = 5; Exercise = $false },
        [pscustomobject]@{ Name = 'Holtburg'; Checkpoint = 'holtburg'; Occurrence = 6; Exercise = $true },
        [pscustomobject]@{ Name = 'Caul return'; Checkpoint = 'caul-return'; Occurrence = 7; Exercise = $true },
        [pscustomobject]@{ Name = 'Sawato plateau'; Checkpoint = 'sawato-plateau'; Occurrence = 8; Exercise = $false },
        [pscustomobject]@{ Name = 'Caul plateau'; Checkpoint = 'caul-plateau'; Occurrence = 9; Exercise = $false }
    )
    [string[]]$expectedCheckpointNames = @(
        'caul-baseline',
        'sawato-baseline',
        'rynthid',
        'aerlinthe',
        'sawato-return',
        'holtburg',
        'caul-return',
        'sawato-plateau',
        'caul-plateau'
    )
    $routeFileName = 'connected-r6-soak.route.txt'
    $runName = 'connected-r6-soak'
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$logDir = Join-Path $Repository 'logs'
$null = New-Item -ItemType Directory -Force -Path $logDir
$prefix = Join-Path $logDir "$runName-$stamp"
$stdoutLog = "$prefix.out.log"
$stderrLog = "$prefix.err.log"
$markerLog = "$prefix.markers.log"
$sampleCsv = "$prefix.samples.csv"
$reportJson = "$prefix.report.json"
$artifactDir = "$prefix.artifacts"
$null = New-Item -ItemType Directory -Force -Path $artifactDir
$collisionShadowDir = Join-Path $artifactDir 'collision-shadow'
$checkpointTimeline = Join-Path $artifactDir 'world-lifecycle.checkpoints.jsonl'
$frameHistory = Join-Path $artifactDir 'frame-history.csv'
$frameSummary = Join-Path $artifactDir 'frame-history-summary.json'
$contentionTrace = Join-Path $artifactDir 'contention.nettrace'
$contentionStdout = Join-Path $artifactDir 'contention.out.log'
$contentionStderr = Join-Path $artifactDir 'contention.err.log'
$runtimeCounters = Join-Path $artifactDir 'runtime-counters.csv'
$counterStdout = Join-Path $artifactDir 'runtime-counters.out.log'
$counterStderr = Join-Path $artifactDir 'runtime-counters.err.log'
$routePath = Join-Path $Repository "tools\$routeFileName"
$exe = Join-Path $Repository 'src\AcDream.App\bin\Release\net10.0\AcDream.App.exe'
$cliDll = Join-Path $Repository 'src\AcDream.Cli\bin\Release\net10.0\AcDream.Cli.dll'

$samples = [System.Collections.Generic.List[object]]::new()
$failures = [System.Collections.Generic.List[string]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()
$process = $null
$traceProcess = $null
$counterProcess = $null
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
$gracefulExit = $false
$exitCode = $null
$canonicalCheckpoints = @()

function Read-LogLines([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return @() }
    return @(Get-Content -LiteralPath $Path -ErrorAction SilentlyContinue)
}

function Write-Marker([string]$Text) {
    $line = "{0:O} {1}" -f [DateTime]::UtcNow, $Text
    Add-Content -LiteralPath $markerLog -Value $line
    Write-Output $line
}

function Get-PatternCount([string]$Path, [string]$Pattern) {
    return @(Read-LogLines $Path | Select-String -SimpleMatch $Pattern).Count
}

function Get-FrameProfiles([string]$Path) {
    return @(Read-LogLines $Path | Where-Object { $_.StartsWith('[frame-prof] n=', [StringComparison]::Ordinal) })
}

function Wait-ForLogPattern(
    [Diagnostics.Process]$Client,
    [string]$Path,
    [string]$Pattern,
    [int]$MinimumCount,
    [int]$TimeoutSeconds)
{
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $Client.Refresh()
        if ($Client.HasExited) {
            throw "client exited with code $($Client.ExitCode) while waiting for '$Pattern'"
        }

        $count = Get-PatternCount $Path $Pattern
        if ($count -ge $MinimumCount) { return $count }
        Start-Sleep -Milliseconds 250
    }

    throw "timed out after $TimeoutSeconds seconds waiting for occurrence $MinimumCount of '$Pattern'"
}

function Wait-ForNextFrameProfile(
    [Diagnostics.Process]$Client,
    [string]$Path,
    [int]$AfterCount,
    [int]$TimeoutSeconds)
{
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $Client.Refresh()
        if ($Client.HasExited) {
            throw "client exited with code $($Client.ExitCode) while waiting for a frame-prof boundary"
        }

        $profiles = Get-FrameProfiles $Path
        if ($profiles.Count -gt $AfterCount) {
            return [pscustomobject]@{ Count = $profiles.Count; Line = $profiles[-1] }
        }
        Start-Sleep -Milliseconds 100
    }

    throw "timed out after $TimeoutSeconds seconds waiting for a frame-prof boundary"
}

function Parse-FrameProfile([string]$Line) {
    $values = [ordered]@{
        FrameCount = $null
        CpuP50Ms = $null
        CpuP95Ms = $null
        CpuP99Ms = $null
        CpuMaxMs = $null
        GpuP50Ms = $null
        GpuP95Ms = $null
        AllocP50Kb = $null
        AllocMaxKb = $null
        UpdateP50Ms = $null
        UpdateP95Ms = $null
    }

    if ($Line -match '^\[frame-prof\] n=(?<n>\d+)') {
        $values.FrameCount = [int]$Matches.n
    }
    if ($Line -match 'cpu_ms p50=(?<p50>[\d.]+) p95=(?<p95>[\d.]+) p99=(?<p99>[\d.]+) max=(?<max>[\d.]+)') {
        $values.CpuP50Ms = [double]$Matches.p50
        $values.CpuP95Ms = [double]$Matches.p95
        $values.CpuP99Ms = [double]$Matches.p99
        $values.CpuMaxMs = [double]$Matches.max
    }
    if ($Line -match 'gpu_ms p50=(?<p50>[\d.]+) p95=(?<p95>[\d.]+)') {
        $values.GpuP50Ms = [double]$Matches.p50
        $values.GpuP95Ms = [double]$Matches.p95
    }
    if ($Line -match 'alloc_kb p50=(?<p50>[\d.]+) max=(?<max>[\d.]+)') {
        $values.AllocP50Kb = [double]$Matches.p50
        $values.AllocMaxKb = [double]$Matches.max
    }
    if ($Line -match 'upd p50=(?<p50>[\d.]+) p95=(?<p95>[\d.]+)') {
        $values.UpdateP50Ms = [double]$Matches.p50
        $values.UpdateP95Ms = [double]$Matches.p95
    }

    return [pscustomobject]$values
}

function Parse-WindowTitle([string]$Title) {
    $values = [ordered]@{
        Fps = $null
        FrameMs = $null
        LoadedLandblocks = $null
        TotalLandblocks = $null
        EntityCount = $null
        AnimationCount = $null
    }

    if ($Title -match 'acdream \| (?<fps>\d+) fps \| (?<ms>[\d.]+) ms \| lb (?<loaded>\d+)/(?<total>\d+) \| ent (?<ent>\d+)/anim (?<anim>\d+)') {
        $values.Fps = [int]$Matches.fps
        $values.FrameMs = [double]$Matches.ms
        $values.LoadedLandblocks = [int]$Matches.loaded
        $values.TotalLandblocks = [int]$Matches.total
        $values.EntityCount = [int]$Matches.ent
        $values.AnimationCount = [int]$Matches.anim
    }

    return [pscustomobject]$values
}

function Capture-Sample(
    [Diagnostics.Process]$Client,
    [string]$Destination,
    [string]$Phase,
    [string]$ProfileLine)
{
    $Client.Refresh()
    $profile = Parse-FrameProfile $ProfileLine
    $title = Parse-WindowTitle $Client.MainWindowTitle
    $sample = [pscustomobject][ordered]@{
        Destination = $Destination
        Phase = $Phase
        ElapsedSeconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 3)
        WorkingSetMiB = [Math]::Round($Client.WorkingSet64 / 1MB, 1)
        PrivateMiB = [Math]::Round($Client.PrivateMemorySize64 / 1MB, 1)
        HandleCount = $Client.HandleCount
        ThreadCount = $Client.Threads.Count
        Fps = $title.Fps
        TitleFrameMs = $title.FrameMs
        LoadedLandblocks = $title.LoadedLandblocks
        TotalLandblocks = $title.TotalLandblocks
        EntityCount = $title.EntityCount
        AnimationCount = $title.AnimationCount
        ProfileFrames = $profile.FrameCount
        CpuP50Ms = $profile.CpuP50Ms
        CpuP95Ms = $profile.CpuP95Ms
        CpuP99Ms = $profile.CpuP99Ms
        CpuMaxMs = $profile.CpuMaxMs
        GpuP50Ms = $profile.GpuP50Ms
        GpuP95Ms = $profile.GpuP95Ms
        AllocP50Kb = $profile.AllocP50Kb
        AllocMaxKb = $profile.AllocMaxKb
        UpdateP50Ms = $profile.UpdateP50Ms
        UpdateP95Ms = $profile.UpdateP95Ms
        Profile = $ProfileLine
    }
    $samples.Add($sample)
    if ($null -eq $sample.ProfileFrames -or
        $null -eq $sample.CpuP95Ms -or
        $null -eq $sample.AllocP50Kb -or
        $null -eq $sample.UpdateP95Ms) {
        $failures.Add("${Destination}/${Phase}: frame-prof sample was missing required parsed metrics")
    }
    return $sample
}

function Observe-R6Exercise(
    [Diagnostics.Process]$Client,
    [string]$Destination,
    [pscustomobject]$Before)
{
    $null = Wait-ForLogPattern $Client $stdoutLog '[input] MovementForward Press' ($Before.ForwardPress + 5) 30
    $null = Wait-ForLogPattern $Client $stdoutLog '[input] MovementForward Release' ($Before.ForwardRelease + 5) 10
    $null = Wait-ForLogPattern $Client $stdoutLog '[input] MovementJump Press' ($Before.JumpPress + 1) 10
    $null = Wait-ForLogPattern $Client $stdoutLog '[input] MovementJump Release' ($Before.JumpRelease + 1) 10
    $null = Wait-ForLogPattern $Client $stdoutLog '[input] CombatToggleCombat Press' ($Before.Combat + 2) 15
    $null = Wait-ForLogPattern $Client $stdoutLog 'move-truth OUT' ($Before.MoveTruth + 2) 10

    $forwardPressDelta = (Get-PatternCount $stdoutLog '[input] MovementForward Press') - $Before.ForwardPress
    $forwardReleaseDelta = (Get-PatternCount $stdoutLog '[input] MovementForward Release') - $Before.ForwardRelease
    $jumpPressDelta = (Get-PatternCount $stdoutLog '[input] MovementJump Press') - $Before.JumpPress
    $jumpReleaseDelta = (Get-PatternCount $stdoutLog '[input] MovementJump Release') - $Before.JumpRelease
    $combatDelta = (Get-PatternCount $stdoutLog '[input] CombatToggleCombat Press') - $Before.Combat
    $moveTruthDelta = (Get-PatternCount $stdoutLog 'move-truth OUT') - $Before.MoveTruth

    if ($forwardPressDelta -lt 5 -or $forwardReleaseDelta -lt 5) {
        $failures.Add("${Destination}: production forward input did not deliver five press/release pairs ($forwardPressDelta/$forwardReleaseDelta)")
    }
    if ($jumpPressDelta -lt 1 -or $jumpReleaseDelta -lt 1) {
        $failures.Add("${Destination}: production jump input did not deliver a press/release pair ($jumpPressDelta/$jumpReleaseDelta)")
    }
    if ($combatDelta -lt 2) {
        $failures.Add("${Destination}: combat toggle did not dispatch twice ($combatDelta)")
    }
    if ($moveTruthDelta -lt 2) {
        $failures.Add("${Destination}: movement/jump exercise produced too few outbound movement records ($moveTruthDelta)")
    }

    Write-Marker "exercise destination='$Destination' forward=$forwardPressDelta/$forwardReleaseDelta jump=$jumpPressDelta/$jumpReleaseDelta combat=$combatDelta moveTruth=$moveTruthDelta"
}

function Read-CanonicalCheckpoints {
    if (-not (Test-Path -LiteralPath $checkpointTimeline)) { return @() }

    $records = [System.Collections.Generic.List[object]]::new()
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $checkpointTimeline) {
        $lineNumber++
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try {
            $records.Add(($line | ConvertFrom-Json))
        }
        catch {
            throw "checkpoint timeline line $lineNumber is invalid JSON: $($_.Exception.Message)"
        }
    }
    return @($records)
}

function Add-CanonicalCheckpointFailures([Diagnostics.Process]$Client) {
    if ($canonicalCheckpoints.Count -ne $expectedCheckpointNames.Count) {
        $failures.Add("expected $($expectedCheckpointNames.Count) canonical checkpoints, found $($canonicalCheckpoints.Count)")
    }

    $count = [Math]::Min($canonicalCheckpoints.Count, $expectedCheckpointNames.Count)
    for ($index = 0; $index -lt $count; $index++) {
        $checkpoint = $canonicalCheckpoints[$index]
        $expectedName = $expectedCheckpointNames[$index]
        $expectedSequence = $index + 1
        if ($checkpoint.name -ne $expectedName) {
            $failures.Add("canonical checkpoint $expectedSequence was '$($checkpoint.name)', expected '$expectedName'")
        }
        if ([int]$checkpoint.sequence -ne $expectedSequence) {
            $failures.Add("canonical checkpoint '$expectedName' had sequence $($checkpoint.sequence), expected $expectedSequence")
        }
        if ([int]$checkpoint.processId -ne $Client.Id) {
            $failures.Add("canonical checkpoint '$expectedName' came from process $($checkpoint.processId), expected $($Client.Id)")
        }

        if ($null -eq $checkpoint.reveal) {
            $failures.Add("canonical checkpoint '$expectedName' is missing reveal data")
        }
        else {
            foreach ($field in @(
                'materialized',
                'completed',
                'worldViewportObserved')) {
                $property = $checkpoint.reveal.PSObject.Properties[$field]
                if ($null -eq $property -or -not [bool]$property.Value) {
                    $failures.Add("canonical checkpoint '$expectedName' has reveal.$field=false or missing")
                }
            }

            $cancelled =
                $checkpoint.reveal.PSObject.Properties['cancelled']
            if ($null -eq $cancelled -or [bool]$cancelled.Value) {
                $failures.Add("canonical checkpoint '$expectedName' has reveal.cancelled=true or missing")
            }

            $invariants =
                $checkpoint.reveal.PSObject.Properties['invariantFailureCount']
            if ($null -eq $invariants -or [long]$invariants.Value -ne 0) {
                $failures.Add("canonical checkpoint '$expectedName' has reveal invariant failure(s)")
            }

            $readiness =
                $checkpoint.reveal.PSObject.Properties['readiness']
            $isReady = if ($null -ne $readiness) {
                $readiness.Value.PSObject.Properties['isReady']
            }
            if ($null -eq $isReady -or -not [bool]$isReady.Value) {
                $failures.Add("canonical checkpoint '$expectedName' has incomplete reveal readiness")
            }
        }

        if ($null -eq $checkpoint.render -or $null -eq $checkpoint.resources) {
            $failures.Add("canonical checkpoint '$expectedName' is missing render/resources data")
            continue
        }

        $renderWorldProperty = $checkpoint.render.PSObject.Properties['world']
        $renderVisibleProperty = if ($null -ne $renderWorldProperty) {
            $renderWorldProperty.Value.PSObject.Properties['visibleLandblocks']
        }
        $renderTotalProperty = if ($null -ne $renderWorldProperty) {
            $renderWorldProperty.Value.PSObject.Properties['totalLandblocks']
        }
        $resourceVisibleProperty =
            $checkpoint.resources.PSObject.Properties['visibleLandblocks']
        $resourceTotalProperty =
            $checkpoint.resources.PSObject.Properties['totalLandblocks']
        if ($null -eq $renderWorldProperty -or
            $null -eq $renderVisibleProperty -or
            $null -eq $renderTotalProperty -or
            $null -eq $resourceVisibleProperty -or
            $null -eq $resourceTotalProperty) {
            $failures.Add("canonical checkpoint '$expectedName' is missing render-world synchronization fields")
        }
        elseif ([int]$renderVisibleProperty.Value -ne
            [int]$resourceVisibleProperty.Value) {
            $failures.Add("canonical checkpoint '$expectedName' mixed render/resource visible-landblock frames")
        }
        elseif ([int]$renderTotalProperty.Value -ne
            [int]$resourceTotalProperty.Value) {
            $failures.Add("canonical checkpoint '$expectedName' mixed render/resource total-landblock frames")
        }

        $namedCheckpointPath =
            Join-Path $artifactDir "checkpoint-$expectedName.json"
        if (-not (Test-Path -LiteralPath $namedCheckpointPath)) {
            $failures.Add("canonical checkpoint '$expectedName' is missing named artifact '$namedCheckpointPath'")
        }
        else {
            try {
                $namedCheckpoint =
                    Get-Content -LiteralPath $namedCheckpointPath -Raw |
                    ConvertFrom-Json
                if ($namedCheckpoint.name -ne $checkpoint.name -or
                    [int]$namedCheckpoint.sequence -ne [int]$checkpoint.sequence -or
                    [int]$namedCheckpoint.processId -ne [int]$checkpoint.processId) {
                    $failures.Add("canonical checkpoint '$expectedName' named artifact does not match its timeline row")
                }
            }
            catch {
                $failures.Add("canonical checkpoint '$expectedName' named artifact is invalid JSON: $($_.Exception.Message)")
            }
        }

        foreach ($field in @(
            'pendingLiveTeardowns',
            'pendingLandblockRetirements',
            'stagedMeshUploads',
            'stagedMeshBytes',
            'compositeWarmupPending')) {
            $property = $checkpoint.resources.PSObject.Properties[$field]
            if ($null -eq $property) {
                $failures.Add("canonical checkpoint '$expectedName' is missing resources.$field")
            }
            elseif ([long]$property.Value -ne 0) {
                $failures.Add("canonical checkpoint '$expectedName' has resources.$field=$($property.Value), expected zero")
            }
        }

        if ($CollisionShadowEvery -gt 0) {
            $shadow = $checkpoint.resources.PSObject.Properties['collisionShadow']
            if ($null -eq $shadow) {
                $failures.Add("canonical checkpoint '$expectedName' is missing resources.collisionShadow")
            }
            else {
                foreach ($field in @('mismatches', 'faults')) {
                    $property = $shadow.Value.PSObject.Properties[$field]
                    if ($null -eq $property) {
                        $failures.Add(
                            "canonical checkpoint '$expectedName' is missing resources.collisionShadow.$field")
                    }
                    elseif ([long]$property.Value -ne 0) {
                        $failures.Add(
                            "canonical checkpoint '$expectedName' has resources.collisionShadow.$field=$($property.Value), expected zero")
                    }
                }
            }
        }

        $streamingWork =
            $checkpoint.resources.PSObject.Properties['streamingWork']
        if ($null -eq $streamingWork) {
            $failures.Add("canonical checkpoint '$expectedName' is missing resources.streamingWork")
        }
        else {
            foreach ($field in @(
                'lifetimeFrameOverrunCount',
                'lifetimeOversizedProgressCount',
                'maximumFrameMilliseconds',
                'maximumFrameStage',
                'maximumOperationMilliseconds',
                'maximumOperationStage')) {
                if ($null -eq $streamingWork.Value.PSObject.Properties[$field]) {
                    $failures.Add("canonical checkpoint '$expectedName' is missing resources.streamingWork.$field")
                }
            }

            foreach ($field in @(
                'deferredCompletions',
                'deferredAdoptedCpuBytes',
                'pendingPublications',
                'pendingRetirements',
                'workerCompletionBacklog',
                'destinationBacklog',
                'controlBacklog',
                'unloadBacklog',
                'nearBacklog',
                'farBacklog')) {
                $property = $streamingWork.Value.PSObject.Properties[$field]
                if ($null -eq $property) {
                    $failures.Add("canonical checkpoint '$expectedName' is missing resources.streamingWork.$field")
                }
                elseif ([long]$property.Value -ne 0) {
                    $failures.Add("canonical checkpoint '$expectedName' has resources.streamingWork.$field=$($property.Value), expected zero")
                }
            }
        }
    }

    if ($DenseTown) {
        return
    }

    $caulReturn = $canonicalCheckpoints |
        Where-Object { $_.name -eq 'caul-return' } |
        Select-Object -First 1
    $caulPlateau = $canonicalCheckpoints |
        Where-Object { $_.name -eq 'caul-plateau' } |
        Select-Object -First 1
    if ($null -eq $caulReturn -or $null -eq $caulPlateau) {
        $failures.Add('canonical Caul return/plateau comparison is unavailable')
        return
    }

    foreach ($field in @(
        'loadedLandblocks',
        'totalLandblocks')) {
        $first = $caulReturn.resources.PSObject.Properties[$field]
        $plateau = $caulPlateau.resources.PSObject.Properties[$field]
        if ($null -eq $first -or $null -eq $plateau) {
            $failures.Add("canonical Caul comparison is missing resources.$field")
        }
        elseif ([long]$first.Value -ne [long]$plateau.Value) {
            $failures.Add("Caul plateau deterministic world domain '$field' changed $($first.Value) -> $($plateau.Value)")
        }
    }

    $workloadChanged = $false
    $firstVisible =
        $caulReturn.resources.PSObject.Properties['visibleLandblocks']
    $plateauVisible =
        $caulPlateau.resources.PSObject.Properties['visibleLandblocks']
    if ($null -eq $firstVisible -or $null -eq $plateauVisible) {
        $failures.Add('canonical Caul workload comparison is missing resources.visibleLandblocks')
    }
    elseif ([long]$firstVisible.Value -ne [long]$plateauVisible.Value) {
        $workloadChanged = $true
        $warnings.Add("Caul plateau visible workload changed $($firstVisible.Value) -> $($plateauVisible.Value)")
    }

    foreach ($field in @(
        'worldEntities',
        'animatedEntities',
        'liveEntities',
        'materializedLiveEntities')) {
        $first = $caulReturn.resources.PSObject.Properties[$field]
        $plateau = $caulPlateau.resources.PSObject.Properties[$field]
        if ($null -eq $first -or $null -eq $plateau) {
            $failures.Add("canonical Caul population comparison is missing resources.$field")
        }
        elseif ([long]$first.Value -ne [long]$plateau.Value) {
            $workloadChanged = $true
            $warnings.Add("Caul plateau authoritative population '$field' changed $($first.Value) -> $($plateau.Value)")
        }
    }

    foreach ($field in @(
        'meshRenderData',
        'meshAtlasArrays',
        'meshEstimatedBytes',
        'trackedGpuBytes',
        'trackedGpuBuffers',
        'trackedGpuTextures',
        'ownedCompositeTextures',
        'compositeTextureOwners',
        'activeParticleTextures',
        'particleTextureOwners')) {
        $first = $caulReturn.resources.PSObject.Properties[$field]
        $plateau = $caulPlateau.resources.PSObject.Properties[$field]
        if ($null -eq $first -or $null -eq $plateau) {
            $failures.Add("canonical Caul cache comparison is missing resources.$field")
        }
        elseif ([long]$plateau.Value -gt [long]$first.Value) {
            $message = "Caul plateau canonical cache '$field' grew $($first.Value) -> $($plateau.Value)"
            if ($workloadChanged) { $warnings.Add($message) }
            else { $failures.Add($message) }
        }
    }

    foreach ($field in @(
        'particleBindings',
        'particleOwners',
        'effectOwners',
        'lightOwners',
        'scriptOwners')) {
        $first = $caulReturn.resources.PSObject.Properties[$field]
        $plateau = $caulPlateau.resources.PSObject.Properties[$field]
        if ($null -eq $first -or $null -eq $plateau) {
            $failures.Add("canonical Caul owner comparison is missing resources.$field")
        }
        elseif ([long]$plateau.Value -gt [long]$first.Value) {
            $message = "Caul plateau population-sensitive owner '$field' grew $($first.Value) -> $($plateau.Value)"
            if ($workloadChanged) { $warnings.Add($message) }
            else { $failures.Add($message) }
        }
    }

    foreach ($field in @(
        'particleEmitters',
        'particles',
        'activeScripts')) {
        $first = $caulReturn.resources.PSObject.Properties[$field]
        $plateau = $caulPlateau.resources.PSObject.Properties[$field]
        if ($null -eq $first -or $null -eq $plateau) {
            $failures.Add("canonical Caul transient comparison is missing resources.$field")
        }
        elseif ([long]$first.Value -ne [long]$plateau.Value) {
            $warnings.Add("Caul plateau authoritative/transient workload '$field' changed $($first.Value) -> $($plateau.Value)")
        }
    }
}

function Add-RelativeGateFailures {
    $caulReturn = $samples | Where-Object { $_.Destination -eq 'Caul return' -and $_.Phase -eq 'stationary' } | Select-Object -First 1
    $caulPlateau = $samples | Where-Object { $_.Destination -eq 'Caul plateau' -and $_.Phase -eq 'stationary' } | Select-Object -First 1

    foreach ($pair in @(
        [pscustomobject]@{ Label = 'Caul plateau'; First = $caulReturn; Return = $caulPlateau }))
    {
        if ($null -eq $pair.First -or $null -eq $pair.Return) {
            $failures.Add("$($pair.Label): missing same-location stationary sample")
            continue
        }

        if ($pair.First.EntityCount -ne $pair.Return.EntityCount) {
            $warnings.Add("$($pair.Label): connected entity population changed $($pair.First.EntityCount) -> $($pair.Return.EntityCount)")
        }
        if ($pair.First.AnimationCount -ne $pair.Return.AnimationCount) {
            $warnings.Add("$($pair.Label): connected animation population changed $($pair.First.AnimationCount) -> $($pair.Return.AnimationCount)")
        }

        $privateLimit = [Math]::Max(192.0, $pair.First.PrivateMiB * 0.20)
        $workingLimit = [Math]::Max(192.0, $pair.First.WorkingSetMiB * 0.20)
        if (($pair.Return.PrivateMiB - $pair.First.PrivateMiB) -gt $privateLimit) {
            $failures.Add("$($pair.Label): private memory grew by $([Math]::Round($pair.Return.PrivateMiB - $pair.First.PrivateMiB, 1)) MiB")
        }
        if (($pair.Return.WorkingSetMiB - $pair.First.WorkingSetMiB) -gt $workingLimit) {
            $failures.Add("$($pair.Label): working set grew by $([Math]::Round($pair.Return.WorkingSetMiB - $pair.First.WorkingSetMiB, 1)) MiB")
        }

        if ($null -ne $pair.First.UpdateP95Ms -and $null -ne $pair.Return.UpdateP95Ms) {
            $updateLimit = $pair.First.UpdateP95Ms * 1.5 + 0.5
            if ($pair.Return.UpdateP95Ms -gt $updateLimit) {
                $failures.Add("$($pair.Label): update p95 regressed $($pair.First.UpdateP95Ms) -> $($pair.Return.UpdateP95Ms) ms")
            }
        }
        if ($null -ne $pair.First.AllocP50Kb -and $null -ne $pair.Return.AllocP50Kb) {
            $allocLimit = $pair.First.AllocP50Kb * 1.5 + 16.0
            if ($pair.Return.AllocP50Kb -gt $allocLimit) {
                $failures.Add("$($pair.Label): allocation p50 regressed $($pair.First.AllocP50Kb) -> $($pair.Return.AllocP50Kb) KiB/frame")
            }
        }
    }
}

function Add-LogFailures {
    $fatalPatterns = @(
        'Unhandled exception',
        'AccessViolation',
        'OutOfMemoryException',
        'WeenieError',
        'device removed',
        'GPU reset',
        'live: disconnected',
        '[shutdown]',
        'ObjectDisposedException'
    )
    foreach ($pattern in $fatalPatterns) {
        $count = (Get-PatternCount $stdoutLog $pattern) + (Get-PatternCount $stderrLog $pattern)
        if ($count -gt 0) { $failures.Add("diagnostic '$pattern' appeared $count time(s)") }
    }

    $missingVfx = (Get-PatternCount $stderrLog 'No PhysicsScriptTable') +
        (Get-PatternCount $stderrLog 'ParticleEmitterInfo')
    if ($missingVfx -gt 0) {
        $warnings.Add("DAT-driven VFX diagnostics: $missingVfx missing table/emitter message(s)")
    }

    $missingLandblocks = Get-PatternCount $stdoutLog 'LandblockLoader.Load returned null'
    if ($missingLandblocks -gt 0) {
        $warnings.Add("World-edge empty landblocks: $missingLandblocks load miss(es)")
    }
}

function Close-ClientGracefully([Diagnostics.Process]$Client) {
    $Client.Refresh()
    if ($Client.HasExited) { return $true }
    $requested = $Client.CloseMainWindow()
    if (-not $requested) { return $false }
    if (-not $Client.WaitForExit(30000)) { return $false }
    $Client.WaitForExit()
    return $true
}

if (-not (Test-Path -LiteralPath $routePath)) { throw "route file not found: $routePath" }
if (@(Get-Process -Name AcDream.App -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'an AcDream.App client is already running; close it gracefully before the soak'
}

$udpOwner = @(Get-NetUDPEndpoint -LocalPort 9000 -ErrorAction SilentlyContinue)
if ($udpOwner.Count -eq 0) {
    throw 'local ACE is not listening on UDP port 9000'
}

if (-not $SkipBuild) {
    Write-Output 'Building Release solution...'
    & dotnet build (Join-Path $Repository 'AcDream.slnx') -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE" }
}
if (-not (Test-Path -LiteralPath $exe)) { throw "client executable not found: $exe" }
if (-not (Test-Path -LiteralPath $cliDll)) { throw "CLI assembly not found: $cliDll" }

$renderPackGate = New-ConnectedRenderPackGateState `
    -Root $artifactDir `
    -Preset $RenderPackPreset `
    -SettingOverrides $RenderPackSettingOverrides
$sessionConfigPath = New-ConnectedGraphicalSessionConfig `
    -State $renderPackGate `
    -Account $Account
try {
$binaryIdentity = Get-ConnectedGateBinaryIdentity `
    -Repository $Repository -Executable $exe -SkipBuild:$SkipBuild
$sourceCommit = $binaryIdentity.SourceCommit
$binaryProductVersion = $binaryIdentity.BinaryProductVersion
$binaryCommit = $binaryIdentity.BinaryCommit
$binaryMatchesSource = $binaryIdentity.BinaryMatchesSource
$sourceStatus = @($binaryIdentity.SourceTrackedStatus)
$commit = $binaryCommit
$videoControllers = @(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | ForEach-Object {
    [pscustomobject]@{ Name = $_.Name; DriverVersion = $_.DriverVersion; AdapterRam = $_.AdapterRAM }
})

$env:ACDREAM_DAT_DIR = "$env:USERPROFILE\Documents\Asheron's Call"
if (-not [string]::IsNullOrWhiteSpace($PreparedAssetPath)) {
    $resolvedPreparedAssetPath = [IO.Path]::GetFullPath($PreparedAssetPath)
    if (-not (Test-Path -LiteralPath $resolvedPreparedAssetPath -PathType Leaf)) {
        throw "prepared asset package not found: $resolvedPreparedAssetPath"
    }
    $env:ACDREAM_PAK_PATH = $resolvedPreparedAssetPath
}
$env:ACDREAM_LIVE = '1'
$env:ACDREAM_TEST_HOST = '127.0.0.1'
$env:ACDREAM_TEST_PORT = '9000'
$env:ACDREAM_TEST_USER = $Account
$env:ACDREAM_TEST_PASS = $Password
$env:ACDREAM_RETAIL_UI = '1'
$env:ACDREAM_FRAME_PROF = '1'
$env:ACDREAM_FRAME_HISTORY = $frameHistory
$env:ACDREAM_UNCAPPED_RENDER = if ($Uncapped) { '1' } else { $null }
$env:ACDREAM_DEVTOOLS = '0'
$env:ACDREAM_UI_PROBE_DUMP = '0'
$env:ACDREAM_UI_PROBE_SCRIPT = $routePath
$env:ACDREAM_AUTOMATION_ARTIFACT_DIR = $artifactDir
$env:ACDREAM_DUMP_MOVE_TRUTH = '1'
$env:ACDREAM_NO_AUDIO = $null
$env:ACDREAM_WB_DIAG = $null
$env:ACDREAM_COLLISION_SHADOW_EVERY =
    if ($CollisionShadowEvery -gt 0) { "$CollisionShadowEvery" } else { $null }
$env:ACDREAM_COLLISION_SHADOW_DIR =
    if ($CollisionShadowEvery -gt 0) { $collisionShadowDir } else { $null }

$acdreamEnvVars = Get-ChildItem Env: | Where-Object { $_.Name -like 'ACDREAM_*' } |
    Sort-Object Name |
    ForEach-Object {
        $sensitive = $_.Name -match '(?i)(PASS|PASSWORD|TOKEN|SECRET|KEY|USER|ACCOUNT)'
        [pscustomobject]@{
            Name = $_.Name
            Value = if ($sensitive) { '<redacted>' } else { $_.Value }
        }
    }
$envDisclosure = [pscustomobject][ordered]@{
    Script = 'run-connected-r6-soak.ps1'
    GeneratedUtc = [DateTime]::UtcNow.ToString('O')
    SourceCommit = $sourceCommit
    BinaryProductVersion = $binaryProductVersion
    BinaryCommit = $binaryCommit
    BinaryMatchesSource = $binaryMatchesSource
    TrackedSourceStatus = @($sourceStatus)
    Uncapped = [bool]$Uncapped
    DenseTown = [bool]$DenseTown
    CaptureContention = [bool]$CaptureContention
    RuntimeCounters = -not [bool]$SkipRuntimeCounters
    CollisionShadowEvery = $CollisionShadowEvery
    Route = $routeFileName
    RenderPackSelection = (Get-ConnectedRenderPackGateReport $renderPackGate)
    EnvironmentVariables = @($acdreamEnvVars)
}
$envDisclosure | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $artifactDir 'env-disclosure.json') -Encoding utf8

try {
    $process = Start-Process -FilePath $exe -WorkingDirectory $Repository `
        -ArgumentList @('--session-config', ('"' + $sessionConfigPath + '"')) `
        -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog -PassThru
    Write-Marker (
        "launch pid=$($process.Id) binaryCommit=$commit sourceCommit=$sourceCommit " +
        "binaryMatchesSource=$binaryMatchesSource session='$env:SESSIONNAME'")

    $null = Wait-ForLogPattern $process $stdoutLog 'live: in world' 1 $LoginTimeoutSeconds
    if (-not $SkipRuntimeCounters) {
        $counterArguments = @(
            'collect',
            '--process-id', $process.Id,
            '--counters', 'System.Runtime',
            '--refresh-interval', '1',
            '--format', 'csv',
            '--output', $runtimeCounters,
            '--duration', '00:00:15:00'
        )
        $counterProcess = Start-Process -FilePath 'dotnet-counters' `
            -ArgumentList $counterArguments `
            -RedirectStandardOutput $counterStdout `
            -RedirectStandardError $counterStderr `
            -PassThru `
            -WindowStyle Hidden
        Write-Marker "runtime-counters pid=$($counterProcess.Id)"
    }

    if ($CaptureContention) {
        $traceArguments = @(
            'collect',
            '--process-id', $process.Id,
            '--providers', 'Microsoft-Windows-DotNETRuntime:0x4000:5',
            '--output', $contentionTrace,
            '--duration', '00:00:15:00'
        )
        $traceProcess = Start-Process -FilePath 'dotnet-trace' `
            -ArgumentList $traceArguments `
            -RedirectStandardOutput $contentionStdout `
            -RedirectStandardError $contentionStderr `
            -PassThru `
            -WindowStyle Hidden
        Write-Marker "contention-capture pid=$($traceProcess.Id)"
    }

    Write-Marker ("in-world elapsed={0:N1}s" -f $stopwatch.Elapsed.TotalSeconds)

    foreach ($destination in $destinations) {
        $null = Wait-ForLogPattern $process $stdoutLog 'live: teleport materialized' $destination.Occurrence 60
        Write-Marker ("materialized destination='{0}' occurrence={1} elapsed={2:N1}s" -f `
            $destination.Name, $destination.Occurrence, $stopwatch.Elapsed.TotalSeconds)

        $exerciseBaseline = [pscustomobject]@{
            ForwardPress = Get-PatternCount $stdoutLog '[input] MovementForward Press'
            ForwardRelease = Get-PatternCount $stdoutLog '[input] MovementForward Release'
            JumpPress = Get-PatternCount $stdoutLog '[input] MovementJump Press'
            JumpRelease = Get-PatternCount $stdoutLog '[input] MovementJump Release'
            Combat = Get-PatternCount $stdoutLog '[input] CombatToggleCombat Press'
            MoveTruth = Get-PatternCount $stdoutLog 'move-truth OUT'
        }

        $null = Wait-ForLogPattern $process $stdoutLog '[input] MovementTurnRight Press' $destination.Occurrence 25
        $turnProfileStart = (Get-FrameProfiles $stdoutLog).Count
        $turnResult = Wait-ForNextFrameProfile $process $stdoutLog $turnProfileStart 12
        $turnSample = Capture-Sample $process $destination.Name 'turn' $turnResult.Line
        Write-Marker "turn-sample destination='$($destination.Name)' cpuP95=$($turnSample.CpuP95Ms) gpuP95=$($turnSample.GpuP95Ms)"

        $null = Wait-ForLogPattern $process $stdoutLog '[input] MovementTurnRight Release' $destination.Occurrence 12
        $checkpointPattern =
            "[world-gate] checkpoint name=$($destination.Checkpoint) sequence=$($destination.Occurrence)"
        $null = Wait-ForLogPattern $process $stdoutLog $checkpointPattern 1 60
        $stationaryProfiles = Get-FrameProfiles $stdoutLog
        if ($stationaryProfiles.Count -eq 0) {
            throw "no frame-prof report exists at checkpoint '$($destination.Checkpoint)'"
        }
        $stationarySample =
            Capture-Sample $process $destination.Name 'stationary' $stationaryProfiles[-1]
        Write-Marker "stationary-sample destination='$($destination.Name)' wsMiB=$($stationarySample.WorkingSetMiB) privateMiB=$($stationarySample.PrivateMiB) ent=$($stationarySample.EntityCount) anim=$($stationarySample.AnimationCount) updateP95=$($stationarySample.UpdateP95Ms)"

        if ($destination.Exercise) {
            Observe-R6Exercise $process $destination.Name $exerciseBaseline
        }
    }

    if ((Get-PatternCount $stdoutLog 'live: teleport materialized') -ne $destinations.Count) {
        $failures.Add("expected $($destinations.Count) teleport materializations")
    }

    $lastCheckpointName = $expectedCheckpointNames[-1]
    $lastCheckpointSequence = $expectedCheckpointNames.Count
    $null = Wait-ForLogPattern `
        $process `
        $stdoutLog `
        "[world-gate] checkpoint name=$lastCheckpointName sequence=$lastCheckpointSequence" `
        1 `
        30
    $canonicalCheckpoints = @(Read-CanonicalCheckpoints)
    Add-CanonicalCheckpointFailures $process
    Add-ConnectedRenderPackMetadataFailures `
        -ArtifactDirectory $artifactDir `
        -ScreenshotNames $expectedCheckpointNames `
        -State $renderPackGate `
        -Failures $failures `
        -Label $runName
    if ($CollisionShadowEvery -gt 0 -and $canonicalCheckpoints.Count -gt 0) {
        $lastShadow =
            $canonicalCheckpoints[-1].resources.PSObject.Properties['collisionShadow']
        $shadowSamples = if ($null -ne $lastShadow) {
            $lastShadow.Value.PSObject.Properties['samples']
        }
        if ($null -eq $shadowSamples -or [long]$shadowSamples.Value -le 0) {
            $failures.Add('collision shadow did not sample any query')
        }
        $mismatchArtifacts = @(
            Get-ChildItem -LiteralPath $collisionShadowDir `
                -Filter 'collision-shadow-*.json' `
                -File `
                -ErrorAction SilentlyContinue)
        if ($mismatchArtifacts.Count -ne 0) {
            $failures.Add(
                "collision shadow emitted $($mismatchArtifacts.Count) mismatch artifact(s)")
        }
    }
    if (-not $DenseTown) {
        Add-RelativeGateFailures
    }

    Write-Marker 'route-complete requesting graceful close'
    $gracefulExit = Close-ClientGracefully $process
    $process.Refresh()
    if ($process.HasExited) { $exitCode = [int]$process.ExitCode }
    if (-not $gracefulExit) { $failures.Add('client did not exit through the graceful WM_CLOSE path') }
    if ($null -ne $exitCode -and $exitCode -ne 0) { $failures.Add("client exit code was $exitCode") }
    Add-LogFailures
}
catch {
    $failures.Add($_.Exception.Message)
    Write-Marker "ERROR $($_.Exception.Message)"
}
finally {
    if ($null -ne $process) {
        $process.Refresh()
        if (-not $process.HasExited) {
            $gracefulExit = Close-ClientGracefully $process
            if (-not $gracefulExit -and -not $process.HasExited) {
                $failures.Add('client remained alive 30 seconds after WM_CLOSE and required forced termination')
                Stop-Process -Id $process.Id -Force
                $process.WaitForExit(10000)
            }
        }
        if ($process.HasExited -and $null -eq $exitCode) { $exitCode = [int]$process.ExitCode }
        $process.Dispose()
    }
    if ($null -ne $traceProcess) {
        if (-not $traceProcess.WaitForExit(30000)) {
            $warnings.Add('dotnet-trace did not exit within 30 seconds of client shutdown')
            Stop-Process -Id $traceProcess.Id -Force
            $traceProcess.WaitForExit(10000)
        }
        if (-not (Test-Path -LiteralPath $contentionTrace)) {
            $failures.Add('contention capture did not produce a nettrace artifact')
        }
        $traceProcess.Dispose()
    }
    if ($null -ne $counterProcess) {
        if (-not $counterProcess.WaitForExit(30000)) {
            $warnings.Add('dotnet-counters did not exit within 30 seconds of client shutdown')
            Stop-Process -Id $counterProcess.Id -Force
            $counterProcess.WaitForExit(10000)
        }
        if (-not (Test-Path -LiteralPath $runtimeCounters)) {
            $failures.Add('runtime counter capture did not produce a CSV artifact')
        }
        $counterProcess.Dispose()
    }

    if (-not (Test-Path -LiteralPath $frameHistory)) {
        $failures.Add('frame-history export was not written during graceful shutdown')
    }
    elseif ((Test-Path -LiteralPath $checkpointTimeline) -and
        (Test-Path -LiteralPath $markerLog)) {
        & dotnet $cliDll summarize-frame-history `
            $frameHistory `
            $checkpointTimeline `
            $markerLog `
            $frameSummary
        if ($LASTEXITCODE -ne 0) {
            $failures.Add("frame-history summary failed with exit code $LASTEXITCODE")
        }
        elseif (-not (Test-Path -LiteralPath $frameSummary)) {
            $failures.Add('frame-history summary did not produce its JSON artifact')
        }
    }

    $samples | Export-Csv -LiteralPath $sampleCsv -NoTypeInformation
    $report = [pscustomobject][ordered]@{
        Passed = $failures.Count -eq 0
        StartedUtc = [DateTime]::UtcNow.Subtract($stopwatch.Elapsed).ToString('O')
        FinishedUtc = [DateTime]::UtcNow.ToString('O')
        ElapsedSeconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 3)
        Commit = $commit
        SourceCommit = $sourceCommit
        BinaryProductVersion = $binaryProductVersion
        BinaryCommit = $binaryCommit
        BinaryMatchesSource = $binaryMatchesSource
        SessionName = $env:SESSIONNAME
        CollisionShadowEvery = $CollisionShadowEvery
        RenderPackSelection = (Get-ConnectedRenderPackGateReport $renderPackGate)
        ExitCode = $exitCode
        GracefulExit = $gracefulExit
        Failures = @($failures)
        Warnings = @($warnings)
        SourceStatus = $sourceStatus
        VideoControllers = $videoControllers
        Samples = @($samples)
        CanonicalCheckpoints = @($canonicalCheckpoints)
        Artifacts = [pscustomobject]@{
            Stdout = $stdoutLog
            Stderr = $stderrLog
            Markers = $markerLog
            SamplesCsv = $sampleCsv
            ReportJson = $reportJson
            CheckpointTimeline = $checkpointTimeline
            EnvDisclosure = Join-Path $artifactDir 'env-disclosure.json'
            FrameHistory = $frameHistory
            FrameSummary = $frameSummary
            ContentionTrace = if ($CaptureContention) { $contentionTrace } else { $null }
            RuntimeCounters = if (-not $SkipRuntimeCounters) { $runtimeCounters } else { $null }
        }
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportJson -Encoding utf8

    Write-Output "REPORT=$reportJson"
    Write-Output "SAMPLES=$sampleCsv"
    Write-Output "RESULT=$(if ($report.Passed) { 'PASS' } else { 'FAIL' })"
    foreach ($failure in $failures) { Write-Output "FAILURE=$failure" }
    foreach ($warning in $warnings) { Write-Output "WARNING=$warning" }
}
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
