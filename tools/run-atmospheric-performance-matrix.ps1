[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [ValidateSet('capped', 'uncapped', 'both')]
    [string]$FramePacing = 'both',
    [ValidateSet('retail', 'low', 'medium', 'high', 'auto')]
    [string[]]$PresetSet = @('retail', 'low', 'medium', 'high', 'auto'),
    [ValidateSet('1920x1080', '2560x1440', '3840x2160')]
    [string[]]$ResolutionSet = @('1920x1080', '2560x1440', '3840x2160'),
    [ValidateRange(45000, 600000)]
    [int]$WarmupMs = 45000,
    [int]$DayGroup = 0,
    [ValidateRange(0.0, 1.0)]
    [double]$WorldDayFraction = 0.5,
    [double]$SkyPhaseSeconds = 0,
    [ValidateRange(0.0, 5000.0)]
    [double]$OrbitDistanceMeters = 0,
    [Nullable[double]]$OrbitYawDegrees,
    [ValidateRange(-89.0, 89.0)]
    [Nullable[double]]$OrbitPitchDegrees,
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
. (Join-Path $PSScriptRoot 'atmospheric-performance-matrix-common.ps1')
. (Join-Path $PSScriptRoot 'connected-render-pack-gate-common.ps1')

$repo = Split-Path -Parent $PSScriptRoot
$pixelGate = Join-Path $PSScriptRoot 'run-offline-pixel-gate.ps1'
$solution = Join-Path $repo 'AcDream.slnx'
$cli = Join-Path $repo 'src\AcDream.Cli\bin\Release\net10.0\AcDream.Cli.dll'
$outputRoot = [IO.Path]::GetFullPath($Out)
$jsonPath = Join-Path $outputRoot 'atmospheric-performance-matrix.json'
$markdownPath = Join-Path $outputRoot 'atmospheric-performance-matrix.md'
$presets = @($PresetSet | ForEach-Object { $_.ToLowerInvariant() } | Select-Object -Unique)
$screenshotLeaf = 'world-offline'
$resolutions = @($ResolutionSet | Select-Object -Unique)
$pacingModes = switch ($FramePacing) {
    'capped' { @('capped') }
    'uncapped' { @('uncapped') }
    default { @('capped', 'uncapped') }
}

$budgets = @{
    low = [pscustomobject][ordered]@{
        IncrementalCpuMillisecondsP50 = 0.15
        IncrementalCpuMillisecondsP99 = 0.50
        InclusiveGpuMillisecondsP50At1080p = 2.00
        InclusiveGpuMillisecondsP99At1080p = 3.00
        ResidentGpuBytes = 64L * 1024L * 1024L
    }
    medium = [pscustomobject][ordered]@{
        IncrementalCpuMillisecondsP50 = 0.25
        IncrementalCpuMillisecondsP99 = 0.75
        InclusiveGpuMillisecondsP50At1080p = 3.25
        InclusiveGpuMillisecondsP99At1080p = 4.50
        ResidentGpuBytes = 128L * 1024L * 1024L
    }
    high = [pscustomobject][ordered]@{
        IncrementalCpuMillisecondsP50 = 0.35
        IncrementalCpuMillisecondsP99 = 1.00
        InclusiveGpuMillisecondsP50At1080p = 4.50
        InclusiveGpuMillisecondsP99At1080p = 6.00
        ResidentGpuBytes = 256L * 1024L * 1024L
    }
}

function Get-RequiredProperty {
    param(
        [Parameter(Mandatory = $true)][object]$Value,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Context
    )

    $property = $Value.PSObject.Properties[$Name]
    if ($null -eq $property) {
        throw "$Context is missing required property '$Name'."
    }
    return $property.Value
}

function Format-Invariant([double]$Value, [string]$Format = '0.###') {
    return $Value.ToString($Format, [Globalization.CultureInfo]::InvariantCulture)
}

function Add-Failure(
    [Collections.Generic.List[string]]$RowFailures,
    [string]$Message)
{
    $null = $RowFailures.Add($Message)
}

function Assert-MatrixContainedPath([string]$Root, [string]$Path) {
    return Assert-ConnectedGateContainedPath $Root $Path
}

function Compare-MatrixFallbackFramebuffer {
    param(
        [Parameter(Mandatory = $true)][string]$Expected,
        [Parameter(Mandatory = $true)][string]$Actual,
        [Parameter(Mandatory = $true)][string]$ReportPath,
        [Parameter(Mandatory = $true)][string]$MaskPath)

    Add-Type -AssemblyName System.Drawing
    $image = [System.Drawing.Bitmap]::FromFile($Actual)
    try {
        $mask = [System.Drawing.Bitmap]::new(
            $image.Width,
            $image.Height,
            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($mask)
            try {
                $graphics.Clear([System.Drawing.Color]::FromArgb(0, 0, 0, 0))
                $opaque = [System.Drawing.SolidBrush]::new(
                    [System.Drawing.Color]::FromArgb(255, 255, 0, 255))
                try {
                    $graphics.FillRectangle(
                        $opaque,
                        0,
                        0,
                        $image.Width,
                        [Math]::Min(280, $image.Height))
                }
                finally { $opaque.Dispose() }
            }
            finally { $graphics.Dispose() }
            $mask.Save($MaskPath, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally { $mask.Dispose() }
    }
    finally { $image.Dispose() }

    & dotnet $cli compare-screenshots `
        $Expected $Actual $ReportPath 2 0.001 $MaskPath | Out-Null
    $compareExitCode = $LASTEXITCODE
    if (-not (Test-Path -LiteralPath $ReportPath -PathType Leaf)) {
        return [pscustomobject][ordered]@{
            Passed = $false
            DifferentPixelFraction = $null
            ExitCode = $compareExitCode
            ReportPath = $ReportPath
            Failure = 'fallback/default screenshot comparison produced no report'
        }
    }
    $verdict = Get-Content -Raw -LiteralPath $ReportPath | ConvertFrom-Json
    $passedProperty = $verdict.PSObject.Properties['passed']
    if ($null -eq $passedProperty) { $passedProperty = $verdict.PSObject.Properties['Passed'] }
    $fractionProperty = $verdict.PSObject.Properties['differentPixelFraction']
    if ($null -eq $fractionProperty) {
        $fractionProperty = $verdict.PSObject.Properties['DifferentPixelFraction']
    }
    $passed = $null -ne $passedProperty -and [bool]$passedProperty.Value
    return [pscustomobject][ordered]@{
        Passed = $passed
        DifferentPixelFraction = if ($null -eq $fractionProperty) {
            $null
        } else { [double]$fractionProperty.Value }
        ExitCode = $compareExitCode
        ReportPath = $ReportPath
        Failure = if ($passed) {
            $null
        } else { 'safe fallback framebuffer differs from its paired default beyond tolerance 2 / 0.001' }
    }
}

if (-not (Test-Path -LiteralPath $pixelGate)) {
    throw "Offline pixel gate not found at '$pixelGate'."
}
if (Test-Path -LiteralPath $outputRoot) {
    throw "Output directory already exists: '$outputRoot'. Choose a new directory."
}
$outputParent = Split-Path -Parent $outputRoot
if (-not (Test-Path -LiteralPath $outputParent -PathType Container)) {
    throw "Output parent directory does not exist: '$outputParent'."
}
Assert-ConnectedGateNoReparsePoint $outputParent
Assert-ConnectedGateSafeLeafName ([IO.Path]::GetFileName($outputRoot))
if (@(Get-Process -Name AcDream.App -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'An AcDream.App client is already running; close it gracefully before the matrix.'
}

if (-not $SkipBuild) {
    & dotnet build $solution -c Release --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Release build failed with exit code $LASTEXITCODE."
    }
}

$exe = Join-Path $repo 'src\AcDream.App\bin\Release\net10.0\AcDream.App.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Client executable not found: '$exe'."
}
$binaryIdentity = Get-ConnectedGateBinaryIdentity `
    -Repository $repo -Executable $exe -SkipBuild:$SkipBuild
$null = New-Item -ItemType Directory -Path $outputRoot
Assert-ConnectedGateNoReparsePoint $outputRoot
$sourceCommit = $binaryIdentity.SourceCommit
$sourceStatus = @($binaryIdentity.SourceTrackedStatus)
$powershellExecutable = (Get-Process -Id $PID).Path
$rows = [Collections.Generic.List[object]]::new()
$matrixFailures = [Collections.Generic.List[string]]::new()
$startedUtc = [DateTime]::UtcNow
$expectedCasterCount = $null
$expectedAdapterIdentity = $null

foreach ($pacing in $pacingModes) {
    foreach ($resolution in $resolutions) {
        $dimensions = $resolution.Split('x')
        $expectedWidth = [int]$dimensions[0]
        $expectedHeight = [int]$dimensions[1]

        foreach ($preset in $presets) {
            $rowId = "$pacing-$preset-$($resolution.Replace('x', 'x'))"
            Assert-ConnectedGateSafeLeafName $rowId
            $rowDirectory = Assert-MatrixContainedPath $outputRoot (Join-Path $outputRoot $rowId)
            if (Test-Path -LiteralPath $rowDirectory) {
                throw "Matrix row directory is not fresh: '$rowDirectory'."
            }
            $metadataPath = Assert-MatrixContainedPath $rowDirectory (
                Join-Path $rowDirectory "screenshots\$screenshotLeaf.metadata.json")
            $rowFailures = [Collections.Generic.List[string]]::new()
            $captureExitCode = -1
            $metadata = $null
            $pack = $null
            $performance = $null
            $cpuSampleCount = $null
            $absoluteReceiverCpuSampleCount = $null
            $gpuSampleCount = $null
            $cpuP50 = $null
            $cpuP95 = $null
            $cpuP99 = $null
            $absoluteReceiverCpuP50 = $null
            $absoluteReceiverCpuP95 = $null
            $absoluteReceiverCpuP99 = $null
            $gpuP50 = $null
            $gpuP95 = $null
            $gpuP99 = $null
            $residentGpuBytes = $null
            $transientGpuBytes = $null
            $casterCount = $null
            $cascadeCount = $null
            $drawCalls = $null
            $dispatchCalls = $null
            $imageCount = $null
            $bufferCount = $null
            $effectiveQuality = $null
            $activationState = $null
            $failureReason = $null
            $availability = if ($preset -eq 'retail') { 'Retail' } else { 'Unknown' }
            $unavailableClassification = $null
            $metadataSchemaVersion = $null
            $packVersion = $null
            $activationGeneration = $null
            $topResidentGpuBytes = $null
            $topTransientGpuBytes = $null
            $classificationCalls = $null
            $passIds = @()
            $framebufferSha256 = $null
            $processEvidence = $null
            $adapterEvidence = $null
            $budget = if ($preset -in @('retail', 'auto')) {
                $null
            } else { $budgets[$preset] }
            $gpuBudgetApplies = $false

            try {
                $arguments = @(
                    '-NoProfile',
                    '-File', $pixelGate,
                    '-Out', $rowDirectory,
                    '-WarmupMs', "$WarmupMs",
                    '-DayGroup', "$DayGroup",
                    '-WorldDayFraction', (Format-Invariant $WorldDayFraction '0.################'),
                    '-SkyPhaseSeconds', (Format-Invariant $SkyPhaseSeconds '0.################'),
                    '-MsaaSamples', '0',
                    '-RenderPackPreset', $preset,
                    '-Resolution', $resolution,
                    '-OrbitDistanceMeters', (Format-Invariant $OrbitDistanceMeters '0.################'),
                    '-SkipBuild')
                if ($preset -ne 'retail') {
                    $arguments += @(
                        '-RequiredRenderPackSamples', '2048',
                        '-RenderPackSampleTimeoutMs', '300000',
                        '-AllowSafeRenderPackFallback')
                }
                if ($null -ne $OrbitYawDegrees) {
                    $arguments += @(
                        '-OrbitYawDegrees',
                        (Format-Invariant ([double]$OrbitYawDegrees) '0.################'))
                }
                if ($null -ne $OrbitPitchDegrees) {
                    $arguments += @(
                        '-OrbitPitchDegrees',
                        (Format-Invariant ([double]$OrbitPitchDegrees) '0.################'))
                }
                if ($pacing -eq 'uncapped') {
                    $arguments += '-Uncapped'
                }

                & $powershellExecutable @arguments
                $captureExitCode = $LASTEXITCODE
                if ($captureExitCode -ne 0) {
                    Add-Failure $rowFailures (
                        "offline capture exited with code $captureExitCode")
                }
                if (-not (Test-Path -LiteralPath $metadataPath)) {
                    throw "screenshot metadata is missing at '$metadataPath'."
                }
                foreach ($capturedPath in @(
                    $rowDirectory,
                    (Join-Path $rowDirectory 'screenshots'),
                    (Join-Path $rowDirectory 'isolated-state'),
                    (Join-Path $rowDirectory 'isolated-state\cache'))) {
                    Assert-ConnectedGateNoReparsePoint $capturedPath
                }

                $metadata = Get-Content -Raw -LiteralPath $metadataPath | ConvertFrom-Json
                $metadataSchemaVersion = [int](Get-RequiredProperty $metadata 'SchemaVersion' 'metadata')
                $strictEvidence = Test-AtmosphericPerformanceMetadataEvidence `
                    -MetadataPath $metadataPath -Preset $preset `
                    -ExpectedWidth $expectedWidth -ExpectedHeight $expectedHeight `
                    -AllowSafeFallback:($preset -ne 'retail')
                foreach ($strictFailure in @($strictEvidence.Failures)) {
                    Add-Failure $rowFailures $strictFailure
                }
                $availability = [string]$strictEvidence.Outcome
                $unavailableClassification = $strictEvidence.UnavailableClassification
                $classificationCalls = $strictEvidence.CpuClassificationCalls
                $passIds = @($strictEvidence.PassIds)
                $actualWidth = [int](Get-RequiredProperty $metadata 'Width' 'metadata')
                $actualHeight = [int](Get-RequiredProperty $metadata 'Height' 'metadata')
                if ($actualWidth -ne $expectedWidth -or $actualHeight -ne $expectedHeight) {
                    Add-Failure $rowFailures (
                        "capture extent was ${actualWidth}x${actualHeight}, expected $resolution")
                }

                $pack = Get-RequiredProperty $metadata 'RenderPack' 'metadata'
                $activationState = [int](Get-RequiredProperty $pack 'State' 'RenderPack')
                $actualPackId = [string](Get-RequiredProperty $pack 'PackId' 'RenderPack')
                $actualPresetId = [string](Get-RequiredProperty $pack 'PresetId' 'RenderPack')
                $packVersion = [string](Get-RequiredProperty $pack 'PackVersion' 'RenderPack')
                $activationGeneration = [int](Get-RequiredProperty $pack 'ActivationGeneration' 'RenderPack')
                $topResidentGpuBytes = [long](Get-RequiredProperty $pack 'RetainedGpuBytes' 'RenderPack')
                $topTransientGpuBytes = [long](Get-RequiredProperty $pack 'TransientGpuBytes' 'RenderPack')
                $effectiveQuality = [string](
                    Get-RequiredProperty $pack 'EffectiveQuality' 'RenderPack')
                if ($availability -eq 'Active' -and $preset -eq 'auto') {
                    $budget = $budgets[$effectiveQuality]
                    if ($null -eq $budget) {
                        Add-Failure $rowFailures (
                            "Automatic resolved unknown effective quality '$effectiveQuality'")
                    }
                }
                $gpuBudgetApplies = $availability -eq 'Active' -and
                    $resolution -eq '1920x1080'
                $failureReason = Get-RequiredProperty $pack 'FailureReason' 'RenderPack'
                $expectedPackId = if ($preset -eq 'retail' -or $availability -eq 'Unavailable') {
                    'retail'
                }
                else {
                    'acdream.atmospheric'
                }
                $expectedPresetId = if ($preset -eq 'retail' -or $availability -eq 'Unavailable') {
                    'off'
                } else { $preset }
                $expectedState = if ($preset -eq 'retail') {
                    0
                } elseif ($availability -eq 'Unavailable') {
                    3
                } else { 2 }
                if ($actualPackId -cne $expectedPackId) {
                    Add-Failure $rowFailures (
                        "pack was '$actualPackId', expected '$expectedPackId'")
                }
                if ($actualPresetId -cne $expectedPresetId) {
                    Add-Failure $rowFailures (
                        "preset was '$actualPresetId', expected '$expectedPresetId'")
                }
                if ($activationState -ne $expectedState) {
                    Add-Failure $rowFailures (
                        "activation state was $activationState, expected $expectedState")
                }
                if ($availability -ne 'Unavailable' -and
                    -not [string]::IsNullOrWhiteSpace([string]$failureReason)) {
                    Add-Failure $rowFailures "render-pack failure: $failureReason"
                }

                $casterCount = [int](Get-RequiredProperty $pack 'ShadowCasterCount' 'RenderPack')
                $cascadeCount = [int](Get-RequiredProperty $pack 'CascadeDrawCount' 'RenderPack')
                $drawCalls = [int](Get-RequiredProperty $pack 'DrawCalls' 'RenderPack')
                $dispatchCalls = [int](Get-RequiredProperty $pack 'DispatchCalls' 'RenderPack')
                $imageCount = [int](Get-RequiredProperty $pack 'ImageCount' 'RenderPack')
                $bufferCount = [int](Get-RequiredProperty $pack 'BufferCount' 'RenderPack')
                $performance = Get-RequiredProperty $pack 'Performance' 'RenderPack'

                $cpuSampleCount = [int](Get-RequiredProperty `
                    $performance 'CpuSampleCount' 'RenderPack.Performance')
                $absoluteReceiverCpuSampleCount = [int](Get-RequiredProperty `
                    $performance 'AbsoluteReceiverCpuSampleCount' 'RenderPack.Performance')
                $gpuSampleCount = [int](Get-RequiredProperty `
                    $performance 'GpuSampleCount' 'RenderPack.Performance')
                $cpuP50 = [double](Get-RequiredProperty `
                    $performance 'IncrementalCpuMillisecondsP50' 'RenderPack.Performance')
                $cpuP95 = [double](Get-RequiredProperty `
                    $performance 'IncrementalCpuMillisecondsP95' 'RenderPack.Performance')
                $cpuP99 = [double](Get-RequiredProperty `
                    $performance 'IncrementalCpuMillisecondsP99' 'RenderPack.Performance')
                $absoluteReceiverCpuP50 = [double](Get-RequiredProperty `
                    $performance 'AbsoluteReceiverCpuMillisecondsP50' 'RenderPack.Performance')
                $absoluteReceiverCpuP95 = [double](Get-RequiredProperty `
                    $performance 'AbsoluteReceiverCpuMillisecondsP95' 'RenderPack.Performance')
                $absoluteReceiverCpuP99 = [double](Get-RequiredProperty `
                    $performance 'AbsoluteReceiverCpuMillisecondsP99' 'RenderPack.Performance')
                $gpuP50 = [double](Get-RequiredProperty `
                    $performance 'InclusiveGpuMillisecondsP50' 'RenderPack.Performance')
                $gpuP95 = [double](Get-RequiredProperty `
                    $performance 'InclusiveGpuMillisecondsP95' 'RenderPack.Performance')
                $gpuP99 = [double](Get-RequiredProperty `
                    $performance 'InclusiveGpuMillisecondsP99' 'RenderPack.Performance')
                $residentGpuBytes = [long](Get-RequiredProperty `
                    $performance 'ResidentGpuBytes' 'RenderPack.Performance')
                $transientGpuBytes = [long](Get-RequiredProperty `
                    $performance 'TransientGpuBytes' 'RenderPack.Performance')

                if ($availability -eq 'Active') {
                    if ($null -eq $expectedCasterCount) { $expectedCasterCount = $casterCount }
                    elseif ($casterCount -ne $expectedCasterCount) {
                        Add-Failure $rowFailures (
                            "shadow caster membership $casterCount differs from matrix oracle $expectedCasterCount")
                    }
                }

                $pngPath = Assert-MatrixContainedPath $rowDirectory (
                    Join-Path $rowDirectory "screenshots\$screenshotLeaf.png")
                $framebufferSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $pngPath).Hash.ToLowerInvariant()
                $processPath = Assert-MatrixContainedPath $rowDirectory (
                    Join-Path $rowDirectory 'capture-process.json')
                $processEvidence = Get-Content -Raw -LiteralPath $processPath | ConvertFrom-Json
                if ([int]$processEvidence.SchemaVersion -ne 1 -or
                    [long]$processEvidence.WorkingSetBytes -le 0 -or
                    [long]$processEvidence.PrivateMemoryBytes -le 0) {
                    Add-Failure $rowFailures 'process-memory evidence is missing or invalid'
                }
                $capabilityPath = Assert-MatrixContainedPath $rowDirectory (
                    Join-Path $rowDirectory 'isolated-state\cache\diagnostics\graphical-capabilities-vulkan.json')
                $capability = Get-Content -Raw -LiteralPath $capabilityPath | ConvertFrom-Json
                $adapterEvidence = [pscustomobject][ordered]@{
                    DeviceName = [string]$capability.DeviceName
                    DriverInfo = [string]$capability.DriverInfo
                    DeviceType = [string]$capability.DeviceType
                    SelectedDeviceIndex = [int]$capability.SelectedDeviceIndex
                    DeviceApiVersion = [string]$capability.DeviceApiVersion
                }
                $adapterIdentity = "$($adapterEvidence.DeviceName)|$($adapterEvidence.DriverInfo)|$($adapterEvidence.SelectedDeviceIndex)"
                if ([string]::IsNullOrWhiteSpace($adapterEvidence.DeviceName)) {
                    Add-Failure $rowFailures 'Vulkan adapter identity is missing'
                }
                elseif ($null -eq $expectedAdapterIdentity) { $expectedAdapterIdentity = $adapterIdentity }
                elseif ($adapterIdentity -cne $expectedAdapterIdentity) {
                    Add-Failure $rowFailures 'Vulkan adapter identity changed between matrix rows'
                }

                if ($preset -eq 'retail') {
                    if ($imageCount -ne 0 -or $bufferCount -ne 0 -or
                        $drawCalls -ne 0 -or $dispatchCalls -ne 0 -or
                        $casterCount -ne 0 -or $cascadeCount -ne 0 -or
                        $residentGpuBytes -ne 0 -or $transientGpuBytes -ne 0) {
                        Add-Failure $rowFailures (
                            'retail row activated pack resources, submissions, casters, or memory')
                    }
                }
                elseif ($availability -eq 'Unavailable') {
                }
                else {
                    if ($cpuSampleCount -le 0 -or $gpuSampleCount -le 0) {
                        Add-Failure $rowFailures (
                            "enhanced row has insufficient samples: cpu=$cpuSampleCount gpu=$gpuSampleCount")
                    }
                    if (-not [double]::IsFinite($cpuP50) -or $cpuP50 -lt 0 -or
                        -not [double]::IsFinite($cpuP99) -or $cpuP99 -lt 0 -or
                        -not [double]::IsFinite($gpuP50) -or $gpuP50 -lt 0 -or
                        -not [double]::IsFinite($gpuP99) -or $gpuP99 -lt 0 -or
                        $residentGpuBytes -lt 0) {
                        Add-Failure $rowFailures 'performance measurements are non-finite or negative'
                    }
                    if ($cpuP50 -gt $budget.IncrementalCpuMillisecondsP50) {
                        Add-Failure $rowFailures (
                            "incremental CPU p50 $(Format-Invariant $cpuP50) ms exceeds " +
                            "$(Format-Invariant $budget.IncrementalCpuMillisecondsP50) ms")
                    }
                    if ($cpuP99 -gt $budget.IncrementalCpuMillisecondsP99) {
                        Add-Failure $rowFailures (
                            "incremental CPU p99 $(Format-Invariant $cpuP99) ms exceeds " +
                            "$(Format-Invariant $budget.IncrementalCpuMillisecondsP99) ms")
                    }
                    $rowPixels = [long]$dimensions[0] * [long]$dimensions[1]
                    $referencePixels = 1920L * 1080L
                    $residentCeiling = if ($rowPixels -le $referencePixels) {
                        [long]$budget.ResidentGpuBytes
                    } else {
                        [long][Math]::Ceiling([double]$budget.ResidentGpuBytes * $rowPixels / $referencePixels)
                    }
                    if ($residentGpuBytes -gt $residentCeiling) {
                        Add-Failure $rowFailures (
                            "resident GPU bytes $residentGpuBytes exceed $residentCeiling " +
                            "(declared $($budget.ResidentGpuBytes) at 1080p, scaled to ${resolution})")
                    }
                    if ($gpuBudgetApplies -and
                        $gpuP50 -gt $budget.InclusiveGpuMillisecondsP50At1080p) {
                        Add-Failure $rowFailures (
                            "inclusive GPU p50 $(Format-Invariant $gpuP50) ms exceeds the 1080p " +
                            "ceiling $(Format-Invariant $budget.InclusiveGpuMillisecondsP50At1080p) ms")
                    }
                    if ($gpuBudgetApplies -and
                        $gpuP99 -gt $budget.InclusiveGpuMillisecondsP99At1080p) {
                        Add-Failure $rowFailures (
                            "inclusive GPU p99 $(Format-Invariant $gpuP99) ms exceeds the 1080p " +
                            "ceiling $(Format-Invariant $budget.InclusiveGpuMillisecondsP99At1080p) ms")
                    }
                }
            }
            catch {
                Add-Failure $rowFailures $_.Exception.Message
            }

            $passed = $rowFailures.Count -eq 0
            $row = [pscustomobject][ordered]@{
                Id = $rowId
                FramePacing = $pacing
                Preset = $preset
                Resolution = $resolution
                CaptureDirectory = $rowDirectory
                MetadataPath = $metadataPath
                CaptureExitCode = $captureExitCode
                Passed = $passed
                Failures = @($rowFailures)
                Activation = [pscustomobject][ordered]@{
                    MetadataSchemaVersion = $metadataSchemaVersion
                    PackVersion = $packVersion
                    State = $activationState
                    Generation = $activationGeneration
                    EffectiveQuality = $effectiveQuality
                    FailureReason = $failureReason
                    Availability = $availability
                    UnavailableClassification = $unavailableClassification
                }
                FramebufferSha256 = $framebufferSha256
                PairedDefaultFramebufferSha256 = $null
                PairedDefaultComparison = $null
                Process = $processEvidence
                Adapter = $adapterEvidence
                Samples = [pscustomobject][ordered]@{
                    IncrementalCpu = $cpuSampleCount
                    AbsoluteReceiverCpu = $absoluteReceiverCpuSampleCount
                    InclusiveGpu = $gpuSampleCount
                }
                PerformanceMilliseconds = [pscustomobject][ordered]@{
                    IncrementalCpuP50 = $cpuP50
                    IncrementalCpuP95 = $cpuP95
                    IncrementalCpuP99 = $cpuP99
                    AbsoluteReceiverCpuP50 = $absoluteReceiverCpuP50
                    AbsoluteReceiverCpuP95 = $absoluteReceiverCpuP95
                    AbsoluteReceiverCpuP99 = $absoluteReceiverCpuP99
                    InclusiveGpuP50 = $gpuP50
                    InclusiveGpuP95 = $gpuP95
                    InclusiveGpuP99 = $gpuP99
                }
                Resources = [pscustomobject][ordered]@{
                    TopLevelResidentGpuBytes = $topResidentGpuBytes
                    TopLevelTransientGpuBytes = $topTransientGpuBytes
                    ResidentGpuBytes = $residentGpuBytes
                    TransientGpuBytes = $transientGpuBytes
                    Images = $imageCount
                    Buffers = $bufferCount
                }
                Work = [pscustomobject][ordered]@{
                    ShadowCasters = $casterCount
                    CascadeDraws = $cascadeCount
                    DrawCalls = $drawCalls
                    DispatchCalls = $dispatchCalls
                    CpuClassificationCalls = $classificationCalls
                    PassIds = @($passIds)
                }
                Budget = if ($null -eq $budget) {
                    $null
                }
                else {
                    [pscustomobject][ordered]@{
                        IncrementalCpuMillisecondsP50 =
                            $budget.IncrementalCpuMillisecondsP50
                        IncrementalCpuMillisecondsP99 =
                            $budget.IncrementalCpuMillisecondsP99
                        InclusiveGpuMillisecondsP50At1080p =
                            $budget.InclusiveGpuMillisecondsP50At1080p
                        InclusiveGpuMillisecondsP99At1080p =
                            $budget.InclusiveGpuMillisecondsP99At1080p
                        ResidentGpuBytes = $budget.ResidentGpuBytes
                        GpuBudgetApplies = $gpuBudgetApplies
                    }
                }
            }
            $null = $rows.Add($row)
            foreach ($failure in $rowFailures) {
                $null = $matrixFailures.Add("${rowId}: $failure")
            }
        }
    }
}

foreach ($row in @($rows | Where-Object { $_.Preset -ne 'retail' })) {
    $defaultRow = @($rows | Where-Object {
        $_.Preset -eq 'retail' -and
        $_.FramePacing -eq $row.FramePacing -and
        $_.Resolution -eq $row.Resolution
    })
    if ($defaultRow.Count -ne 1 -or
        [string]::IsNullOrWhiteSpace([string]$defaultRow[0].FramebufferSha256)) {
        $message = "$($row.Id): paired default framebuffer digest is unavailable"
        $null = $matrixFailures.Add($message)
        $row.Passed = $false
        $row.Failures = @($row.Failures) + $message
    }
    else {
        $row.PairedDefaultFramebufferSha256 = $defaultRow[0].FramebufferSha256
        if ($row.Activation.Availability -eq 'Unavailable') {
            $expectedPng = Assert-MatrixContainedPath $defaultRow[0].CaptureDirectory (
                Join-Path $defaultRow[0].CaptureDirectory "screenshots\$screenshotLeaf.png")
            $actualPng = Assert-MatrixContainedPath $row.CaptureDirectory (
                Join-Path $row.CaptureDirectory "screenshots\$screenshotLeaf.png")
            $comparisonReport = Assert-MatrixContainedPath $row.CaptureDirectory (
                Join-Path $row.CaptureDirectory 'compare-paired-default.json')
            $comparisonMask = Assert-MatrixContainedPath $row.CaptureDirectory (
                Join-Path $row.CaptureDirectory 'compare-paired-default-mask.png')
            try {
                $row.PairedDefaultComparison = Compare-MatrixFallbackFramebuffer `
                    -Expected $expectedPng `
                    -Actual $actualPng `
                    -ReportPath $comparisonReport `
                    -MaskPath $comparisonMask
                if (-not $row.PairedDefaultComparison.Passed) {
                    $message = "$($row.Id): $($row.PairedDefaultComparison.Failure)"
                    $null = $matrixFailures.Add($message)
                    $row.Passed = $false
                    $row.Failures = @($row.Failures) + $message
                }
            }
            catch {
                $message = "$($row.Id): fallback/default screenshot comparison failed: $($_.Exception.Message)"
                $null = $matrixFailures.Add($message)
                $row.Passed = $false
                $row.Failures = @($row.Failures) + $message
            }
        }
    }
}

$finishedUtc = [DateTime]::UtcNow
$report = [pscustomobject][ordered]@{
    SchemaVersion = 1
    Scope = 'offline fixed-scene pack diagnostics; final receiver CPU authority requires connected identical pack-off/on A/B'
    Passed = $matrixFailures.Count -eq 0
    StartedUtc = $startedUtc.ToString('O')
    FinishedUtc = $finishedUtc.ToString('O')
    SourceCommit = $sourceCommit
    TrackedSourceStatus = @($sourceStatus)
    BinaryProductVersion = $binaryIdentity.BinaryProductVersion
    BinaryCommit = $binaryIdentity.BinaryCommit
    BinaryMatchesSource = $binaryIdentity.BinaryMatchesSource
    SkipBuild = $binaryIdentity.SkipBuild
    FramePacing = $FramePacing
    Pins = [pscustomobject][ordered]@{
        WarmupMs = $WarmupMs
        ExplicitPresetPerformanceWindowResetAfterWarmup = $true
        AutomaticPerformanceWindowPolicy =
            'settled rolling window; diagnostic reset prohibited'
        RequiredEnhancedSamplesPerMetric = 2048
        RenderPackSampleTimeoutMs = 300000
        DayGroup = $DayGroup
        WorldDayFraction = $WorldDayFraction
        SkyPhaseSeconds = $SkyPhaseSeconds
        MsaaSamples = 0
        OrbitDistanceMeters = $OrbitDistanceMeters
        OrbitYawDegrees = $OrbitYawDegrees
        OrbitPitchDegrees = $OrbitPitchDegrees
        Presets = $presets
        Resolutions = $resolutions
    }
    Summary = [pscustomobject][ordered]@{
        TotalRows = $rows.Count
        PassedRows = @($rows | Where-Object Passed).Count
        ActiveEnhancedRows = @($rows | Where-Object {
            $_.Activation.Availability -eq 'Active' -and $_.Passed
        }).Count
        ResourceUnavailableRows = @($rows | Where-Object {
            $_.Activation.UnavailableClassification -eq 'ResourceUnavailable' -and $_.Passed
        }).Count
        CapabilityUnavailableRows = @($rows | Where-Object {
            $_.Activation.UnavailableClassification -eq 'CapabilityUnavailable' -and $_.Passed
        }).Count
        FailedRows = @($rows | Where-Object { -not $_.Passed }).Count
    }
    EvidenceOracle = [pscustomobject][ordered]@{
        RequiredSamplesPerMetric = 2048
        EqualEnhancedShadowCasterCount = $expectedCasterCount
        CascadeDraws = [pscustomobject][ordered]@{ Low = 2; Medium = 3; High = 4 }
        WarmedCpuClassificationCalls = 0
        VulkanAdapterIdentity = $expectedAdapterIdentity
    }
    DeclaredBudgets = [pscustomobject][ordered]@{
        Low = $budgets.low
        Medium = $budgets.medium
        High = $budgets.high
    }
    Rows = @($rows)
    Failures = @($matrixFailures)
}
$report | ConvertTo-Json -Depth 12 |
    Set-Content -Encoding utf8 -LiteralPath $jsonPath

$markdown = [Text.StringBuilder]::new()
$null = $markdown.AppendLine('# Atmospheric Rendering Performance Matrix')
$null = $markdown.AppendLine()
$null = $markdown.AppendLine('Scope: offline fixed-scene pack diagnostics. Final receiver CPU authority requires connected identical pack-off/on A/B.')
$null = $markdown.AppendLine()
$null = $markdown.AppendLine("- Result: **$(if ($report.Passed) { 'PASS' } else { 'FAIL' })**")
$null = $markdown.AppendLine("- Source commit: ``$sourceCommit``")
$null = $markdown.AppendLine("- Binary commit: ``$($binaryIdentity.BinaryCommit)`` (source match: $($binaryIdentity.BinaryMatchesSource))")
$null = $markdown.AppendLine("- Vulkan adapter: ``$expectedAdapterIdentity``")
$null = $markdown.AppendLine("- Frame pacing: ``$FramePacing``")
$null = $markdown.AppendLine(
    "- Pins: warmup ${WarmupMs} ms; day group $DayGroup; world day fraction " +
    "$(Format-Invariant $WorldDayFraction '0.################'); sky phase " +
    "$(Format-Invariant $SkyPhaseSeconds '0.################') s; MSAA 0; orbit " +
    "$(Format-Invariant $OrbitDistanceMeters) m")
$null = $markdown.AppendLine(
    "- Evidence window: explicit presets reset diagnostics after warmup; Auto preserves " +
    "its hysteresis-owned rolling window. Every active row waits for a complete 2048-sample " +
    "CPU / receiver / GPU window (300000 ms timeout).")
$null = $markdown.AppendLine()
$null = $markdown.AppendLine('## Declared ceilings')
$null = $markdown.AppendLine()
$null = $markdown.AppendLine('| Preset | CPU p50 / p99 (ms) | GPU p50 / p99 at 1080p (ms) | Resident MiB |')
$null = $markdown.AppendLine('|---|---:|---:|---:|')
foreach ($preset in @('low', 'medium', 'high')) {
    $budget = $budgets[$preset]
    $null = $markdown.AppendLine(
        "| $preset | $(Format-Invariant $budget.IncrementalCpuMillisecondsP50) / " +
        "$(Format-Invariant $budget.IncrementalCpuMillisecondsP99) | " +
        "$(Format-Invariant $budget.InclusiveGpuMillisecondsP50At1080p) / " +
        "$(Format-Invariant $budget.InclusiveGpuMillisecondsP99At1080p) | " +
        "$([Math]::Round($budget.ResidentGpuBytes / 1MB, 0)) |")
}
$null = $markdown.AppendLine()
$null = $markdown.AppendLine('## Rows')
$null = $markdown.AppendLine()
$null = $markdown.AppendLine('| Pacing | Preset | Resolution | Availability | CPU / receiver / GPU samples | CPU p50 / p95 / p99 ms | Receiver p50 / p95 / p99 ms | GPU p50 / p95 / p99 ms | Resident MiB | Process WS / private MiB | Casters | Cascades | Draw / dispatch | Framebuffer / paired-default SHA-256 | Result |')
$null = $markdown.AppendLine('|---|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---|')
foreach ($row in $rows) {
    $cpu = if ($null -eq $row.PerformanceMilliseconds.IncrementalCpuP50) {
        'n/a'
    }
    else {
        "$(Format-Invariant $row.PerformanceMilliseconds.IncrementalCpuP50) / " +
        "$(Format-Invariant $row.PerformanceMilliseconds.IncrementalCpuP95) / " +
        "$(Format-Invariant $row.PerformanceMilliseconds.IncrementalCpuP99)"
    }
    $gpu = if ($null -eq $row.PerformanceMilliseconds.InclusiveGpuP50) {
        'n/a'
    }
    else {
        "$(Format-Invariant $row.PerformanceMilliseconds.InclusiveGpuP50) / " +
        "$(Format-Invariant $row.PerformanceMilliseconds.InclusiveGpuP95) / " +
        "$(Format-Invariant $row.PerformanceMilliseconds.InclusiveGpuP99)"
    }
    $receiver = if ($null -eq $row.PerformanceMilliseconds.AbsoluteReceiverCpuP50) {
        'n/a'
    }
    else {
        "$(Format-Invariant $row.PerformanceMilliseconds.AbsoluteReceiverCpuP50) / " +
        "$(Format-Invariant $row.PerformanceMilliseconds.AbsoluteReceiverCpuP95) / " +
        "$(Format-Invariant $row.PerformanceMilliseconds.AbsoluteReceiverCpuP99)"
    }
    $resident = if ($null -eq $row.Resources.ResidentGpuBytes) {
        'n/a'
    }
    else {
        Format-Invariant ($row.Resources.ResidentGpuBytes / 1MB)
    }
    $processMemory = if ($null -eq $row.Process) { 'n/a' } else {
        "$(Format-Invariant ($row.Process.WorkingSetBytes / 1MB)) / " +
        "$(Format-Invariant ($row.Process.PrivateMemoryBytes / 1MB))"
    }
    $digest = if ($null -eq $row.FramebufferSha256) { 'n/a' } else { $row.FramebufferSha256 }
    $pairedDigest = if ($null -eq $row.PairedDefaultFramebufferSha256) { 'n/a' } else { $row.PairedDefaultFramebufferSha256 }
    $result = if ($row.Passed -and $row.Activation.Availability -eq 'Unavailable') {
        'UNAVAILABLE: ' + $row.Activation.UnavailableClassification + ' - ' +
            (([string]$row.Activation.FailureReason) -replace '\|', '/')
    }
    elseif ($row.Passed) {
        'PASS'
    }
    else {
        'FAIL: ' + ((@($row.Failures) -join '; ') -replace '\|', '/')
    }
    $null = $markdown.AppendLine(
        "| $($row.FramePacing) | $($row.Preset) | $($row.Resolution) | $($row.Activation.Availability) | " +
        "$($row.Samples.IncrementalCpu) / $($row.Samples.AbsoluteReceiverCpu) / $($row.Samples.InclusiveGpu) | " +
        "$cpu | $receiver | $gpu | $resident | $processMemory | $($row.Work.ShadowCasters) | " +
        "$($row.Work.CascadeDraws) | $($row.Work.DrawCalls) / $($row.Work.DispatchCalls) | " +
        "$digest / $pairedDigest | $result |")
}
if ($matrixFailures.Count -ne 0) {
    $null = $markdown.AppendLine()
    $null = $markdown.AppendLine('## Failures')
    $null = $markdown.AppendLine()
    foreach ($failure in $matrixFailures) {
        $null = $markdown.AppendLine("- $failure")
    }
}
$markdown.ToString() | Set-Content -Encoding utf8 -LiteralPath $markdownPath

Write-Output "JSON=$jsonPath"
Write-Output "MARKDOWN=$markdownPath"
Write-Output "RESULT=$(if ($report.Passed) { 'PASS' } else { 'FAIL' })"
if (-not $report.Passed) {
    exit 1
}
