# Launch OpenAC against Aeshnidae under `dotnet watch`, so edits to method
# bodies patch the running client without a restart (.NET Hot Reload).
#
#   .\dev-aesh.ps1                  -> logs in as drakkon2
#   .\dev-aesh.ps1 -Account raslau1
#   .\dev-aesh.ps1 -Offline         -> no server; the offline world view
#
# What hot-reloads: statement-level edits inside existing methods - UI layout,
# widget calls, behaviour logic, constants read at runtime. What needs a
# restart: new types, new fields, changed signatures, changes to static
# initialisers already run. `dotnet watch` prints "Hot reload of changes
# succeeded" or explains why it could not apply an edit; with
# --non-interactive it restarts the client on edits it cannot apply.
#
# Debug configuration, unlike play-aesh.ps1, so the bot's ImGui windows and
# behaviours can be iterated without a Release build.

param(
    [string]$Account = 'drakkon2',
    [string]$Server  = '127.0.0.1',
    [int]$Port       = 9020,
    [string]$DatDir  = 'C:\Games\ACE\Dats',
    [switch]$Offline,
    [switch]$NoAudio,
    [switch]$PasswordFromEnv
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$pak  = Join-Path $DatDir 'acdream.pak'

if (-not (Test-Path $pak)) {
    Write-Host "No prepared package at $pak" -ForegroundColor Yellow
    Write-Host "Bake it once:  dotnet run --project src\AcDream.Bake -c Release -- --dat-dir `"$DatDir`" --out `"$pak`""
    exit 1
}

$env:ACDREAM_DAT_DIR  = $DatDir
$env:ACDREAM_PAK_PATH = $pak
if ($NoAudio) { $env:ACDREAM_NO_AUDIO = '1' } else { Remove-Item Env:ACDREAM_NO_AUDIO -ErrorAction SilentlyContinue }

if ($Offline) {
    foreach ($name in 'ACDREAM_LIVE', 'ACDREAM_TEST_HOST', 'ACDREAM_TEST_PORT', 'ACDREAM_TEST_USER', 'ACDREAM_TEST_PASS') {
        Remove-Item "Env:$name" -ErrorAction SilentlyContinue
    }
} else {
    if ($PasswordFromEnv) {
        $plain = $env:ACDREAM_TEST_PASS
        if ([string]::IsNullOrEmpty($plain)) {
            Write-Host "-PasswordFromEnv given but ACDREAM_TEST_PASS is empty." -ForegroundColor Yellow
            exit 1
        }
    } else {
        $secure = Read-Host "Password for '$Account'" -AsSecureString
        $plain  = [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
                      [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
    }
    $env:ACDREAM_LIVE      = '1'
    $env:ACDREAM_TEST_HOST = $Server
    $env:ACDREAM_TEST_PORT = "$Port"
    $env:ACDREAM_TEST_USER = $Account
    $env:ACDREAM_TEST_PASS = $plain
}

# Hot Reload needs the runtime started by dotnet watch, in Debug, with the
# modifiable-assemblies switch it sets itself. --non-interactive turns the
# "restart?" question for rude edits into an automatic restart.
Set-Location $root
dotnet watch --project src\AcDream.App\AcDream.App.csproj --non-interactive run
