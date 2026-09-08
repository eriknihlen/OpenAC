Set-StrictMode -Version Latest

function Get-AtmosphericPresetUnavailableReasonClassification {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Reason,
        [Parameter(Mandatory = $true)]
        [ValidateSet('low', 'medium', 'high', 'auto')][string]$Preset)

    if ($Preset -eq 'auto') {
        $performancePattern = "^Automatic quality disabled render pack 'acdream\.atmospheric' because Low remained over its declared performance budget for 180 stable samples: GPU p99 (?<gpu>[0-9]+(?:\.[0-9]+)?) ms \(budget (?<gpuBudget>[0-9]+(?:\.[0-9]+)?) ms\), CPU p99 (?<cpu>[0-9]+(?:\.[0-9]+)?) ms \(budget (?<cpuBudget>[0-9]+(?:\.[0-9]+)?) ms\), resident GPU bytes (?<resident>[0-9]+) \(budget (?<residentBudget>[0-9]+)\)\.$"
        $performance = [Regex]::Match(
            $Reason,
            $performancePattern,
            [Text.RegularExpressions.RegexOptions]::CultureInvariant)
        if ($performance.Success) {
            $culture = [Globalization.CultureInfo]::InvariantCulture
            $gpu = [double]::Parse($performance.Groups['gpu'].Value, $culture)
            $gpuBudget = [double]::Parse($performance.Groups['gpuBudget'].Value, $culture)
            $cpu = [double]::Parse($performance.Groups['cpu'].Value, $culture)
            $cpuBudget = [double]::Parse($performance.Groups['cpuBudget'].Value, $culture)
            $resident = [long]::Parse($performance.Groups['resident'].Value, $culture)
            $residentBudget = [long]::Parse($performance.Groups['residentBudget'].Value, $culture)
            if ($gpu -gt $gpuBudget -or $cpu -gt $cpuBudget -or
                $resident -gt $residentBudget) {
                return [pscustomobject][ordered]@{
                    Supported = $true
                    Classification = 'PerformanceUnavailable'
                    Reason = $Reason
                }
            }
        }
        foreach ($resolvedPreset in @('low', 'medium', 'high')) {
            $classification = Get-AtmosphericPresetUnavailableReasonClassification `
                -Reason $Reason -Preset $resolvedPreset
            if ($classification.Supported) { return $classification }
        }
        return [pscustomobject][ordered]@{
            Supported = $false
            Classification = 'UnexpectedFailure'
            Reason = $Reason
        }
    }

    $escapedPreset = [Regex]::Escape($Preset)
    $preparationPrefix = "(?:Render pack 'acdream\.atmospheric' could not be prepared: )?"
    $resourcePatterns = @(
        "^${preparationPrefix}Render pack preset '$escapedPreset' needs [1-9][0-9]* resident GPU bytes at [1-9][0-9]*x[1-9][0-9]*; its declared ceiling is [1-9][0-9]*\. Select a compatible preset or reduce the main-world resolution\.$",
        "^${preparationPrefix}Render pack preset '$escapedPreset' needs [1-9][0-9]* resident GPU bytes at [1-9][0-9]*x[1-9][0-9]*; this host permits [1-9][0-9]* under its .+ policy\.$",
        "^${preparationPrefix}Render pack preset '$escapedPreset' needs [1-9][0-9]* transient multisample GPU bytes at [1-9][0-9]*x[1-9][0-9]* x[1-9][0-9]*; this host permits [1-9][0-9]* under its .+ policy\.$",
        "^Directional shadow rendering failed: Render pack preset '$escapedPreset' needs [1-9][0-9]* resident GPU bytes after materializing its scene-dependent shadow command buffers; the active pack budget is [1-9][0-9]* bytes\.$",
        "^Preset '$escapedPreset' declares a [1-9][0-9]*-byte resident GPU ceiling, but this host permits [1-9][0-9]* bytes under its .+ policy\.$"
    )
    foreach ($pattern in $resourcePatterns) {
        if ([Regex]::IsMatch($Reason, $pattern, [Text.RegularExpressions.RegexOptions]::CultureInvariant)) {
            return [pscustomobject][ordered]@{
                Supported = $true
                Classification = 'ResourceUnavailable'
                Reason = $Reason
            }
        }
    }

    $capabilityPatterns = @(
        "^Pack 'acdream\.atmospheric' requires unsupported capability '[A-Za-z][A-Za-z0-9]*'\.$",
        "^Preset '$escapedPreset' requires unsupported capability '[A-Za-z][A-Za-z0-9]*'\.$",
        "^Preset '$escapedPreset' resource '[a-z0-9][a-z0-9.-]*' needs [1-9][0-9]* image-array layers; this device provides [0-9]+\.$",
        "^Preset '$escapedPreset' resource '[a-z0-9][a-z0-9.-]*' needs [1-9][0-9]*(?:\.[0-9]+)?x[1-9][0-9]*(?:\.[0-9]+)?; this device's maximum 2-D image edge is [1-9][0-9]*\.$",
        "^${preparationPrefix}Render pack preset '$escapedPreset' resolves an image to [1-9][0-9]*x[1-9][0-9]* at [1-9][0-9]*x[1-9][0-9]*; this device's maximum 2-D image edge is [1-9][0-9]*\.$",
        "^${preparationPrefix}Render pack preset '$escapedPreset' needs [1-9][0-9]* image-array layers; this device provides [0-9]+\.$"
    )
    foreach ($pattern in $capabilityPatterns) {
        if ([Regex]::IsMatch($Reason, $pattern, [Text.RegularExpressions.RegexOptions]::CultureInvariant)) {
            return [pscustomobject][ordered]@{
                Supported = $true
                Classification = 'CapabilityUnavailable'
                Reason = $Reason
            }
        }
    }

    return [pscustomobject][ordered]@{
        Supported = $false
        Classification = 'UnexpectedFailure'
        Reason = $Reason
    }
}

function Test-AtmosphericPerformanceMetadataEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$MetadataPath,
        [Parameter(Mandatory = $true)]
        [ValidateSet('retail', 'low', 'medium', 'high', 'auto')][string]$Preset,
        [Parameter(Mandatory = $true)][int]$ExpectedWidth,
        [Parameter(Mandatory = $true)][int]$ExpectedHeight,
        [switch]$AllowSafeFallback)

    $failures = [Collections.Generic.List[string]]::new()
    function Require([object]$Value, [string]$Name, [string]$Context) {
        $property = $Value.PSObject.Properties[$Name]
        if ($null -eq $property) { throw "$Context is missing required property '$Name'." }
        return $property.Value
    }
    function Fail([string]$Message) { $null = $failures.Add($Message) }
    function Require-FiniteNonNegative([object]$Value, [string]$Name) {
        $number = [double](Require $Value $Name 'RenderPack.Performance')
        if (-not [double]::IsFinite($number) -or $number -lt 0) {
            Fail "$Name must be finite and non-negative"
        }
        return $number
    }

    $metadata = Get-Content -Raw -LiteralPath $MetadataPath | ConvertFrom-Json
    if ([int](Require $metadata 'SchemaVersion' 'metadata') -ne 1) {
        Fail 'metadata SchemaVersion must be 1'
    }
    if ([int](Require $metadata 'Width' 'metadata') -ne $ExpectedWidth -or
        [int](Require $metadata 'Height' 'metadata') -ne $ExpectedHeight) {
        Fail "capture extent must be ${ExpectedWidth}x${ExpectedHeight}"
    }
    $pack = Require $metadata 'RenderPack' 'metadata'
    $enhanced = $Preset -ne 'retail'
    $state = [int](Require $pack 'State' 'RenderPack')
    $fallback = $enhanced -and $state -eq 3
    $expectedPackId = if ($enhanced -and -not $fallback) { 'acdream.atmospheric' } else { 'retail' }
    $expectedVersion = if ($enhanced -and -not $fallback) { '1.0.0' } else { '' }
    $expectedPreset = if ($enhanced -and -not $fallback) { $Preset } else { 'off' }
    $expectedState = if ($enhanced -and -not $fallback) { 2 } elseif ($fallback) { 3 } else { 0 }
    $actualEffectiveQuality = [string](
        Require $pack 'EffectiveQuality' 'RenderPack')
    $expectedQuality = if ($enhanced -and -not $fallback -and $Preset -ne 'auto') {
        $Preset
    } else { 'off' }
    foreach ($comparison in @(
        @('PackId', $expectedPackId), @('PackVersion', $expectedVersion),
        @('PresetId', $expectedPreset))) {
        $actual = [string](Require $pack ([string]$comparison[0]) 'RenderPack')
        if ($actual -cne [string]$comparison[1]) {
            Fail "$($comparison[0]) was '$actual', expected '$($comparison[1])'"
        }
    }
    if ($enhanced -and -not $fallback -and $Preset -eq 'auto') {
        if ($actualEffectiveQuality -cnotin @('low', 'medium', 'high')) {
            Fail "Automatic EffectiveQuality was '$actualEffectiveQuality', expected low, medium, or high"
        }
    }
    elseif ($actualEffectiveQuality -cne $expectedQuality) {
        Fail "EffectiveQuality was '$actualEffectiveQuality', expected '$expectedQuality'"
    }
    if ($state -ne $expectedState) {
        Fail "activation state must be $expectedState"
    }
    $generation = [int](Require $pack 'ActivationGeneration' 'RenderPack')
    if (($fallback -and $generation -lt 1) -or
        ($enhanced -and -not $fallback -and $Preset -eq 'auto' -and
            $generation -lt 1) -or
        ($enhanced -and -not $fallback -and $Preset -ne 'auto' -and
            $generation -ne 1) -or
        (-not $enhanced -and $generation -ne 0)) {
        Fail "activation generation is invalid for state $state"
    }
    $failureReason = [string](Require $pack 'FailureReason' 'RenderPack')
    $unavailableClassification = $null
    if ($fallback) {
        if (-not $AllowSafeFallback) {
            Fail 'FailedToRetail fallback was not explicitly allowed for this capture'
        }
        if ([string]::IsNullOrWhiteSpace($failureReason)) {
            Fail 'FailedToRetail fallback must preserve one exact failure reason'
        }
        else {
            $reasonClassification = Get-AtmosphericPresetUnavailableReasonClassification `
                -Reason $failureReason -Preset $Preset
            $unavailableClassification = $reasonClassification.Classification
            if (-not $reasonClassification.Supported) {
                Fail 'FailedToRetail reason is not a strict resource/capability/Auto-performance unavailability'
            }
        }
    }
    elseif (-not [string]::IsNullOrWhiteSpace($failureReason)) {
        Fail 'render-pack FailureReason must be empty'
    }

    $performance = Require $pack 'Performance' 'RenderPack'
    $topResident = [long](Require $pack 'RetainedGpuBytes' 'RenderPack')
    $topTransient = [long](Require $pack 'TransientGpuBytes' 'RenderPack')
    $nestedResident = [long](Require $performance 'ResidentGpuBytes' 'RenderPack.Performance')
    $nestedTransient = [long](Require $performance 'TransientGpuBytes' 'RenderPack.Performance')
    if ($topResident -ne $nestedResident) { Fail 'top-level and performance resident GPU bytes disagree' }
    if ($topTransient -ne $nestedTransient) { Fail 'top-level and performance transient GPU bytes disagree' }

    $casterCount = [int](Require $pack 'ShadowCasterCount' 'RenderPack')
    $cascadeCount = [int](Require $pack 'CascadeDrawCount' 'RenderPack')
    $classificationCalls = [int](Require $pack 'CpuClassificationCalls' 'RenderPack')
    $drawCalls = [int](Require $pack 'DrawCalls' 'RenderPack')
    $dispatchCalls = [int](Require $pack 'DispatchCalls' 'RenderPack')
    $imageCount = [int](Require $pack 'ImageCount' 'RenderPack')
    $bufferCount = [int](Require $pack 'BufferCount' 'RenderPack')
    $passes = @(Require $pack 'Passes' 'RenderPack')

    if ($fallback) {
        if ($casterCount -ne 0 -or $cascadeCount -ne 0 -or $classificationCalls -ne 0 -or
            $drawCalls -ne 0 -or $dispatchCalls -ne 0 -or $passes.Count -ne 0 -or
            $imageCount -ne 0 -or $bufferCount -ne 0 -or
            $topResident -ne 0 -or $topTransient -ne 0) {
            Fail 'FailedToRetail fallback must have zero pack work and resources'
        }
        foreach ($countName in @('CpuSampleCount', 'AbsoluteReceiverCpuSampleCount', 'GpuSampleCount')) {
            if ([int](Require $performance $countName 'RenderPack.Performance') -ne 0) {
                Fail "FailedToRetail fallback must have zero $countName"
            }
        }
        foreach ($metric in @(
            'IncrementalCpuMillisecondsP50', 'IncrementalCpuMillisecondsP95',
            'IncrementalCpuMillisecondsP99', 'AbsoluteReceiverCpuMillisecondsP50',
            'AbsoluteReceiverCpuMillisecondsP95', 'AbsoluteReceiverCpuMillisecondsP99',
            'InclusiveGpuMillisecondsP50', 'InclusiveGpuMillisecondsP95',
            'InclusiveGpuMillisecondsP99', 'ResidentGpuBytes', 'TransientGpuBytes')) {
            if ([double](Require $performance $metric 'RenderPack.Performance') -ne 0) {
                Fail "FailedToRetail fallback must report zero $metric"
            }
        }
    }
    elseif (-not $enhanced) {
        if ($casterCount -ne 0 -or $cascadeCount -ne 0 -or $classificationCalls -ne 0 -or
            $drawCalls -ne 0 -or $dispatchCalls -ne 0 -or $passes.Count -ne 0 -or
            $imageCount -ne 0 -or $bufferCount -ne 0 -or
            $topResident -ne 0 -or $topTransient -ne 0) {
            Fail 'retail row must have zero pack work, resources, and classification'
        }
    }
    else {
        $shapePreset = if ($Preset -eq 'auto') {
            $actualEffectiveQuality
        } else { $Preset }
        $expectedCascades = @{ low = 2; medium = 3; high = 4 }[$shapePreset]
        if ($casterCount -le 0) { Fail 'enhanced row must contain at least one shadow caster' }
        if ($cascadeCount -ne $expectedCascades) {
            Fail "$Preset/$shapePreset must render exactly $expectedCascades cascades"
        }
        if ($classificationCalls -ne 0) { Fail 'warmed capture must perform zero CPU classifications' }
        $expectedPassIds = [Collections.Generic.List[string]]::new()
        $expectedPassIds.Add('atmospheric-world-receiver')
        if ($shapePreset -eq 'low') {
            $expectedPassIds.Add('directional-shadow-multiview')
        }
        else {
            for ($cascade = 0; $cascade -lt $expectedCascades; $cascade++) {
                $expectedPassIds.Add("directional-shadow-cascade-$cascade")
            }
        }
        $postPassIds = @(
            'atmospheric-sun-occlusion', 'atmospheric-sun-rays',
            'atmospheric-volumetric-shafts', 'atmospheric-bloom-downsample',
            'atmospheric-bloom-blur-horizontal', 'atmospheric-bloom-blur-vertical',
            'atmospheric-filmic')
        foreach ($id in $postPassIds) { $expectedPassIds.Add($id) }
        $actualPassIds = @($passes | ForEach-Object { [string](Require $_ 'PassId' 'RenderPack.Passes[]') })
        if (($actualPassIds -join '|') -cne (@($expectedPassIds) -join '|')) {
            Fail "pass order/shape was '$($actualPassIds -join ',')'"
        }
        $shadowDrawsPerPass = 5
        # Low now uses the quarter-resolution separable graph as well. Its
        # volumetric pass remains declared for one stable API shape but records
        # zero draws because the Low preset disables volumetric strength.
        $postDraws = 6
        $expectedDraws = $postDraws + $(if ($shapePreset -eq 'low') {
                $shadowDrawsPerPass
            } else {
                $shadowDrawsPerPass * $expectedCascades
            })
        $summedDraws = [int](($passes | Measure-Object -Property DrawCalls -Sum).Sum)
        $summedDispatches = [int](($passes | Measure-Object -Property DispatchCalls -Sum).Sum)
        if ($drawCalls -ne $expectedDraws -or $summedDraws -ne $expectedDraws) {
            Fail "draw shape must total $expectedDraws calls"
        }
        if ($dispatchCalls -ne 0 -or $summedDispatches -ne 0) {
            Fail 'atmospheric pack must issue zero dispatch calls'
        }
        foreach ($pass in $passes) {
            $passId = [string](Require $pass 'PassId' 'RenderPack.Passes[]')
            $expectedPassDraws = if ($passId -like 'directional-shadow-*') {
                    $shadowDrawsPerPass
                }
                elseif ($passId -in @('atmospheric-world-receiver', 'atmospheric-volumetric-shafts')) { 0 }
                else { 1 }
            if ([int](Require $pass 'DrawCalls' 'RenderPack.Passes[]') -ne $expectedPassDraws -or
                [int](Require $pass 'DispatchCalls' 'RenderPack.Passes[]') -ne 0) {
                Fail "pass '$passId' must record exactly $expectedPassDraws draws and zero dispatches"
            }
        }
        foreach ($countName in @('CpuSampleCount', 'AbsoluteReceiverCpuSampleCount', 'GpuSampleCount')) {
            if ([int](Require $performance $countName 'RenderPack.Performance') -ne 2048) {
                Fail "$countName must contain the complete 2048-sample window"
            }
        }
        foreach ($metric in @(
            'IncrementalCpuMillisecondsP50', 'IncrementalCpuMillisecondsP95',
            'IncrementalCpuMillisecondsP99', 'AbsoluteReceiverCpuMillisecondsP50',
            'AbsoluteReceiverCpuMillisecondsP95', 'AbsoluteReceiverCpuMillisecondsP99',
            'InclusiveGpuMillisecondsP50', 'InclusiveGpuMillisecondsP95',
            'InclusiveGpuMillisecondsP99')) { $null = Require-FiniteNonNegative $performance $metric }
    }

    return [pscustomobject][ordered]@{
        Passed = $failures.Count -eq 0
        Failures = @($failures)
        Outcome = if ($fallback) { 'Unavailable' } elseif ($enhanced) { 'Active' } else { 'Retail' }
        UnavailableClassification = $unavailableClassification
        FailureReason = if ($fallback) { $failureReason } else { $null }
        EffectiveQuality = $actualEffectiveQuality
        ShadowCasterCount = $casterCount
        CascadeDrawCount = $cascadeCount
        CpuClassificationCalls = $classificationCalls
        PassIds = @($passes | ForEach-Object { [string]$_.PassId })
    }
}
