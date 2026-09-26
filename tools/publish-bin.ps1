[CmdletBinding()]
param(
    [string]$Version,
    [string]$BaseUrl,
    [switch]$IncludeLinux,
    [switch]$IncludeMacOS,
    [switch]$MacOnly,
    [ValidateSet('osx-arm64', 'osx-x64')][string]$MacRid = 'osx-arm64',
    # osx-x64: where tools/build-macos-x64-vulkan.ps1 builds or reuses its runtime.
    [string]$MacVulkanRuntimeDirectory,
    [string]$MacArtifactsDirectory,
    # The oldest launcher that may install this release's client. Empty means
    # this release's own launcher: a launcher always updates itself before the
    # client, and one that predates a change in how the two share the install
    # (the single install folder, for one) must not put the new client in
    # place first. Pass an older version only for a release whose client
    # works with every launcher back to it.
    [string]$MinimumLauncherVersion
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

if ([string]::IsNullOrWhiteSpace($MinimumLauncherVersion)) {
    $MinimumLauncherVersion = $Version
}

$semver = '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$'
foreach ($candidate in @($Version, $MinimumLauncherVersion)) {
    if ($candidate -notmatch $semver) {
        throw "Version '$candidate' is not SemVer 2.0 (build metadata '+' is not allowed here)."
    }
}

# The launcher refuses a manifest whose minimum is newer than its release.
if ([System.Management.Automation.SemanticVersion]::new($MinimumLauncherVersion) -gt
    [System.Management.Automation.SemanticVersion]::new($Version)) {
    throw "Minimum launcher version '$MinimumLauncherVersion' is newer than release '$Version'."
}

$RawBase = if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
    "https://github.com/eriknihlen/OpenAC/releases/download/v$Version"
} else {
    $BaseUrl.TrimEnd('/')
}

$BinRoot = Join-Path $RepoRoot 'bin'
$Staging = Join-Path $BinRoot 'payload'
if ($MacOnly -and ($IncludeLinux -or $IncludeMacOS -or -not [string]::IsNullOrWhiteSpace($MacArtifactsDirectory))) {
    throw '-MacOnly cannot be combined with another platform-selection parameter.'
}
if ($IncludeMacOS -and -not $IsMacOS) {
    throw '-IncludeMacOS packages an OpenAC.app bundle and must run on macOS.'
}
if (-not [string]::IsNullOrWhiteSpace($MacArtifactsDirectory) -and $IncludeMacOS) {
    throw 'Use either -IncludeMacOS or -MacArtifactsDirectory, not both.'
}
# osx-x64: -MacRid only means something alongside -MacOnly or -IncludeMacOS.
if ($PSBoundParameters.ContainsKey('MacRid') -and -not ($MacOnly -or $IncludeMacOS)) {
    throw '-MacRid selects the macOS payload built by -MacOnly or -IncludeMacOS.'
}
[string[]]$Rids = if ($MacOnly) { 'osx-arm64' } else { 'win-x64' }
if ($IncludeLinux) { $Rids += 'linux-x64' }
if ($IncludeMacOS) { $Rids += 'osx-arm64' }
# osx-x64: overrides $Rids with x64 in place of the arm64 default.
if ($MacRid -eq 'osx-x64') {
    if ($MacOnly) {
        $Rids = @('osx-x64')
    } elseif ($IncludeMacOS) {
        $Rids = @($Rids | ForEach-Object { if ($_ -eq 'osx-arm64') { 'osx-x64' } else { $_ } })
    }
}

Write-Host "acdream alpha feed" -ForegroundColor Cyan
Write-Host "  version : $Version"
Write-Host "  rids    : $($Rids -join ', ')"
Write-Host "  output  : $BinRoot"
Write-Host ''

if (Test-Path $Staging) { Remove-Item -LiteralPath $Staging -Recurse -Force }
$null = New-Item -ItemType Directory -Path $Staging -Force

# The launcher's fingerprint (tools/launcher-fingerprint.ps1 says what it
# covers), embedded in the launcher and published beside the manifest.
. (Join-Path $PSScriptRoot 'launcher-fingerprint.ps1')
$LauncherFingerprint = Get-LauncherFingerprint
Write-Host "  launcher fingerprint : $LauncherFingerprint"

function Invoke-Publish {
    param(
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$Rid,
        [Parameter(Mandatory)][string]$OutputDirectory,
        [switch]$SingleFile,
        [switch]$ReadyToRun,
        [string]$Fingerprint
    )

    # The shared flags are part of the launcher fingerprint (launcher-fingerprint.ps1).
    $arguments = @(
        'publish', (Join-Path $RepoRoot $Project),
        '-r', $Rid,
        "-p:Version=$Version",
        '-o', $OutputDirectory
    ) + $CommonPublishFlags
    if ($SingleFile) { $arguments += '-p:PublishSingleFile=true' }
    # Only the launcher carries its fingerprint, so it can tell whether a
    # release's launcher is the one already running.
    if ($Fingerprint) { $arguments += "-p:LauncherFingerprint=$Fingerprint" }
    # Ahead-of-time compilation, for the hosts that run a frame loop: every
    # path a player walks for the first time -- the first object torn down,
    # the first portal, the first spell -- otherwise pays its compilation
    # inside the frame that needs it. It costs payload size, so the launcher
    # and the bake tool it co-deploys stay just-in-time: they start once and
    # have no frames to miss. Cross-RID is fine -- the self-contained restore
    # already pulls the target runtime pack and the compiler runs on the host.
    if ($ReadyToRun) { $arguments += '-p:PublishReadyToRun=true' }

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

function Copy-MacArtifacts {
    param([Parameter(Mandatory)][string]$SourceDirectory)

    $source = [IO.Path]::GetFullPath($SourceDirectory)
    if (-not (Test-Path -LiteralPath $source -PathType Container)) {
        throw "macOS release artifact directory '$source' does not exist."
    }

    foreach ($name in @('client-osx-arm64.zip', 'launcher-osx-arm64.zip')) {
        $from = Join-Path $source $name
        $to = Join-Path $BinRoot $name
        if (-not (Test-Path -LiteralPath $from -PathType Leaf)) {
            throw "macOS release artifact '$from' is missing."
        }
        Copy-Item -LiteralPath $from -Destination $to -Force
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
    Invoke-Publish 'src/AcDream.App/AcDream.App.csproj' $rid $clientDirectory -ReadyToRun
    Invoke-Publish 'src/AcDream.Headless/AcDream.Headless.csproj' $rid $clientDirectory -ReadyToRun

    Write-Host "[$rid] publishing launcher (+ co-deployed bake)..." -ForegroundColor Yellow
    Invoke-Publish 'src/AcDream.Launcher/AcDream.Launcher.csproj' $rid $launcherDirectory -Fingerprint $LauncherFingerprint

    $clientZip = Join-Path $BinRoot "client-$rid.zip"
    $launcherZip = Join-Path $BinRoot "launcher-$rid.zip"

    Write-Host "[$rid] packing..." -ForegroundColor Yellow
    if ($rid -eq 'osx-arm64') {
        $publishedAppHost = Join-Path $clientDirectory 'AcDream.App'
        $macClient = Join-Path $clientDirectory 'acdream-client'
        if (-not (Test-Path -LiteralPath $publishedAppHost -PathType Leaf)) {
            throw "macOS graphical client publish output '$publishedAppHost' is missing."
        }
        if (Test-Path -LiteralPath $macClient) {
            throw "macOS graphical client destination '$macClient' already exists."
        }
        Move-Item -LiteralPath $publishedAppHost -Destination $macClient
        & (Join-Path $RepoRoot 'tools/package-macos-vulkan.ps1') -ClientDirectory $clientDirectory
        if ($LASTEXITCODE) { throw 'macOS Vulkan dependency packaging failed.' }
    }
    if ($rid -eq 'osx-x64') {
        # osx-x64: same client staging as arm64, then the pinned x86_64 Vulkan runtime.
        $publishedAppHost = Join-Path $clientDirectory 'AcDream.App'
        $macClient = Join-Path $clientDirectory 'acdream-client'
        if (-not (Test-Path -LiteralPath $publishedAppHost -PathType Leaf)) {
            throw "macOS graphical client publish output '$publishedAppHost' is missing."
        }
        if (Test-Path -LiteralPath $macClient) {
            throw "macOS graphical client destination '$macClient' already exists."
        }
        Move-Item -LiteralPath $publishedAppHost -Destination $macClient
        $runtime = if ([string]::IsNullOrWhiteSpace($MacVulkanRuntimeDirectory)) {
            Join-Path $RepoRoot 'artifacts/macos-x64-vulkan'
        } else {
            $MacVulkanRuntimeDirectory
        }
        & (Join-Path $RepoRoot 'tools/build-macos-x64-vulkan.ps1') -OutputDirectory $runtime
        & (Join-Path $RepoRoot 'tools/package-macos-x64-vulkan.ps1') `
            -ClientDirectory $clientDirectory -VulkanRuntimeDirectory $runtime
        if ($LASTEXITCODE) { throw 'osx-x64 Vulkan dependency packaging failed.' }
    }
    $clientExecutables = if ($rid -eq 'osx-arm64') {
        @('acdream-client', 'acdream-headless')
    } else {
        @("AcDream.App$suffix", "acdream-headless$suffix")
    }
    # osx-x64: same client executables as arm64.
    if ($rid -eq 'osx-x64') { $clientExecutables = @('acdream-client', 'acdream-headless') }
    New-PayloadZip $clientDirectory $clientZip $clientExecutables
    if ($rid -eq 'osx-arm64') {
        & (Join-Path $RepoRoot 'tools/package-macos-launcher.ps1') `
            -PublishDirectory $launcherDirectory `
            -OutputDirectory $Staging `
            -Version $Version `
            -ZipPath $launcherZip
        if ($LASTEXITCODE) { throw 'macOS launcher bundle packaging failed.' }
    } else {
        New-PayloadZip $launcherDirectory $launcherZip @("acdream-launcher$suffix", "acdream-bake$suffix")
    }
    if ($rid -eq 'osx-x64') {
        # osx-x64: rebuilds the launcher zip as an app bundle, same as arm64.
        & (Join-Path $RepoRoot 'tools/package-macos-launcher.ps1') `
            -PublishDirectory $launcherDirectory `
            -OutputDirectory $Staging `
            -Version $Version `
            -ZipPath $launcherZip
        if ($LASTEXITCODE) { throw 'macOS launcher bundle packaging failed.' }
    }

    $clients[$rid] = Get-Artifact $clientZip "$RawBase/client-$rid.zip"
    $launchers[$rid] = Get-Artifact $launcherZip "$RawBase/launcher-$rid.zip"
}

if (-not [string]::IsNullOrWhiteSpace($MacArtifactsDirectory)) {
    Copy-MacArtifacts $MacArtifactsDirectory
    $clients['osx-arm64'] = Get-Artifact `
        (Join-Path $BinRoot 'client-osx-arm64.zip') `
        "$RawBase/client-osx-arm64.zip"
    $launchers['osx-arm64'] = Get-Artifact `
        (Join-Path $BinRoot 'launcher-osx-arm64.zip') `
        "$RawBase/launcher-osx-arm64.zip"

    # osx-x64: copies its release pair only when both zips are present.
    $x64Source = [IO.Path]::GetFullPath($MacArtifactsDirectory)
    $x64Names = @('client-osx-x64.zip', 'launcher-osx-x64.zip')
    $x64Present = @($x64Names | Where-Object { Test-Path -LiteralPath (Join-Path $x64Source $_) -PathType Leaf })
    if ($x64Present.Count -eq $x64Names.Count) {
        foreach ($name in $x64Names) {
            Copy-Item -LiteralPath (Join-Path $x64Source $name) -Destination (Join-Path $BinRoot $name) -Force
        }
        $clients['osx-x64'] = Get-Artifact `
            (Join-Path $BinRoot 'client-osx-x64.zip') `
            "$RawBase/client-osx-x64.zip"
        $launchers['osx-x64'] = Get-Artifact `
            (Join-Path $BinRoot 'launcher-osx-x64.zip') `
            "$RawBase/launcher-osx-x64.zip"
    } elseif ($x64Present.Count -ne 0) {
        throw "macOS release artifacts for 'osx-x64' are incomplete in '$x64Source'."
    }
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

# Beside manifest.json, not in it: a launcher from before fingerprints reads
# manifest.json strictly and would refuse a field it does not know. A launcher
# that reads this file updates itself only when the fingerprint differs from
# its own.
$fingerprintDocument = [ordered]@{
    schemaVersion = 1
    version       = $Version
    fingerprint   = $LauncherFingerprint
}
[IO.File]::WriteAllText(
    (Join-Path $BinRoot 'launcher-fingerprint.json'),
    (($fingerprintDocument | ConvertTo-Json) + "`n"),
    [Text.UTF8Encoding]::new($false))

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
