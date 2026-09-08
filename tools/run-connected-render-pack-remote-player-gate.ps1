[CmdletBinding()]
param(
    [string]$Repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$Account = $env:ACDREAM_TEST_USER,
    [string]$Password = $env:ACDREAM_TEST_PASS,
    [string]$ObserverAccount = $env:ACDREAM_TEST_OBSERVER_USER,
    [string]$ObserverPassword = $env:ACDREAM_TEST_OBSERVER_PASS,
    [ValidateRange(0, [int]::MaxValue)][int]$PrimaryCharacterIndex = 0,
    [ValidateRange(0, [int]::MaxValue)][int]$ObserverCharacterIndex = 0,
    [switch]$SkipBuild,
    [int]$LoginTimeoutSeconds = 90
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'connected-render-pack-gate-common.ps1')

if ([string]::IsNullOrWhiteSpace($Account)) { $Account = 'testaccount' }
if ([string]::IsNullOrWhiteSpace($Password)) { $Password = 'testpassword' }
if ([string]::IsNullOrWhiteSpace($ObserverAccount)) { $ObserverAccount = $Account }
if ([string]::IsNullOrWhiteSpace($ObserverPassword)) { $ObserverPassword = $Password }
if ([string]::Equals($Account, $ObserverAccount, [StringComparison]::OrdinalIgnoreCase)) {
    throw ('Remote-player shadow evidence requires distinct ACE accounts. ' +
        'Set ACDREAM_TEST_OBSERVER_USER and ACDREAM_TEST_OBSERVER_PASS.')
}

$startedUtc = [DateTime]::UtcNow.ToString('O')
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$root = Join-Path $Repository `
    "artifacts\atmospheric-rendering\connected-remote-player-$stamp"
$observerRoot = Join-Path $root 'observer'
$primaryRoot = Join-Path $root 'primary'
$observerRoute = Join-Path $Repository `
    'tools\connected-render-pack-remote-observer.route.txt'
$primaryRoute = Join-Path $Repository `
    'tools\connected-render-pack-remote-primary.route.txt'
$exe = Join-Path $Repository `
    'src\AcDream.App\bin\Release\net10.0\AcDream.App.exe'
$observerState = $null
$primaryState = $null
$observerConfig = $null
$primaryConfig = $null
$observer = $null
$primary = $null
$binaryIdentity = $null
$result = $null
$failure = $null

function Set-ClientEnvironment {
    param(
        [Parameter(Mandatory = $true)][object]$State,
        [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
        [Parameter(Mandatory = $true)][string]$Route,
        [Parameter(Mandatory = $true)][string]$SessionAccount,
        [Parameter(Mandatory = $true)][string]$SessionPassword
    )
    $env:ACDREAM_CONFIG_DIR = $State.ConfigDirectory
    $env:ACDREAM_DATA_DIR = $State.DataDirectory
    $env:ACDREAM_CACHE_DIR = $State.CacheDirectory
    $env:ACDREAM_DAT_DIR = "$env:USERPROFILE\Documents\Asheron's Call"
    $env:ACDREAM_LIVE = '1'
    $env:ACDREAM_TEST_HOST = '127.0.0.1'
    $env:ACDREAM_TEST_PORT = '9000'
    $env:ACDREAM_TEST_USER = $SessionAccount
    $env:ACDREAM_TEST_PASS = $SessionPassword
    $env:ACDREAM_RETAIL_UI = '1'
    $env:ACDREAM_DEVTOOLS = '0'
    $env:ACDREAM_UI_PROBE_DUMP = '0'
    $env:ACDREAM_UI_PROBE_SCRIPT = $Route
    $env:ACDREAM_AUTOMATION_ARTIFACT_DIR = $ArtifactDirectory
    $env:ACDREAM_NO_AUDIO = '1'
}

function Wait-ForLogPattern {
    param(
        [Parameter(Mandatory = $true)][Diagnostics.Process]$Client,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $Client.Refresh()
        if ($Client.HasExited) {
            throw "client exited with code $($Client.ExitCode) while waiting for '$Pattern'"
        }
        if (Test-Path -LiteralPath $Path) {
            $sessionFailure = Select-String -LiteralPath $Path `
                -SimpleMatch 'live: session failed:' | Select-Object -Last 1
            if ($null -ne $sessionFailure) {
                throw "client session failed while waiting for '$Pattern': $($sessionFailure.Line)"
            }
            if (Select-String -LiteralPath $Path -SimpleMatch $Pattern -Quiet) {
                return
            }
        }
        Start-Sleep -Milliseconds 250
    }
    throw "timed out after $TimeoutSeconds seconds waiting for '$Pattern'"
}

function Close-ClientGracefully {
    param([Parameter(Mandatory = $true)][Diagnostics.Process]$Client)
    $Client.Refresh()
    if ($Client.HasExited) { return }
    if (-not $Client.CloseMainWindow() -or -not $Client.WaitForExit(30000)) {
        throw "client $($Client.Id) did not accept graceful close within 30 seconds"
    }
    $Client.WaitForExit()
}

function Stop-ExactClient {
    param([Diagnostics.Process]$Client)
    if ($null -eq $Client) { return }
    $Client.Refresh()
    if (-not $Client.HasExited) {
        $closed = $Client.CloseMainWindow()
        if (-not $closed -or -not $Client.WaitForExit(10000)) {
            Stop-Process -Id $Client.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

function Publish-ClientSignal {
    param(
        [Parameter(Mandatory = $true)][string]$ClientRoot,
        [Parameter(Mandatory = $true)][string]$Name
    )
    if ($Name -notmatch '^[A-Za-z0-9_-]{1,80}$') {
        throw "invalid automation signal name '$Name'"
    }
    $signalDirectory = Join-Path $ClientRoot 'signals'
    $null = New-Item -ItemType Directory -Force -Path $signalDirectory
    Assert-ConnectedGateNoReparsePoint $signalDirectory
    Set-Content -Encoding ascii -LiteralPath `
        (Join-Path $signalDirectory "$Name.signal") -Value 'published'
}

function Read-StatusEvents {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "session status stream is missing: '$Path'"
    }
    return @(Get-Content -LiteralPath $Path | ForEach-Object {
        $_ | ConvertFrom-Json
    })
}

function Assert-TerminalStatus {
    param([Parameter(Mandatory = $true)][string]$Path)
    $events = @(Read-StatusEvents $Path)
    foreach ($name in @('started', 'connected', 'characterList', 'enteredWorld',
            'disconnected', 'exited')) {
        $matching = @($events | Where-Object { $_.e -ceq $name })
        if ($matching.Count -ne 1) {
            throw "'$Path' expected one '$name' event, observed $($matching.Count)"
        }
    }
    $entered = @($events | Where-Object { $_.e -ceq 'enteredWorld' })[0]
    $exited = @($events | Where-Object { $_.e -ceq 'exited' })[0]
    if ([int]$exited.code -ne 0) {
        throw "'$Path' terminal status code was $($exited.code)"
    }
    return $entered
}

function Read-RemoteMetadata {
    param([Parameter(Mandatory = $true)][string]$Name)
    Assert-ConnectedGateSafeLeafName $Name
    $path = Join-Path $primaryRoot "screenshots\$Name.metadata.json"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "remote-player metadata is missing: '$path'"
    }
    $metadata = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
    $pack = $metadata.RenderPack
    $classes = $pack.ShadowTransformChurn.CasterClasses
    if ([int]$metadata.SchemaVersion -ne 1 -or [int]$pack.State -ne 2 -or
        [string]$pack.PackId -cne 'acdream.atmospheric' -or
        [string]$pack.PresetId -cne 'medium') {
        throw "'$Name' did not capture active Atmospheric Medium metadata"
    }
    if (-not [bool]$pack.Outdoor -or [double]$pack.DirectionalShadowStrength -le 0) {
        throw "'$Name' did not capture an active outdoor directional-shadow frame"
    }
    if ([int]$classes.LocalPlayers -ne 1 -or [int]$classes.RemotePlayers -lt 1) {
        throw "'$Name' caster classes were local=$($classes.LocalPlayers), remote=$($classes.RemotePlayers)"
    }
    if ([int]$pack.ShadowCasterCount -le 0 -or [int]$pack.CascadeDrawCount -ne 3) {
        throw "'$Name' did not record complete Medium shadow work"
    }
    return [pscustomobject][ordered]@{
        Name = $Name
        Path = $path
        PackId = $pack.PackId
        PresetId = $pack.PresetId
        ShadowCasterCount = $pack.ShadowCasterCount
        CascadeDrawCount = $pack.CascadeDrawCount
        LocalPlayers = $classes.LocalPlayers
        RemotePlayers = $classes.RemotePlayers
        NonPlayerCreatures = $classes.NonPlayerCreatures
        EquippedChildren = $classes.EquippedChildren
        DirectionalShadowSourceKind = $pack.DirectionalShadowSourceKind
        DirectionalShadowStrength = $pack.DirectionalShadowStrength
        RemoteRootChanges = $pack.ShadowTransformChurn.LiveDynamicRootChanges
        SharedWorldTransformUsedInstances = $pack.SharedWorldTransformUsedInstances
    }
}

if (@(Get-Process -Name AcDream.App -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'an AcDream.App client is already running; close it gracefully before the two-client gate'
}
if (@(Get-NetUDPEndpoint -LocalPort 9000 -ErrorAction SilentlyContinue).Count -eq 0) {
    throw 'local ACE is not listening on UDP port 9000'
}
foreach ($required in @($observerRoute, $primaryRoute)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "route file not found: '$required'"
    }
}
if (-not $SkipBuild) {
    Write-Output 'Building Release solution...'
    & dotnet build (Join-Path $Repository 'AcDream.slnx') -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE" }
}
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "client executable not found: '$exe'"
}

$null = New-Item -ItemType Directory -Path $observerRoot, $primaryRoot
foreach ($path in @($root, $observerRoot, $primaryRoot)) {
    Assert-ConnectedGateNoReparsePoint $path
}
$observerState = New-ConnectedRenderPackGateState -Root $observerRoot -Preset medium
$primaryState = New-ConnectedRenderPackGateState -Root $primaryRoot -Preset medium
try {
    $binaryIdentity = Get-ConnectedGateBinaryIdentity `
        -Repository $Repository -Executable $exe -SkipBuild:$SkipBuild
    $observerStatus = Join-Path $observerState.StateDirectory 'status\observer.jsonl'
    $primaryStatus = Join-Path $primaryState.StateDirectory 'status\primary.jsonl'
    $observerConfig = New-ConnectedGraphicalSessionConfig `
        -State $observerState -Account $ObserverAccount `
        -CharacterIndex $ObserverCharacterIndex -Plugins @() `
        -StatusFile $observerStatus
    $primaryConfig = New-ConnectedGraphicalSessionConfig `
        -State $primaryState -Account $Account `
        -CharacterIndex $PrimaryCharacterIndex -Plugins @() `
        -StatusFile $primaryStatus

    $observerStdout = Join-Path $observerRoot 'client.stdout.log'
    $observerStderr = Join-Path $observerRoot 'client.stderr.log'
    Set-ClientEnvironment $observerState $observerRoot $observerRoute `
        $ObserverAccount $ObserverPassword
    $observer = Start-Process -FilePath $exe -WorkingDirectory $Repository `
        -ArgumentList @('--session-config', ('"' + $observerConfig + '"')) `
        -RedirectStandardOutput $observerStdout `
        -RedirectStandardError $observerStderr -PassThru
    Wait-ForLogPattern $observer $observerStdout 'live: in world' $LoginTimeoutSeconds
    Wait-ForLogPattern $observer $observerStdout `
        '[world-gate] checkpoint name=remote-observer-ready' 120

    $primaryStdout = Join-Path $primaryRoot 'client.stdout.log'
    $primaryStderr = Join-Path $primaryRoot 'client.stderr.log'
    Set-ClientEnvironment $primaryState $primaryRoot $primaryRoute `
        $Account $Password
    $primary = Start-Process -FilePath $exe -WorkingDirectory $Repository `
        -ArgumentList @('--session-config', ('"' + $primaryConfig + '"')) `
        -RedirectStandardOutput $primaryStdout `
        -RedirectStandardError $primaryStderr -PassThru
    Wait-ForLogPattern $primary $primaryStdout 'live: in world' $LoginTimeoutSeconds
    Wait-ForLogPattern $primary $primaryStdout `
        '[world-gate] screenshot-complete name=remote-player-before' 120
    Publish-ClientSignal $observerRoot 'primary-before'
    Wait-ForLogPattern $observer $observerStdout `
        '[world-gate] checkpoint name=remote-observer-moved' 60
    Publish-ClientSignal $primaryRoot 'observer-moved'
    Wait-ForLogPattern $primary $primaryStdout `
        '[world-gate] screenshot-complete name=remote-player-after' 120
    Wait-ForLogPattern $primary $primaryStdout `
        '[world-gate] checkpoint name=remote-primary' 30

    Close-ClientGracefully $primary
    Close-ClientGracefully $observer
    if ($primary.ExitCode -ne 0 -or $observer.ExitCode -ne 0) {
        throw "client exit codes were primary=$($primary.ExitCode), observer=$($observer.ExitCode)"
    }
    $primaryEntered = Assert-TerminalStatus $primaryStatus
    $observerEntered = Assert-TerminalStatus $observerStatus
    if ([uint32]$primaryEntered.characterId -eq [uint32]$observerEntered.characterId) {
        throw 'both clients entered world with the same character identity'
    }
    $before = Read-RemoteMetadata 'remote-player-before'
    $after = Read-RemoteMetadata 'remote-player-after'
    $result = [pscustomobject][ordered]@{
        Passed = $true
        PrimaryProcessId = $primary.Id
        ObserverProcessId = $observer.Id
        PrimaryCharacterId = $primaryEntered.characterId
        ObserverCharacterId = $observerEntered.characterId
        PrimaryExitCode = $primary.ExitCode
        ObserverExitCode = $observer.ExitCode
        Before = $before
        After = $after
    }
    Write-Output (
        "PASS remote-player casters before=$($before.RemotePlayers) " +
        "after=$($after.RemotePlayers) primary=0x$('{0:X8}' -f [uint32]$primaryEntered.characterId) " +
        "observer=0x$('{0:X8}' -f [uint32]$observerEntered.characterId)")
}
catch {
    $failure = $_.Exception.ToString()
}
finally {
    Stop-ExactClient $primary
    Stop-ExactClient $observer
    if ($null -ne $primary) { $primary.Dispose() }
    if ($null -ne $observer) { $observer.Dispose() }
    if ($null -ne $primaryConfig -and $null -ne $primaryState) {
        Remove-ConnectedGraphicalSessionConfig -State $primaryState -Path $primaryConfig
    }
    if ($null -ne $observerConfig -and $null -ne $observerState) {
        Remove-ConnectedGraphicalSessionConfig -State $observerState -Path $observerConfig
    }
    if ($null -ne $primaryState) {
        Restore-ConnectedRenderPackGateEnvironment $primaryState
    }
    if ($null -ne $observerState) {
        Restore-ConnectedRenderPackGateEnvironment $observerState
    }
    $report = [pscustomobject][ordered]@{
        Passed = $null -ne $result -and [string]::IsNullOrWhiteSpace($failure)
        StartedUtc = $startedUtc
        FinishedUtc = [DateTime]::UtcNow.ToString('O')
        SourceCommit = if ($null -eq $binaryIdentity) { $null } else { $binaryIdentity.SourceCommit }
        BinaryCommit = if ($null -eq $binaryIdentity) { $null } else { $binaryIdentity.BinaryCommit }
        BinaryMatchesSource = if ($null -eq $binaryIdentity) { $false } else { $binaryIdentity.BinaryMatchesSource }
        TrackedSourceStatus = if ($null -eq $binaryIdentity) { @() } else { @($binaryIdentity.SourceTrackedStatus) }
        PrimaryCharacterIndex = $PrimaryCharacterIndex
        ObserverCharacterIndex = $ObserverCharacterIndex
        DistinctAccountsConfigured = $true
        Result = $result
        Failure = $failure
    }
    $reportPath = Join-Path $root 'report.json'
    $report | ConvertTo-Json -Depth 10 |
        Set-Content -Encoding utf8 -LiteralPath $reportPath
    Write-Output "REPORT=$reportPath"
    Write-Output "RESULT=$(if ($report.Passed) { 'PASS' } else { 'FAIL' })"
}

if (-not [string]::IsNullOrWhiteSpace($failure)) { throw $failure }
if ($null -eq $result) { throw 'remote-player gate produced no result' }
