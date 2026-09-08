[CmdletBinding()]
param(
    [string]$Repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$Account = $env:ACDREAM_TEST_USER,
    [string]$Password = $env:ACDREAM_TEST_PASS,
    [switch]$SkipBuild,
    [int]$LoginTimeoutSeconds = 90
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'connected-render-pack-gate-common.ps1')

if ([string]::IsNullOrWhiteSpace($Account)) { $Account = 'testaccount' }
if ([string]::IsNullOrWhiteSpace($Password)) { $Password = 'testpassword' }

$packageId = 'acdream.test.external-render-pack-package'
$packId = 'acdream.test.external-render-pack'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$root = Join-Path $Repository `
    "artifacts\atmospheric-rendering\connected-package-lifecycle-$stamp"
$route = Join-Path $Repository `
    'tools\connected-render-pack-package-lifecycle.route.txt'
$exe = Join-Path $Repository `
    'src\AcDream.App\bin\Release\net10.0\AcDream.App.exe'
$fixture = Join-Path $Repository `
    'tests\AcDream.Plugin.Tests.Fixtures.HostPlugin\bin\Release\net10.0\AcDream.Plugin.Tests.Fixtures.HostPlugin.dll'
$startedUtc = [DateTime]::UtcNow.ToString('O')
$results = [Collections.Generic.List[object]]::new()
$renderPackGate = $null
$sessionConfigPath = $null
$failure = $null
$binaryIdentity = $null

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

function Write-Selection {
    param(
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$Preset
    )
    [ordered]@{
        display = [ordered]@{
            renderPack = [ordered]@{
                packId = $packId
                packVersion = $Version
                presetId = $Preset
                settingOverrides = [ordered]@{}
            }
        }
        version = 3
    } | ConvertTo-Json -Depth 8 |
        Set-Content -Encoding utf8 -LiteralPath (
            Join-Path $renderPackGate.ConfigDirectory 'settings.json')
}

function Write-PackageVersion {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string]$Version
    )
    Set-Content -Encoding utf8 -NoNewline -LiteralPath (
        Join-Path $Directory 'render-pack-version.txt') -Value $Version
    [ordered]@{
        id = $packageId
        displayName = 'Connected external render-pack lifecycle fixture'
        version = $Version
        entryDll = [IO.Path]::GetFileName($fixture)
        apiVersion = 1
        kinds = @('renderPack')
    } | ConvertTo-Json -Depth 4 |
        Set-Content -Encoding utf8 -LiteralPath (Join-Path $Directory 'plugin.json')
}

function Install-Package {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string]$Version
    )
    $null = New-Item -ItemType Directory -Path $Directory
    Assert-ConnectedGateNoReparsePoint $Directory
    Copy-Item -LiteralPath $fixture -Destination (
        Join-Path $Directory ([IO.Path]::GetFileName($fixture)))
    Write-PackageVersion -Directory $Directory -Version $Version
}

function Read-StatusEvents {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "session status stream is missing: '$Path'"
    }
    return @(Get-Content -LiteralPath $Path | ForEach-Object {
        $_ | ConvertFrom-Json
    })
}

function Assert-SingleStatusEvent {
    param(
        [Parameter(Mandatory = $true)][object[]]$Events,
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$Plugin
    )
    $matching = @($Events | Where-Object {
        $_.e -ceq $Name -and (
            -not $PSBoundParameters.ContainsKey('Plugin') -or
            $_.plugin -ceq $Plugin)
    })
    if ($matching.Count -ne 1) {
        throw "expected one '$Name' status event, observed $($matching.Count)"
    }
    return $matching[0]
}

function Invoke-Scenario {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][int]$ExpectedState,
        [Parameter(Mandatory = $true)][string]$ExpectedPackId,
        [AllowNull()][string]$ExpectedPackVersion,
        [Parameter(Mandatory = $true)][string]$ExpectedPreset,
        [Parameter(Mandatory = $true)]
        [ValidateSet('pluginLoaded', 'pluginFailed')]
        [string]$ExpectedPluginEvent,
        [string]$ExpectedFailureFragment,
        [Parameter(Mandatory = $true)][string]$ExpectedPersistedPackId
    )
    Assert-ConnectedGateSafeLeafName $Name
    $scenarioRoot = Assert-ConnectedGateContainedPath $root (Join-Path $root $Name)
    $null = New-Item -ItemType Directory -Path $scenarioRoot
    Assert-ConnectedGateNoReparsePoint $scenarioRoot
    $stdout = Join-Path $scenarioRoot 'client.stdout.log'
    $stderr = Join-Path $scenarioRoot 'client.stderr.log'
    $status = Assert-ConnectedGateContainedPath $renderPackGate.StateDirectory (
        Join-Path $renderPackGate.StateDirectory "status\$Name.jsonl")
    $sessionConfigPath = New-ConnectedGraphicalSessionConfig `
        -State $renderPackGate `
        -Account $Account `
        -Plugins @($packageId) `
        -StatusFile $status
    $client = $null
    try {
        $env:ACDREAM_AUTOMATION_ARTIFACT_DIR = $scenarioRoot
        $client = Start-Process -FilePath $exe -WorkingDirectory $Repository `
            -ArgumentList @('--session-config', ('"' + $sessionConfigPath + '"')) `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
        Wait-ForLogPattern $client $stdout 'live: in world' $LoginTimeoutSeconds
        Wait-ForLogPattern $client $stdout `
            '[world-gate] screenshot-complete name=package-lifecycle' 120
        Wait-ForLogPattern $client $stdout `
            '[world-gate] checkpoint name=package-lifecycle' 30
        Close-ClientGracefully $client
        if ($client.ExitCode -ne 0) {
            throw "client exited with code $($client.ExitCode)"
        }

        $metadataPath = Join-Path $scenarioRoot `
            'screenshots\package-lifecycle.metadata.json'
        if (-not (Test-Path -LiteralPath $metadataPath)) {
            throw "render-pack metadata is missing: '$metadataPath'"
        }
        $metadata = Get-Content -Raw -LiteralPath $metadataPath | ConvertFrom-Json
        $actual = $metadata.RenderPack
        if ([int]$metadata.SchemaVersion -ne 1) {
            throw "metadata schema was $($metadata.SchemaVersion), expected 1"
        }
        foreach ($comparison in @(
            @('State', [int]$ExpectedState, [int]$actual.State),
            @('PackId', $ExpectedPackId, [string]$actual.PackId),
            @('PackVersion', [string]$ExpectedPackVersion, [string]$actual.PackVersion),
            @('PresetId', $ExpectedPreset, [string]$actual.PresetId))) {
            if ($comparison[1] -cne $comparison[2]) {
                throw "$($comparison[0]) '$($comparison[2])', expected '$($comparison[1])'"
            }
        }
        if ([string]::IsNullOrWhiteSpace($ExpectedFailureFragment)) {
            if (-not [string]::IsNullOrWhiteSpace([string]$actual.FailureReason)) {
                throw "unexpected render-pack failure: $($actual.FailureReason)"
            }
        }
        elseif ([string]$actual.FailureReason -notlike "*$ExpectedFailureFragment*") {
            throw "failure '$($actual.FailureReason)' did not contain '$ExpectedFailureFragment'"
        }

        $events = @(Read-StatusEvents $status)
        $pluginEvent = Assert-SingleStatusEvent `
            -Events $events -Name $ExpectedPluginEvent -Plugin $packageId
        foreach ($eventName in @('started', 'connected', 'characterList',
                'enteredWorld', 'disconnected', 'exited')) {
            $null = Assert-SingleStatusEvent -Events $events -Name $eventName
        }
        $exitEvent = Assert-SingleStatusEvent -Events $events -Name 'exited'
        if ([int]$exitEvent.code -ne 0) {
            throw "terminal status exit code was $($exitEvent.code)"
        }
        $persisted = (Get-Content -Raw -LiteralPath (
            Join-Path $renderPackGate.ConfigDirectory 'settings.json') |
                ConvertFrom-Json).display.renderPack
        if ([string]$persisted.packId -cne $ExpectedPersistedPackId) {
            throw "persisted pack '$($persisted.packId)', expected '$ExpectedPersistedPackId'"
        }

        $pluginErrorProperty = $pluginEvent.PSObject.Properties['error']
        $result = [pscustomobject][ordered]@{
            Name = $Name
            Passed = $true
            ProcessId = $client.Id
            ExitCode = $client.ExitCode
            PluginEvent = $pluginEvent.e
            PluginError = if ($null -eq $pluginErrorProperty) {
                $null
            }
            else {
                $pluginErrorProperty.Value
            }
            RenderPack = [pscustomobject][ordered]@{
                State = $actual.State
                PackId = $actual.PackId
                PackVersion = $actual.PackVersion
                PresetId = $actual.PresetId
                EffectiveQuality = $actual.EffectiveQuality
                FailureReason = $actual.FailureReason
                ActivationGeneration = $actual.ActivationGeneration
                RetainedGpuBytes = $actual.RetainedGpuBytes
                TransientGpuBytes = $actual.TransientGpuBytes
            }
            PersistedPackId = $persisted.packId
            StatusFile = $status
            MetadataFile = $metadataPath
        }
        $results.Add($result)
        Write-Output (
            "PASS scenario=$Name plugin=$($pluginEvent.e) " +
            "renderPack=$($actual.PackId) state=$($actual.State)")
    }
    finally {
        if ($null -ne $client) {
            $client.Refresh()
            if (-not $client.HasExited) {
                $closed = $client.CloseMainWindow()
                if (-not $closed -or -not $client.WaitForExit(10000)) {
                    Stop-Process -Id $client.Id -Force -ErrorAction SilentlyContinue
                }
            }
            $client.Dispose()
        }
        if ($null -ne $sessionConfigPath) {
            Remove-ConnectedGraphicalSessionConfig `
                -State $renderPackGate -Path $sessionConfigPath
            $sessionConfigPath = $null
        }
    }
}

if (@(Get-Process -Name AcDream.App -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'an AcDream.App client is already running; close it gracefully before the package gate'
}
if (@(Get-NetUDPEndpoint -LocalPort 9000 -ErrorAction SilentlyContinue).Count -eq 0) {
    throw 'local ACE is not listening on UDP port 9000'
}
if (-not (Test-Path -LiteralPath $route -PathType Leaf)) {
    throw "route file not found: '$route'"
}
if (-not $SkipBuild) {
    Write-Output 'Building Release solution...'
    & dotnet build (Join-Path $Repository 'AcDream.slnx') -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE" }
}
foreach ($required in @($exe, $fixture)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "required gate input not found: '$required'"
    }
}

$null = New-Item -ItemType Directory -Path $root
Assert-ConnectedGateNoReparsePoint $root
$renderPackGate = New-ConnectedRenderPackGateState -Root $root -Preset retail
try {
    $binaryIdentity = Get-ConnectedGateBinaryIdentity `
        -Repository $Repository -Executable $exe -SkipBuild:$SkipBuild
    $env:ACDREAM_DAT_DIR = "$env:USERPROFILE\Documents\Asheron's Call"
    $env:ACDREAM_LIVE = '1'
    $env:ACDREAM_TEST_HOST = '127.0.0.1'
    $env:ACDREAM_TEST_PORT = '9000'
    $env:ACDREAM_TEST_USER = $Account
    $env:ACDREAM_TEST_PASS = $Password
    $env:ACDREAM_RETAIL_UI = '1'
    $env:ACDREAM_DEVTOOLS = '0'
    $env:ACDREAM_UI_PROBE_DUMP = '0'
    $env:ACDREAM_UI_PROBE_SCRIPT = $route
    $env:ACDREAM_NO_AUDIO = '1'

    $packageDirectory = Assert-ConnectedGateContainedPath `
        $renderPackGate.DataDirectory (
            Join-Path $renderPackGate.DataDirectory "plugins\$packageId")
    $withdrawnDirectory = Assert-ConnectedGateContainedPath $root (
        Join-Path $root 'withdrawn-package')

    Install-Package -Directory $packageDirectory -Version '1.0.0'
    Write-Selection -Version '1.0.0' -Preset 'low'
    Invoke-Scenario -Name '01-installed-v1-selected' `
        -ExpectedState 2 -ExpectedPackId $packId -ExpectedPackVersion '1.0.0' `
        -ExpectedPreset 'low' -ExpectedPluginEvent pluginLoaded `
        -ExpectedPersistedPackId $packId

    Write-PackageVersion -Directory $packageDirectory -Version '2.0.0'
    Write-Selection -Version '1.0.0' -Preset 'low'
    Invoke-Scenario -Name '02-updated-v2-stale-selection' `
        -ExpectedState 3 -ExpectedPackId 'retail' -ExpectedPackVersion $null `
        -ExpectedPreset 'off' -ExpectedPluginEvent pluginLoaded `
        -ExpectedFailureFragment 'version 1.0.0 was selected, but version 2.0.0 is installed' `
        -ExpectedPersistedPackId 'retail'

    Write-Selection -Version '2.0.0' -Preset 'high'
    Invoke-Scenario -Name '03-updated-v2-selected' `
        -ExpectedState 2 -ExpectedPackId $packId -ExpectedPackVersion '2.0.0' `
        -ExpectedPreset 'high' -ExpectedPluginEvent pluginLoaded `
        -ExpectedPersistedPackId $packId

    Move-Item -LiteralPath $packageDirectory -Destination $withdrawnDirectory
    Write-Selection -Version '2.0.0' -Preset 'high'
    Invoke-Scenario -Name '04-package-removed' `
        -ExpectedState 3 -ExpectedPackId 'retail' -ExpectedPackVersion $null `
        -ExpectedPreset 'off' -ExpectedPluginEvent pluginFailed `
        -ExpectedFailureFragment 'is not installed' `
        -ExpectedPersistedPackId 'retail'

    Move-Item -LiteralPath $withdrawnDirectory -Destination $packageDirectory
    Set-Content -NoNewline -LiteralPath (
        Join-Path $packageDirectory 'throw-after-render-pack-register') -Value ''
    Write-Selection -Version '2.0.0' -Preset 'high'
    Invoke-Scenario -Name '05-registration-failed' `
        -ExpectedState 3 -ExpectedPackId 'retail' -ExpectedPackVersion $null `
        -ExpectedPreset 'off' -ExpectedPluginEvent pluginFailed `
        -ExpectedFailureFragment 'is not installed' `
        -ExpectedPersistedPackId 'retail'

    Remove-Item -LiteralPath (
        Join-Path $packageDirectory 'throw-after-render-pack-register')
    Write-Selection -Version '2.0.0' -Preset 'high'
    Invoke-Scenario -Name '06-corrected-and-recovered' `
        -ExpectedState 2 -ExpectedPackId $packId -ExpectedPackVersion '2.0.0' `
        -ExpectedPreset 'high' -ExpectedPluginEvent pluginLoaded `
        -ExpectedPersistedPackId $packId
}
catch {
    $failure = $_.Exception.ToString()
}
finally {
    if ($null -ne $sessionConfigPath -and $null -ne $renderPackGate) {
        Remove-ConnectedGraphicalSessionConfig `
            -State $renderPackGate -Path $sessionConfigPath
    }
    if ($null -ne $renderPackGate) {
        Restore-ConnectedRenderPackGateEnvironment $renderPackGate
    }
    $report = [pscustomobject][ordered]@{
        Passed = [string]::IsNullOrWhiteSpace($failure) -and $results.Count -eq 6
        StartedUtc = $startedUtc
        FinishedUtc = [DateTime]::UtcNow.ToString('O')
        SourceCommit = if ($null -eq $binaryIdentity) {
            $null
        } else { $binaryIdentity.SourceCommit }
        BinaryProductVersion = if ($null -eq $binaryIdentity) {
            $null
        } else { $binaryIdentity.BinaryProductVersion }
        BinaryCommit = if ($null -eq $binaryIdentity) {
            $null
        } else { $binaryIdentity.BinaryCommit }
        BinaryMatchesSource = if ($null -eq $binaryIdentity) {
            $false
        } else { $binaryIdentity.BinaryMatchesSource }
        TrackedSourceStatus = if ($null -eq $binaryIdentity) {
            @()
        } else { @($binaryIdentity.SourceTrackedStatus) }
        PackageId = $packageId
        RenderPackId = $packId
        ScenarioCount = $results.Count
        Scenarios = @($results)
        Failure = $failure
    }
    $reportPath = Join-Path $root 'report.json'
    $report | ConvertTo-Json -Depth 12 |
        Set-Content -Encoding utf8 -LiteralPath $reportPath
    Write-Output "REPORT=$reportPath"
    Write-Output "RESULT=$(if ($report.Passed) { 'PASS' } else { 'FAIL' })"
}

if (-not [string]::IsNullOrWhiteSpace($failure)) {
    throw $failure
}
if ($results.Count -ne 6) {
    throw "package lifecycle completed $($results.Count) of 6 scenarios"
}
