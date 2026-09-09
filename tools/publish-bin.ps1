[CmdletBinding()]
param(
    [string]$Version,
    [string]$BaseUrl,
    [switch]$IncludeLinux,
    [string]$MinimumLauncherVersion = '0.0.1'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'publish-bin requires PowerShell 7 or newer.'
}

$RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not (Test-Path (Join-Path $RepoRoot 'AcDream.slnx'))) {
    throw "Could not locate AcDream.slnx above '$PSScriptRoot'."
}

[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $RepoRoot 'Directory.Build.props') -Raw
$repositoryVersion = [string]$buildProperties.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $repositoryVersion
} elseif ($Version -cne $repositoryVersion) {
    throw "Requested version '$Version' differs from repository version '$repositoryVersion'. Update Directory.Build.props before publishing."
}

$semver = '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$'
foreach ($candidate in @($Version, $MinimumLauncherVersion)) {
    if ($candidate -notmatch $semver) {
        throw "Version '$candidate' is not SemVer 2.0 (build metadata '+' is not allowed here)."
    }
}

$RawBase = if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
    "https://github.com/eriknihlen/OpenAC/releases/download/v$Version"
} else {
    $BaseUrl.TrimEnd('/')
}

$BinRoot = Join-Path $RepoRoot 'bin'
$Staging = Join-Path $BinRoot 'payload'
$Rids = @('win-x64')
if ($IncludeLinux) { $Rids += 'linux-x64' }

Write-Host "acdream alpha feed" -ForegroundColor Cyan
Write-Host "  version : $Version"
Write-Host "  rids    : $($Rids -join ', ')"
Write-Host "  output  : $BinRoot"
Write-Host ''

if (Test-Path $Staging) { Remove-Item -LiteralPath $Staging -Recurse -Force }
$null = New-Item -ItemType Directory -Path $Staging -Force

function Invoke-Publish {
    param(
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$Rid,
        [Parameter(Mandatory)][string]$OutputDirectory,
        [switch]$SingleFile
    )

    $arguments = @(
        'publish', (Join-Path $RepoRoot $Project),
        '-c', 'Release',
        '-r', $Rid,
        '--self-contained', 'true',
        "-p:Version=$Version",
        # No SourceLink '+<sha>' suffix: LauncherVersion parses this as SemVer.
        '-p:IncludeSourceRevisionInInformationalVersion=false',
        '-o', $OutputDirectory,
        '--nologo'
    )
    if ($SingleFile) { $arguments += '-p:PublishSingleFile=true' }

    & dotnet @arguments | Out-Null
    if ($LASTEXITCODE) { throw "publish failed: $Project ($Rid)" }
}

function Remove-DebugSymbols {
    param([Parameter(Mandatory)][string]$Directory)

    $symbols = @(Get-ChildItem -LiteralPath $Directory -Recurse -File -Filter *.pdb)
    if ($symbols.Count -eq 0) { return }
    $freed = ($symbols | Measure-Object -Property Length -Sum).Sum
    $symbols | Remove-Item -Force
    '    stripped {0} debug symbol file(s), {1:N1} MB' -f $symbols.Count, ($freed / 1MB) |
        Write-Host -ForegroundColor DarkGray
}

function New-PayloadZip {
    param(
        [Parameter(Mandatory)][string]$SourceDirectory,
        [Parameter(Mandatory)][string]$ZipPath,
        [Parameter(Mandatory)][string[]]$RequiredFiles
    )

    Remove-DebugSymbols $SourceDirectory

    foreach ($required in $RequiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $SourceDirectory $required))) {
            throw "Payload '$SourceDirectory' is missing required file '$required'."
        }
    }

    if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
    # includeBaseDirectory:$false -> entries sit at the zip ROOT, which is where
    # LauncherExecutableSet resolves the hosts after extraction.
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $SourceDirectory,
        $ZipPath,
        [IO.Compression.CompressionLevel]::Optimal,
        $false)

    Set-PayloadEntryModes $ZipPath $RequiredFiles
    Set-ZipCreatorHostUnix $ZipPath
}

function Set-PayloadEntryModes {
    param(
        [Parameter(Mandatory)][string]$ZipPath,
        [Parameter(Mandatory)][string[]]$Executables
    )

    $regularFile = 0x81A4 # 0644
    $executable = 0x81ED  # 0755
    $names = [Collections.Generic.HashSet[string]]::new(
        [string[]]$Executables,
        [StringComparer]::Ordinal)

    $archive = [IO.Compression.ZipFile]::Open($ZipPath, 'Update')
    try {
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            $mode = if ($names.Contains($entry.FullName.Replace('\', '/'))) {
                $executable
            } else {
                $regularFile
            }
            $entry.ExternalAttributes = $mode -shl 16
        }
    } finally {
        $archive.Dispose()
    }
}

function Set-ZipCreatorHostUnix {
    param([Parameter(Mandatory)][string]$ZipPath)

    $bytes = [IO.File]::ReadAllBytes($ZipPath)

    $eocd = -1
    $floor = [Math]::Max(0, $bytes.Length - 22 - 65535)
    for ($i = $bytes.Length - 22; $i -ge $floor; $i--) {
        if ($bytes[$i] -eq 0x50 -and $bytes[$i + 1] -eq 0x4B -and
            $bytes[$i + 2] -eq 0x05 -and $bytes[$i + 3] -eq 0x06) {
            $eocd = $i
            break
        }
    }
    if ($eocd -lt 0) { throw "No end-of-central-directory record in '$ZipPath'." }

    $entryCount = [BitConverter]::ToUInt16($bytes, $eocd + 10)
    $directoryOffset = [BitConverter]::ToUInt32($bytes, $eocd + 16)
    if ($entryCount -eq 0xFFFF -or $directoryOffset -eq 0xFFFFFFFF) {
        throw "'$ZipPath' is a ZIP64 archive; Set-ZipCreatorHostUnix handles the classic layout only."
    }

    $position = [int64]$directoryOffset
    for ($n = 0; $n -lt $entryCount; $n++) {
        if (-not ($bytes[$position] -eq 0x50 -and $bytes[$position + 1] -eq 0x4B -and
                  $bytes[$position + 2] -eq 0x01 -and $bytes[$position + 3] -eq 0x02)) {
            throw "Central directory header $n of '$ZipPath' is malformed."
        }
        $bytes[$position + 5] = 3
        $nameLength = [BitConverter]::ToUInt16($bytes, $position + 28)
        $extraLength = [BitConverter]::ToUInt16($bytes, $position + 30)
        $commentLength = [BitConverter]::ToUInt16($bytes, $position + 32)
        $position += 46 + $nameLength + $extraLength + $commentLength
    }

    [IO.File]::WriteAllBytes($ZipPath, $bytes)
}

function Get-Artifact {
    param(
        [Parameter(Mandatory)][string]$ZipPath,
        [Parameter(Mandatory)][string]$Url
    )

    $item = Get-Item -LiteralPath $ZipPath
    return [ordered]@{
        url    = $Url
        sha256 = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        size   = $item.Length
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue

$clients = [ordered]@{}
$launchers = [ordered]@{}

foreach ($rid in $Rids) {
    $suffix = if ($rid -like 'win-*') { '.exe' } else { '' }
    $clientDirectory = Join-Path $Staging "client-$rid"
    $launcherDirectory = Join-Path $Staging "launcher-$rid"

    Write-Host "[$rid] publishing client (App + Headless)..." -ForegroundColor Yellow
    Invoke-Publish 'src/AcDream.App/AcDream.App.csproj' $rid $clientDirectory
    Invoke-Publish 'src/AcDream.Headless/AcDream.Headless.csproj' $rid $clientDirectory

    Write-Host "[$rid] publishing launcher (+ co-deployed bake)..." -ForegroundColor Yellow
    Invoke-Publish 'src/AcDream.Launcher/AcDream.Launcher.csproj' $rid $launcherDirectory

    $clientZip = Join-Path $BinRoot "client-$rid.zip"
    $launcherZip = Join-Path $BinRoot "launcher-$rid.zip"

    Write-Host "[$rid] packing..." -ForegroundColor Yellow
    New-PayloadZip $clientDirectory $clientZip @("AcDream.App$suffix", "acdream-headless$suffix")
    New-PayloadZip $launcherDirectory $launcherZip @("acdream-launcher$suffix", "acdream-bake$suffix")

    $clients[$rid] = Get-Artifact $clientZip "$RawBase/client-$rid.zip"
    $launchers[$rid] = Get-Artifact $launcherZip "$RawBase/launcher-$rid.zip"
}

$manifest = [ordered]@{
    schemaVersion          = 1
    version                = $Version
    minimumLauncherVersion = $MinimumLauncherVersion
    clients                = $clients
    launchers              = $launchers
}

$manifestPath = Join-Path $BinRoot 'manifest.json'
$json = $manifest | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText($manifestPath, $json + "`n", [Text.UTF8Encoding]::new($false))

Remove-Item -LiteralPath $Staging -Recurse -Force

Write-Host ''
Write-Host 'Feed written:' -ForegroundColor Green
$total = 0L
foreach ($file in (Get-ChildItem -LiteralPath $BinRoot -File | Sort-Object Name)) {
    $total += $file.Length
    '  {0,-26} {1,10:N1} MB' -f $file.Name, ($file.Length / 1MB) | Write-Host
}
'  {0,-26} {1,10:N1} MB' -f 'TOTAL', ($total / 1MB) | Write-Host
$dirtyLocks = @(
    @(& git -C $RepoRoot status --porcelain -- '*packages.*.lock.json' 2>$null) |
        Where-Object { $_ }
)
if ($dirtyLocks.Count -gt 0) {
    Write-Host ''
    Write-Host 'Note: the RID restore modified these lock files:' -ForegroundColor Yellow
    $dirtyLocks | ForEach-Object { "  $($_.Trim())" | Write-Host }
    Write-Host '  If you did not change dependencies, discard them:' -ForegroundColor DarkGray
    Write-Host "  git checkout -- '*packages.*.lock.json'" -ForegroundColor DarkGray
}

Write-Host ''
Write-Host 'Releases are published by CI, not from here:' -ForegroundColor Cyan
Write-Host '  a green gate on main (or a v* tag) -> the release job -> a Release with these assets' -ForegroundColor DarkGray
Write-Host '  This build is for local inspection; bin/ stays gitignored.' -ForegroundColor DarkGray
