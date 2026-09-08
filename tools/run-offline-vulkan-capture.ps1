[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$WarmupMs = 12000,
    [int]$DayGroup = 0,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo 'src\AcDream.App\bin\Release\net10.0\AcDream.App.exe'

function Write-Step($message) { Write-Host "[vk-capture] $message" }

if (-not $SkipBuild) {
    & dotnet build (Join-Path $repo 'AcDream.slnx') -c Release --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }
}
if (-not (Test-Path $exe)) { throw "Client not found at $exe." }

if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
New-Item -ItemType Directory -Force -Path $Out | Out-Null

$probe = Join-Path $Out 'vk.probe.txt'
Set-Content -Encoding utf8 -Path $probe -Value @"
sleep $WarmupMs
screenshot vk-offline 30000
sleep 500
"@

$log = Join-Path $Out 'client.log'
$previousLive = $env:ACDREAM_LIVE
Remove-Item Env:\ACDREAM_LIVE -ErrorAction SilentlyContinue
$env:ACDREAM_DAT_DIR = Join-Path $env:USERPROFILE "Documents\Asheron's Call"
$env:ACDREAM_NO_AUDIO = '1'
$env:ACDREAM_RETAIL_UI = '1'
$env:ACDREAM_DAY_GROUP = "$DayGroup"
$env:ACDREAM_UI_PROBE_SCRIPT = $probe
$env:ACDREAM_AUTOMATION_ARTIFACT_DIR = $Out
$env:ACDREAM_RENDER_BACKEND = 'vulkan'
$env:VK_INSTANCE_LAYERS = 'VK_LAYER_KHRONOS_validation'
$env:VK_LOADER_DEBUG = 'layer'

Write-Step "launching Vulkan offline client (warmup ${WarmupMs}ms, day group $DayGroup)"
$proc = Start-Process -FilePath $exe -RedirectStandardOutput $log `
    -RedirectStandardError "$log.err" -PassThru -WindowStyle Minimized

try {
    $shots = Join-Path $Out 'screenshots'
    $deadline = (Get-Date).AddMilliseconds($WarmupMs + 60000)
    $captured = $false
    while ((Get-Date) -lt $deadline) {
        if ((Test-Path $shots) -and (Get-ChildItem $shots -Filter *.png -ErrorAction SilentlyContinue)) {
            $captured = $true
            break
        }
        if ($proc.HasExited) { break }
        Start-Sleep -Milliseconds 1000
    }
    if ($captured) { Start-Sleep -Milliseconds 1500 }
    else { Write-Step 'no screenshot captured before the deadline' }
}
finally {
    $app = Get-Process -Name AcDream.App -ErrorAction SilentlyContinue
    if ($app) {
        $app.CloseMainWindow() | Out-Null
        if (-not $app.WaitForExit(15000)) {
            Write-Step 'WM_CLOSE timed out; forcing'
            $app | Stop-Process -Force
        }
    }
    Remove-Item Env:\ACDREAM_RENDER_BACKEND -ErrorAction SilentlyContinue
    Remove-Item Env:\VK_INSTANCE_LAYERS -ErrorAction SilentlyContinue
    Remove-Item Env:\VK_LOADER_DEBUG -ErrorAction SilentlyContinue
    if ($previousLive) { $env:ACDREAM_LIVE = $previousLive }
}

Write-Step "exit code $($proc.ExitCode)"
Write-Step 'layer insertion evidence:'
Select-String -Path $log, "$log.err" -Pattern 'Insert instance layer' -ErrorAction SilentlyContinue |
    Select-Object -First 3 | ForEach-Object { Write-Host "  $($_.Line)" }
Write-Step 'validation output:'
$vuids = Select-String -Path $log, "$log.err" -Pattern 'VUID-|UNASSIGNED-|Validation Error|Validation Warning' -ErrorAction SilentlyContinue
if ($vuids) { $vuids | Select-Object -First 20 | ForEach-Object { Write-Host "  $($_.Line)" } }
else { Write-Host '  none' }
