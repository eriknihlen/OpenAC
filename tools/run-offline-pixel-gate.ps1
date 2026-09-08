[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Baseline,
    [int]$WarmupMs = 12000,
    [int]$DayGroup = 0,
    [double]$WorldDayFraction = 0.5,
    [double]$SkyPhaseSeconds = 0,
    [int]$MsaaSamples = 0,
    [int]$Tolerance = 2,
    [double]$MaxDifferentFraction = 0.001,
    [int]$MaskTopPixels = 280,
    [ValidateSet('retail', 'low', 'medium', 'high', 'auto')]
    [string]$RenderPackPreset = 'retail',
    [ValidatePattern('^[1-9][0-9]*x[1-9][0-9]*$')]
    [string]$Resolution = '1280x720',
    [ValidateRange(0.0, 5000.0)]
    [double]$OrbitDistanceMeters = 0,
    [Nullable[double]]$OrbitYawDegrees,
    [ValidateRange(-89.0, 89.0)]
    [Nullable[double]]$OrbitPitchDegrees,
    [hashtable]$RenderPackSettingOverrides = @{},
    [ValidateRange(0, 2048)]
    [int]$RequiredRenderPackSamples = 0,
    [ValidateRange(1000, 600000)]
    [int]$RenderPackSampleTimeoutMs = 300000,
    [switch]$AllowSafeRenderPackFallback,
    [switch]$Uncapped,
    [bool]$BuildingDetailTextures = $true,
    [switch]$SkipBuild,
    [string]$ExeOverride,
    [string]$DebuggerScript
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo 'src\AcDream.App\bin\Release\net10.0\AcDream.App.exe'
$cli = Join-Path $repo 'src\AcDream.Cli\bin\Release\net10.0\AcDream.Cli.dll'
. (Join-Path $PSScriptRoot 'atmospheric-performance-matrix-common.ps1')

if ($ExeOverride) {
    if (-not (Test-Path $ExeOverride)) { throw "ExeOverride not found: $ExeOverride" }
    $exe = (Resolve-Path $ExeOverride).Path
    $SkipBuild = $true
}
if ($DebuggerScript -and -not (Test-Path $DebuggerScript)) {
    throw "DebuggerScript not found: $DebuggerScript"
}

if ($AllowSafeRenderPackFallback -and $RenderPackPreset -eq 'retail') {
    throw '-AllowSafeRenderPackFallback is valid only for an explicitly selected enhanced preset.'
}

function Write-Step($message) { Write-Host "[pixel-gate] $message" }

# --- 1. Build -----------------------------------------------------------------
if (-not $SkipBuild) {
    Write-Step 'building Release'
    & dotnet build (Join-Path $repo 'AcDream.slnx') -c Release --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }
}
if (-not (Test-Path $exe)) { throw "Client not found at $exe. Build Release first." }

if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
New-Item -ItemType Directory -Force -Path $Out | Out-Null

$state = Join-Path $Out 'isolated-state'
$config = Join-Path $state 'config'
$data = Join-Path $state 'data'
$cache = Join-Path $state 'cache'
New-Item -ItemType Directory -Force -Path $config, $data, $cache | Out-Null
$packId = if ($RenderPackPreset -eq 'retail') { 'retail' } else { 'acdream.atmospheric' }
$packVersion = if ($RenderPackPreset -eq 'retail') { $null } else { '1.0.0' }
$presetId = if ($RenderPackPreset -eq 'retail') { 'off' } else { $RenderPackPreset }
$orderedOverrides = [ordered]@{}
foreach ($key in @($RenderPackSettingOverrides.Keys | Sort-Object)) {
    $orderedOverrides[$key] = [string]$RenderPackSettingOverrides[$key]
}
$settings = [ordered]@{
    display = [ordered]@{
        resolution = $Resolution
        fullscreen = $false
        vsync = $false
        buildingDetailTextures = $BuildingDetailTextures
        renderPack = [ordered]@{
            packId = $packId
            packVersion = $packVersion
            presetId = $presetId
            settingOverrides = $orderedOverrides
        }
    }
    version = 3
}
$settings | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 `
    -LiteralPath (Join-Path $config 'settings.json')

$probe = Join-Path $Out 'offline.probe.txt'
$probeCommands = [Collections.Generic.List[string]]::new()
$probeCommands.Add("sleep $WarmupMs")
if ($RequiredRenderPackSamples -gt 0) {
    if ($RenderPackPreset -ne 'auto') {
        $probeCommands.Add('renderpack reset-performance')
    }
    $probeCommands.Add(
        "wait render-pack-samples $RequiredRenderPackSamples $RenderPackSampleTimeoutMs")
    if ($AllowSafeRenderPackFallback) {
        $probeCommands.Add('sleep 2000')
    }
}
$probeCommands.Add('screenshot world-offline 30000')
$probeCommands.Add('sleep 4000')
$probeCommands.Add('close-client')
Set-Content -Encoding utf8 -Path $probe -Value $probeCommands

$log = Join-Path $Out 'client.log'

# --- 3. Launch offline --------------------------------------------------------
$previousLive = $env:ACDREAM_LIVE
$previousConfigDirectory = $env:ACDREAM_CONFIG_DIR
$previousDataDirectory = $env:ACDREAM_DATA_DIR
$previousCacheDirectory = $env:ACDREAM_CACHE_DIR
$previousOrbitDistance = $env:ACDREAM_ORBIT_DISTANCE_METERS
$previousOrbitYaw = $env:ACDREAM_ORBIT_YAW_DEGREES
$previousOrbitPitch = $env:ACDREAM_ORBIT_PITCH_DEGREES
$previousUncappedRender = $env:ACDREAM_UNCAPPED_RENDER
$previousExactFramebuffer = $env:ACDREAM_AUTOMATION_EXACT_FRAMEBUFFER
Remove-Item Env:\ACDREAM_LIVE -ErrorAction SilentlyContinue
$env:ACDREAM_DAT_DIR = Join-Path $env:USERPROFILE "Documents\Asheron's Call"
$env:ACDREAM_CONFIG_DIR = $config
$env:ACDREAM_DATA_DIR = $data
$env:ACDREAM_CACHE_DIR = $cache
$env:ACDREAM_NO_AUDIO = '1'
$env:ACDREAM_RETAIL_UI = '1'
$env:ACDREAM_DAY_GROUP = "$DayGroup"
$env:ACDREAM_UI_PROBE_SCRIPT = $probe
$env:ACDREAM_AUTOMATION_ARTIFACT_DIR = $Out
$env:ACDREAM_AUTOMATION_EXACT_FRAMEBUFFER = '1'
$env:ACDREAM_UNCAPPED_RENDER = if ($Uncapped) { '1' } else { $null }
if ($OrbitDistanceMeters -gt 0) {
    $env:ACDREAM_ORBIT_DISTANCE_METERS = $OrbitDistanceMeters.ToString(
        [System.Globalization.CultureInfo]::InvariantCulture)
} else {
    Remove-Item Env:\ACDREAM_ORBIT_DISTANCE_METERS -ErrorAction SilentlyContinue
}
if ($null -ne $OrbitYawDegrees) {
    $env:ACDREAM_ORBIT_YAW_DEGREES = ([double]$OrbitYawDegrees).ToString(
        [System.Globalization.CultureInfo]::InvariantCulture)
} else {
    Remove-Item Env:\ACDREAM_ORBIT_YAW_DEGREES -ErrorAction SilentlyContinue
}
if ($null -ne $OrbitPitchDegrees) {
    $env:ACDREAM_ORBIT_PITCH_DEGREES = ([double]$OrbitPitchDegrees).ToString(
        [System.Globalization.CultureInfo]::InvariantCulture)
} else {
    Remove-Item Env:\ACDREAM_ORBIT_PITCH_DEGREES -ErrorAction SilentlyContinue
}

# The determinism pins, forced rather than inherited. See .DESCRIPTION.
$invariant = [System.Globalization.CultureInfo]::InvariantCulture
$env:ACDREAM_WORLD_TIME = $WorldDayFraction.ToString($invariant)
$env:ACDREAM_SKY_PHASE_SECONDS = $SkyPhaseSeconds.ToString($invariant)
if ($MsaaSamples -ge 0) { $env:ACDREAM_MSAA_SAMPLES = "$MsaaSamples" }
else { Remove-Item Env:\ACDREAM_MSAA_SAMPLES -ErrorAction SilentlyContinue }

Write-Step "launching offline client (pack $packId/$presetId, $Resolution, $(if ($Uncapped) { 'uncapped' } else { 'capped' }), orbit ${OrbitDistanceMeters}m/$OrbitYawDegrees deg yaw/$OrbitPitchDegrees deg pitch, warmup ${WarmupMs}ms, day group $DayGroup, day fraction $WorldDayFraction, sky phase $SkyPhaseSeconds, MSAA $MsaaSamples)"
$proc = if ($DebuggerScript) {
    $cdb = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\cdb.exe'
    if (-not (Test-Path $cdb)) { throw "cdb.exe not found at $cdb (install the Windows Debugging Tools)." }
    Start-Process -FilePath $cdb `
        -ArgumentList @('-G', '-o', '-lines', '-cf', (Resolve-Path $DebuggerScript).Path, $exe) `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru -WindowStyle Hidden
} else {
    Start-Process -FilePath $exe -RedirectStandardOutput $log `
        -RedirectStandardError "$log.err" -PassThru -WindowStyle Hidden
}

try {
    $shots = Join-Path $Out 'screenshots'
    $sampleWaitMs = if ($RequiredRenderPackSamples -gt 0) {
        $RenderPackSampleTimeoutMs
    } else { 0 }
    $deadline = (Get-Date).AddMilliseconds(
        $WarmupMs + $sampleWaitMs + 60000)
    $captured = $false
    while ((Get-Date) -lt $deadline) {
        if ((Test-Path $shots) -and (Get-ChildItem $shots -Filter *.png -ErrorAction SilentlyContinue)) {
            $captured = $true
            break
        }
        if ($proc.HasExited) { break }
        Start-Sleep -Milliseconds 1000
    }

    if (-not $captured) {
        Write-Host (Get-Content $log -Tail 30 -ErrorAction SilentlyContinue)
        throw 'No screenshot was captured before the deadline.'
    }
    # Let the probe script finish its trailing sleep so the PNG is fully flushed.
    Start-Sleep -Milliseconds 1500
    $proc.Refresh()
    [pscustomobject][ordered]@{
        SchemaVersion = 1
        CapturedUtc = [DateTime]::UtcNow.ToString('O')
        WorkingSetBytes = [long]$proc.WorkingSet64
        PrivateMemoryBytes = [long]$proc.PrivateMemorySize64
        HandleCount = [int]$proc.HandleCount
        ThreadCount = [int]$proc.Threads.Count
    } | ConvertTo-Json -Depth 3 | Set-Content -Encoding utf8 `
        -LiteralPath (Join-Path $Out 'capture-process.json')
}
finally {
    $shutdownFailure = $null
    $proc.Refresh()
    if (-not $proc.HasExited -and -not $proc.WaitForExit(15000)) {
        $shutdownFailure = 'in-process automation close timed out'
        Write-Step "$shutdownFailure; forcing exact capture process"
        Stop-Process -Id $proc.Id -Force
        $proc.WaitForExit()
    }
    if ($null -eq $shutdownFailure -and $proc.ExitCode -ne 0) {
        $shutdownFailure = "client exited with code $($proc.ExitCode)"
    }
    Remove-Item Env:\ACDREAM_MSAA_SAMPLES -ErrorAction SilentlyContinue
    Remove-Item Env:\ACDREAM_WORLD_TIME -ErrorAction SilentlyContinue
    Remove-Item Env:\ACDREAM_SKY_PHASE_SECONDS -ErrorAction SilentlyContinue
    if ($null -eq $previousOrbitDistance) {
        Remove-Item Env:\ACDREAM_ORBIT_DISTANCE_METERS -ErrorAction SilentlyContinue
    } else { $env:ACDREAM_ORBIT_DISTANCE_METERS = $previousOrbitDistance }
    if ($null -eq $previousOrbitYaw) {
        Remove-Item Env:\ACDREAM_ORBIT_YAW_DEGREES -ErrorAction SilentlyContinue
    } else { $env:ACDREAM_ORBIT_YAW_DEGREES = $previousOrbitYaw }
    if ($null -eq $previousOrbitPitch) {
        Remove-Item Env:\ACDREAM_ORBIT_PITCH_DEGREES -ErrorAction SilentlyContinue
    } else { $env:ACDREAM_ORBIT_PITCH_DEGREES = $previousOrbitPitch }
    if ($null -eq $previousUncappedRender) {
        Remove-Item Env:\ACDREAM_UNCAPPED_RENDER -ErrorAction SilentlyContinue
    } else { $env:ACDREAM_UNCAPPED_RENDER = $previousUncappedRender }
    if ($null -eq $previousExactFramebuffer) {
        Remove-Item Env:\ACDREAM_AUTOMATION_EXACT_FRAMEBUFFER -ErrorAction SilentlyContinue
    } else {
        $env:ACDREAM_AUTOMATION_EXACT_FRAMEBUFFER = $previousExactFramebuffer
    }
    if ($previousLive) { $env:ACDREAM_LIVE = $previousLive }
    if ($null -eq $previousConfigDirectory) {
        Remove-Item Env:\ACDREAM_CONFIG_DIR -ErrorAction SilentlyContinue
    } else { $env:ACDREAM_CONFIG_DIR = $previousConfigDirectory }
    if ($null -eq $previousDataDirectory) {
        Remove-Item Env:\ACDREAM_DATA_DIR -ErrorAction SilentlyContinue
    } else { $env:ACDREAM_DATA_DIR = $previousDataDirectory }
    if ($null -eq $previousCacheDirectory) {
        Remove-Item Env:\ACDREAM_CACHE_DIR -ErrorAction SilentlyContinue
    } else { $env:ACDREAM_CACHE_DIR = $previousCacheDirectory }
    if ($null -ne $shutdownFailure) {
        throw $shutdownFailure
    }
}

$captures = @(Get-ChildItem (Join-Path $Out 'screenshots') -Filter *.png)
Write-Step "captured $($captures.Count) screenshot(s) into $Out"
$metadataPath = Join-Path $Out 'screenshots\world-offline.metadata.json'
if (-not (Test-Path -LiteralPath $metadataPath)) {
    throw "Render-pack screenshot metadata is missing at '$metadataPath'."
}
$metadata = Get-Content -Raw -LiteralPath $metadataPath | ConvertFrom-Json
if ($AllowSafeRenderPackFallback) {
    $dimensions = $Resolution.Split('x')
    $evidence = Test-AtmosphericPerformanceMetadataEvidence `
        -MetadataPath $metadataPath `
        -Preset $RenderPackPreset `
        -ExpectedWidth ([int]$dimensions[0]) `
        -ExpectedHeight ([int]$dimensions[1]) `
        -AllowSafeFallback
    if (-not $evidence.Passed) {
        throw ('Capture is neither an active complete evidence window nor a safe ' +
            'resource/capability fallback: ' + (@($evidence.Failures) -join '; '))
    }
    Write-Step ("render-pack capture outcome: {0}{1}" -f `
        $evidence.Outcome,
        $(if ($evidence.Outcome -eq 'Unavailable') {
            " ($($evidence.UnavailableClassification): $($evidence.FailureReason))"
        } else { '' }))
}
else {
    if (($metadata.RenderPack.PackId -ne $packId) -or
        ($metadata.RenderPack.PresetId -ne $presetId)) {
        throw ("Capture selected {0}/{1}, expected {2}/{3}. Failure: {4}" -f `
            $metadata.RenderPack.PackId,
            $metadata.RenderPack.PresetId,
            $packId,
            $presetId,
            $metadata.RenderPack.FailureReason)
    }
    $expectedState = if ($RenderPackPreset -eq 'retail') { 0 } else { 2 }
    if ([int]$metadata.RenderPack.State -ne $expectedState) {
        throw ("Capture render-pack state was {0}, expected {1}. Failure: {2}" -f `
            $metadata.RenderPack.State,
            $expectedState,
            $metadata.RenderPack.FailureReason)
    }
}

$cameraInput = Select-String -Path $log -Pattern 'ScrollUp|ScrollDown|ZoomIn|ZoomOut|CameraZoom' `
    -CaseSensitive -ErrorAction SilentlyContinue
if ($cameraInput) {
    Write-Host ''
    Write-Host '[pixel-gate] ABORTED: camera-affecting input reached the capture window.' -ForegroundColor Red
    Write-Host '            The capture is not comparable. Re-run without touching the machine.' -ForegroundColor Red
    $cameraInput | Select-Object -First 5 | ForEach-Object { Write-Host "  $($_.Line)" -ForegroundColor Red }
    exit 2
}

if (-not $Baseline) {
    Write-Step 'no baseline supplied; capture only'
    exit 0
}

if (-not (Test-Path $cli)) { throw "AcDream.Cli not found at $cli." }

$maskPath = $null
if ($MaskTopPixels -gt 0) {
    Add-Type -AssemblyName System.Drawing
    $probeImage = [System.Drawing.Bitmap]::FromFile($captures[0].FullName)
    $width = $probeImage.Width
    $height = $probeImage.Height
    $probeImage.Dispose()

    $mask = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($mask)
    $graphics.Clear([System.Drawing.Color]::FromArgb(0, 0, 0, 0))
    $opaque = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 0, 255))
    $graphics.FillRectangle($opaque, 0, 0, $width, [Math]::Min($MaskTopPixels, $height))
    $graphics.Dispose()
    $opaque.Dispose()

    $maskPath = Join-Path $Out 'sky-mask.png'
    $mask.Save($maskPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $mask.Dispose()
    Write-Step "masking the top $MaskTopPixels rows (animated sky and treeline)"
}

$failed = @()
foreach ($shot in $captures) {
    $expected = Join-Path $Baseline "screenshots\$($shot.Name)"
    if (-not (Test-Path $expected)) {
        $failed += "$($shot.Name): no baseline counterpart"
        continue
    }

    $report = Join-Path $Out "compare-$($shot.BaseName).json"
    if ($maskPath) {
        & dotnet $cli compare-screenshots $expected $shot.FullName $report $Tolerance $MaxDifferentFraction $maskPath | Out-Null
    }
    else {
        & dotnet $cli compare-screenshots $expected $shot.FullName $report $Tolerance $MaxDifferentFraction | Out-Null
    }
    $verdict = Get-Content $report -Raw | ConvertFrom-Json

    $fraction = $verdict.differentPixelFraction
    if ($null -eq $fraction) { $fraction = $verdict.DifferentPixelFraction }
    $passed = $verdict.passed
    if ($null -eq $passed) { $passed = $verdict.Passed }

    if ($passed) {
        Write-Step "PASS $($shot.Name) (differing fraction $fraction)"
    }
    else {
        $failed += "$($shot.Name): differing fraction $fraction exceeds $MaxDifferentFraction (report: $report)"
    }
}

if ($failed.Count -gt 0) {
    Write-Host ''
    Write-Host '[pixel-gate] FAILED:' -ForegroundColor Red
    $failed | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Step 'all screenshots match the baseline'
exit 0
