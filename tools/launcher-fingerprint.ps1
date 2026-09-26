<#
.SYNOPSIS
The launcher's fingerprint: one SHA-256 over the launcher's own code and what
decides whether its preparation tool still prepares valid data.

.DESCRIPTION
A launcher whose embedded fingerprint matches the one a release publishes does
not update itself. The inputs are:

- the launcher's own projects: AcDream.Launcher, AcDream.Launcher.Core and
  AcDream.Platform (the install paths and platform seams the launcher is
  built on);
- the preparation tool's own project, AcDream.Bake;
- the prepared-data recipe: the pak format version and the bake tool version
  in AcDream.Content (Pak/PakFormat.cs), which the prepared data and the
  session config carry and which already decide when prepared data must be
  rebuilt;
- the build-wide files (global.json, Directory.Packages.props, NuGet.Config,
  and Directory.Build.props without its release version), the launcher
  icons, and the scripts that build and package the payload;
- the .NET SDK the build resolves and the runtime it bundles, which a
  self-contained launcher carries, so a runtime security update reaches it;
- the dotnet publish flags, and the way the workflow runs the publish script.

The shared game code (AcDream.Core, AcDream.Content apart from the recipe,
and the rest) is deliberately not an input: the bake tool an older launcher
carries still prepares valid data until the recipe version goes up. The
release version alone changes nothing. Files are read as git object ids from
a committed tree, so the value is the same on every runner and line-ending
setting.

.PARAMETER ListInputs
Prints each input and its value instead of the fingerprint.

.PARAMETER Revision
The commit or tree to read; HEAD by default.
#>
[CmdletBinding()]
param(
    [switch]$ListInputs,
    [string]$Revision = 'HEAD'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$FingerprintRepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

# The dotnet publish flags every payload shares, whatever its project; the
# release version, the RID and the output folder are added by the caller.
$CommonPublishFlags = @(
    '-c', 'Release',
    '--self-contained', 'true',
    # No SourceLink '+<sha>' suffix: LauncherVersion parses this as SemVer.
    '-p:IncludeSourceRevisionInInformationalVersion=false',
    '--nologo'
)

# The launcher's own code, the bake tool's own project, and the build and
# packaging files; the shared game code the bake tool links is left out.
$LauncherOwnPaths = @(
    'src/AcDream.Launcher',
    'src/AcDream.Launcher.Core',
    'src/AcDream.Platform',
    'src/AcDream.Bake',
    'assets/icons',
    'global.json',
    'Directory.Packages.props',
    'NuGet.Config',
    'tools/publish-bin.ps1',
    'tools/package-macos-launcher.ps1',
    'tools/launcher-fingerprint.ps1'
)

function Read-CommittedText([string]$At, [string]$Path) {
    $text = (& git -C $FingerprintRepoRoot show "${At}:$Path") -join "`n"
    if ($LASTEXITCODE) { throw "Could not read '$Path' at '$At' for the launcher fingerprint." }
    return $text
}

function Get-LauncherFingerprintInputs([string]$At = 'HEAD') {
    $inputs = [ordered]@{}
    foreach ($path in $LauncherOwnPaths) {
        $objectId = (& git -C $FingerprintRepoRoot rev-parse "${At}:$path")
        if ($LASTEXITCODE) { throw "Could not read '$path' at '$At' for the launcher fingerprint." }
        $inputs[$path] = "$objectId".Trim()
    }

    # The release version alone must not change the fingerprint; everything
    # else in the build-wide properties counts.
    $props = Read-CommittedText $At 'Directory.Build.props'
    $inputs['Directory.Build.props'] = [regex]::Replace($props, '<Version>[^<]*</Version>', '<Version />')

    # The recipe the prepared data is built to: its values, not the rest of the file.
    $pak = Read-CommittedText $At 'src/AcDream.Content/Pak/PakFormat.cs'
    foreach ($name in @('CurrentFormatVersion', 'CurrentBakeToolVersion')) {
        $match = [regex]::Match($pak, "const\s+uint\s+$name\s*=\s*(\d+)")
        if (-not $match.Success) { throw "Could not find PakFormat.$name for the launcher fingerprint." }
        $inputs["recipe.$name"] = $match.Groups[1].Value
    }

    Push-Location $FingerprintRepoRoot
    try {
        $sdk = (& dotnet --version)
        if ($LASTEXITCODE) { throw 'Could not resolve the .NET SDK for the launcher fingerprint.' }
        $runtime = (& dotnet msbuild 'src/AcDream.Launcher/AcDream.Launcher.csproj' -nologo '-getProperty:BundledNETCoreAppPackageVersion')
        if ($LASTEXITCODE) { throw 'Could not read the bundled .NET runtime version for the launcher fingerprint.' }
    } finally {
        Pop-Location
    }
    $inputs['dotnet-sdk'] = "$sdk".Trim()
    $inputs['dotnet-runtime'] = "$runtime".Trim()
    $inputs['publish-flags'] = $CommonPublishFlags -join ' '

    # How the workflow runs the publish script, without the per-release parts
    # (version and asset address) and without comments.
    $workflow = Read-CommittedText $At '.github/workflows/ci.yml'
    $invocations = @($workflow -split "`n" |
        Where-Object { $_ -match 'publish-bin\.ps1' -and $_.Trim() -notmatch '^#' } |
        ForEach-Object {
            $line = $_.Trim() -replace '-Version \S+', '' -replace '-BaseUrl "[^"]*"', ''
            ($line -replace '\s+', ' ').Trim()
        })
    $inputs['workflow-publish'] = $invocations -join "`n"

    return $inputs
}

function Get-LauncherFingerprint([string]$At = 'HEAD') {
    $inputs = Get-LauncherFingerprintInputs $At
    $text = ($inputs.GetEnumerator() | ForEach-Object { "$($_.Key) $($_.Value)" }) -join "`n"
    $bytes = [Text.Encoding]::UTF8.GetBytes($text + "`n")
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

# Run directly, print; dot-sourced (publish-bin.ps1), only define.
if ($MyInvocation.InvocationName -ne '.') {
    if ($ListInputs) {
        (Get-LauncherFingerprintInputs $Revision).GetEnumerator() |
            ForEach-Object { "$($_.Key)`t$($_.Value -replace "`n", ' | ')" }
    } else {
        Get-LauncherFingerprint $Revision
    }
}
