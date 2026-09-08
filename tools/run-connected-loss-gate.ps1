
[CmdletBinding()]
param(
    [string]$Repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$Account = $env:ACDREAM_TEST_USER,
    [string]$Password = $env:ACDREAM_TEST_PASS,
    [string]$AceLogPath = 'C:\ACE\Server\ACE_Log.txt',
    [switch]$SkipBuild,
    [int]$SessionTimeoutSeconds = 420,
    [int]$DropPct = 2,
    [int]$Seed = 1
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Account)) { $Account = 'testaccount' }
if ([string]::IsNullOrWhiteSpace($Password)) { $Password = 'testpassword' }
if ($DropPct -lt 1 -or $DropPct -gt 100) { throw "DropPct must be 1..100 (got $DropPct)" }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$root = Join-Path $Repository "logs\connected-loss-gate-$stamp"
$null = New-Item -ItemType Directory -Force -Path $root
$reportPath = Join-Path $root 'report.json'
$exe = Join-Path $Repository 'src\AcDream.App\bin\Release\net10.0\AcDream.App.exe'
$failures = [System.Collections.Generic.List[string]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()
$sessions = [System.Collections.Generic.List[object]]::new()
$startedUtc = [DateTime]::UtcNow
$lossEvidence = $null

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
}

function Read-NetFinal([string]$Stdout) {
    if (-not (Test-Path -LiteralPath $Stdout)) { return $null }
    $line = @(Get-Content -LiteralPath $Stdout |
        Select-String -SimpleMatch '[net-final]' | Select-Object -Last 1)
    if ($line.Count -eq 0) { return $null }
    $text = [string]$line[0].Line
    $counters = [ordered]@{}
    foreach ($match in [Text.RegularExpressions.Regex]::Matches(
        $text, '([a-z\-]+)=(-?\d+)')) {
        $counters[$match.Groups[1].Value] = [long]$match.Groups[2].Value
    }
    return [pscustomobject]@{ Line = $text; Counters = [pscustomobject]$counters }
}

# Parse the decorator's own final ledger: "[net-loss] dropped out=N in=N ...".
function Read-NetLossDropped([string]$Stdout) {
    if (-not (Test-Path -LiteralPath $Stdout)) { return $null }
    $line = @(Get-Content -LiteralPath $Stdout |
        Select-String -SimpleMatch '[net-loss] dropped' | Select-Object -Last 1)
    if ($line.Count -eq 0) { return $null }
    $text = [string]$line[0].Line
    $match = [Text.RegularExpressions.Regex]::Match(
        $text, 'dropped out=(\d+) in=(\d+)')
    if (-not $match.Success) { return $null }
    return [pscustomobject]@{
        Line = $text
        DroppedOut = [long]$match.Groups[1].Value
        DroppedIn = [long]$match.Groups[2].Value
    }
}

function Invoke-Session(
    [string]$Label,
    [string]$RoutePath,
    [bool]$Uncapped,
    [string[]]$ExpectedCheckpoints,
    [string[]]$ExpectedScreenshots)
{
    $sessionDir = Join-Path $root $Label
    $artifactDir = Join-Path $sessionDir 'artifacts'
    $null = New-Item -ItemType Directory -Force -Path $artifactDir
    $stdout = Join-Path $sessionDir 'stdout.log'
    $stderr = Join-Path $sessionDir 'stderr.log'
    $timeline = Join-Path $artifactDir 'world-lifecycle.checkpoints.jsonl'
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
    $env:ACDREAM_COLLISION_SHADOW_EVERY = $null
    $env:ACDREAM_COLLISION_SHADOW_DIR = $null
    # N5 -- THE point of this gate: deterministic seeded loss + the probe
    # that makes the recovery observable.
    $env:ACDREAM_NET_DROP_PCT = "$DropPct"
    $env:ACDREAM_NET_DROP_SEED = "$Seed"
    $env:ACDREAM_NET_DROP_DIR = $null   # default: both directions
    $env:ACDREAM_PROBE_NET = '1'

    try {
        $client = Start-Process -FilePath $exe -WorkingDirectory $Repository `
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

        foreach ($name in $ExpectedScreenshots) {
            $png = Join-Path $artifactDir "screenshots\$name.png"
            if (-not (Test-Png $png)) { $failures.Add("${Label}: missing or invalid screenshot '$png'") }
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

        # ---- N5: the loss-evidence assertions -------------------------
        $netFinal = Read-NetFinal $stdout
        $netLoss = Read-NetLossDropped $stdout
        if ($null -eq $netFinal) {
            $failures.Add("${Label}: no [net-final] transport counter line was emitted")
        }
        if ($null -eq $netLoss) {
            $failures.Add("${Label}: no [net-loss] dropped ledger was emitted (decorator absent?)")
        }
        if ($null -ne $netFinal) {
            $resends = [long]$netFinal.Counters.resends
            $nakOut = [long]$netFinal.Counters.'nak-out'
            $nakIn = [long]$netFinal.Counters.'nak-in'
            if (($resends -eq 0) -and ($nakIn -eq 0)) {
                $failures.Add((
                    "${Label}: no C2S recovery observed " +
                    "(resends=0, nak-in=0) -- outbound loss never healed or never happened"))
            }
            if ($nakOut -eq 0) {
                $failures.Add((
                    "${Label}: no S2C recovery observed " +
                    "(nak-out=0) -- inbound loss never healed or never happened"))
            }
            foreach ($invariant in @('cksum-fail', 'sanity-drop', 'uncached-nak')) {
                $value = [long]$netFinal.Counters.$invariant
                if ($value -ne 0) {
                    $failures.Add((
                        "${Label}: keystream-health invariant violated " +
                        "($invariant=$value, expected 0)"))
                }
            }
        }
        if ($null -ne $netLoss -and ($netLoss.DroppedOut + $netLoss.DroppedIn) -eq 0) {
            $failures.Add("${Label}: the decorator forwarded everything (dropped out=0 in=0) -- no loss was injected")
        }
        $script:lossEvidence = [pscustomobject][ordered]@{
            NetFinal = $netFinal
            NetLoss = $netLoss
        }

        $session = [pscustomobject][ordered]@{
            Label = $Label
            Uncapped = $Uncapped
            DropPct = $DropPct
            Seed = $Seed
            ElapsedSeconds = [Math]::Round($elapsed.Elapsed.TotalSeconds, 3)
            GracefulExit = $graceful
            ExitCode = $exitCode
            Process = $processSample
            LossEvidence = $script:lossEvidence
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

$null = Invoke-Session `
    'loss-capped' `
    (Join-Path $Repository 'tools\connected-world-lifecycle.route.txt') `
    $false `
    @('capped_login', 'aerlinthe_first', 'rynthid', 'facility_hub', 'holtburg_after_dungeon', 'aerlinthe_revisit') `
    @('capped_login', 'aerlinthe_first', 'facility_hub', 'holtburg_after_dungeon', 'aerlinthe_revisit')

$report = [pscustomobject][ordered]@{
    Passed = $failures.Count -eq 0
    StartedUtc = $startedUtc.ToString('O')
    FinishedUtc = [DateTime]::UtcNow.ToString('O')
    Commit = (& git -C $Repository rev-parse HEAD).Trim()
    SourceStatus = @(& git -C $Repository status --short)
    SessionName = $env:SESSIONNAME
    DropPct = $DropPct
    Seed = $Seed
    LossEvidence = $lossEvidence
    Failures = @($failures)
    Warnings = @($warnings)
    Sessions = @($sessions)
}
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $reportPath -Encoding utf8

Write-Output "REPORT=$reportPath"
if ($null -ne $lossEvidence -and $null -ne $lossEvidence.NetFinal) {
    Write-Output "NETFINAL=$($lossEvidence.NetFinal.Line)"
}
if ($null -ne $lossEvidence -and $null -ne $lossEvidence.NetLoss) {
    Write-Output "NETLOSS=$($lossEvidence.NetLoss.Line)"
}
Write-Output "RESULT=$(if ($report.Passed) { 'PASS' } else { 'FAIL' })"
foreach ($failure in $failures) { Write-Output "FAILURE=$failure" }
foreach ($warning in $warnings) { Write-Output "WARNING=$warning" }

if ($failures.Count -gt 0) { exit 1 }
