[CmdletBinding()]
param(
    [ValidateSet('Low', 'Medium', 'High', 'Auto')]
    [string]$Preset = 'High',
    [ValidatePattern('^[1-9][0-9]*x[1-9][0-9]*$')]
    [string]$Resolution = '1920x1080',
    [switch]$EnableAudio,
    [switch]$NoAudio,
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo 'src\AcDream.App\bin\Release\net10.0\AcDream.App.exe'
$datDirectory = Join-Path $env:USERPROFILE "Documents\Asheron's Call"
$audioEnabled = [bool]$EnableAudio

if ($EnableAudio -and $NoAudio) {
    throw '-EnableAudio and -NoAudio are mutually exclusive.'
}

if (-not $SkipBuild) {
    Write-Host '[atmospheric-preview] building acdream Release'
    & dotnet build (Join-Path $repo 'AcDream.slnx') -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        throw "acdream Release build failed with exit code $LASTEXITCODE."
    }
}
if (-not (Test-Path -LiteralPath $exe)) {
    throw "acdream executable not found at '$exe'. Build it first or omit -SkipBuild."
}
if (-not (Test-Path -LiteralPath $datDirectory)) {
    throw "AC DAT directory not found at '$datDirectory'."
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$root = Join-Path $repo "artifacts\atmospheric-rendering\visible-$($Preset.ToLowerInvariant())-$stamp"
$config = Join-Path $root 'state\config'
$data = Join-Path $root 'state\data'
$cache = Join-Path $root 'state\cache'
if (Test-Path -LiteralPath $root) {
    throw "Preview artifact directory already exists: '$root'."
}
New-Item -ItemType Directory -Path $config, $data, $cache | Out-Null

$settings = [ordered]@{
    display = [ordered]@{
        resolution = $Resolution
        fullscreen = $false
        vsync = $true
        renderPack = [ordered]@{
            packId = 'acdream.atmospheric'
            packVersion = '1.0.0'
            presetId = $Preset.ToLowerInvariant()
            settingOverrides = [ordered]@{}
        }
    }
    version = 3
}
$settingsPath = Join-Path $config 'settings.json'
$settings | ConvertTo-Json -Depth 8 |
    Set-Content -Encoding utf8 -LiteralPath $settingsPath

$requiredEnvironmentNames = @(
    'ACDREAM_CONFIG_DIR',
    'ACDREAM_DATA_DIR',
    'ACDREAM_CACHE_DIR',
    'ACDREAM_DAT_DIR',
    'ACDREAM_RETAIL_UI',
    'ACDREAM_NO_AUDIO'
)
$environmentNames = @(
    @([Environment]::GetEnvironmentVariables('Process').Keys) |
        ForEach-Object { [string]$_ } |
        Where-Object { $_.StartsWith('ACDREAM_', [StringComparison]::OrdinalIgnoreCase) }
    $requiredEnvironmentNames
) | Sort-Object -Unique
$prior = @{}
foreach ($name in $environmentNames) {
    $prior[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

$stdoutLog = Join-Path $root 'client.stdout.log'
$stderrLog = Join-Path $root 'client.stderr.log'
$launchPath = Join-Path $root 'launch.json'
$binary = Get-Item -LiteralPath $exe
$binaryVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion
$launch = [ordered]@{
    schemaVersion = 2
    status = 'prepared'
    processId = $null
    executable = $exe
    executableSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $exe).Hash.ToLowerInvariant()
    executableProductVersion = $binaryVersion
    executableLastWriteUtc = $binary.LastWriteTimeUtc.ToString('O')
    preset = $Preset.ToLowerInvariant()
    resolution = $Resolution
    audio = [ordered]@{
        enabled = $audioEnabled
        mode = if ($audioEnabled) { 'enabled-explicit' } else { 'disabled-default' }
    }
    stateRoot = Join-Path $root 'state'
    settings = $settingsPath
    stdoutLog = $stdoutLog
    stderrLog = $stderrLog
    preparedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    launchedUtc = $null
    startupError = $null
}
$launch | ConvertTo-Json -Depth 6 |
    Set-Content -Encoding utf8 -LiteralPath $launchPath

$process = $null
try {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }
    $env:ACDREAM_CONFIG_DIR = $config
    $env:ACDREAM_DATA_DIR = $data
    $env:ACDREAM_CACHE_DIR = $cache
    $env:ACDREAM_DAT_DIR = $datDirectory
    $env:ACDREAM_RETAIL_UI = '1'
    $env:ACDREAM_NO_AUDIO = if ($audioEnabled) { $null } else { '1' }

    try {
        $process = Start-Process -FilePath $exe -PassThru `
            -RedirectStandardOutput $stdoutLog `
            -RedirectStandardError $stderrLog
    }
    catch {
        $launch.status = 'start-failed'
        $launch.startupError = $_.Exception.Message
        $launch | ConvertTo-Json -Depth 6 |
            Set-Content -Encoding utf8 -LiteralPath $launchPath
        throw
    }
}
finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $prior[$name], 'Process')
    }
}

$launch.status = 'started'
$launch.processId = $process.Id
$launch.launchedUtc = [DateTimeOffset]::UtcNow.ToString('O')
$launch | ConvertTo-Json -Depth 6 |
    Set-Content -Encoding utf8 -LiteralPath $launchPath

Write-Host "[atmospheric-preview] launched acdream process $($process.Id)"
Write-Host "[atmospheric-preview] preset: $Preset; resolution: $Resolution"
Write-Host "[atmospheric-preview] audio: $($launch.audio.mode)"
Write-Host "[atmospheric-preview] disposable state: $root"
Write-Host "[atmospheric-preview] logs: $stdoutLog and $stderrLog"
Write-Host '[atmospheric-preview] close the acdream window normally when finished'
