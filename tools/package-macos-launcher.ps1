[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$ZipPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7 -or -not $IsMacOS) {
    throw 'package-macos-launcher requires PowerShell 7 on macOS.'
}

$publish = [IO.Path]::GetFullPath($PublishDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
$zip = [IO.Path]::GetFullPath($ZipPath)
if (-not (Test-Path -LiteralPath $publish -PathType Container)) {
    throw "Published launcher directory '$publish' does not exist."
}

$launcher = Join-Path $publish 'acdream-launcher'
$bake = Join-Path $publish 'acdream-bake'
foreach ($required in @($launcher, $bake)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Published launcher output is missing '$required'."
    }
}

$bundle = Join-Path $output 'OpenAC.app'
if (Test-Path -LiteralPath $bundle) {
    Remove-Item -LiteralPath $bundle -Recurse -Force
}
if (Test-Path -LiteralPath $zip) {
    Remove-Item -LiteralPath $zip -Force
}

$contents = Join-Path $bundle 'Contents'
$macos = Join-Path $contents 'MacOS'
$resources = Join-Path $contents 'Resources'
New-Item -ItemType Directory -Path $macos -Force | Out-Null
New-Item -ItemType Directory -Path $resources -Force | Out-Null

# PublishSingleFile keeps the launcher and bake executable-shaped. Preserve the
# complete publish directory in case a future Avalonia runtime emits a native
# sidecar; every entry remains inside Contents/MacOS where it is part of the
# signed application payload.
Get-ChildItem -LiteralPath $publish -Force | ForEach-Object {
    if ($_.Extension -ne '.pdb') {
        Copy-Item -LiteralPath $_.FullName -Destination $macos -Recurse -Force
    }
}
& /bin/chmod 755 (Join-Path $macos 'acdream-launcher') (Join-Path $macos 'acdream-bake')
if ($LASTEXITCODE -ne 0) { throw 'chmod failed for a macOS launcher executable.' }

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$iconSources = @{
    'icon_16x16.png' = 'acdream-launcher-16.png'
    'icon_16x16@2x.png' = 'acdream-launcher-32.png'
    'icon_32x32.png' = 'acdream-launcher-32.png'
    'icon_32x32@2x.png' = 'acdream-launcher-64.png'
    'icon_128x128.png' = 'acdream-launcher-128.png'
    'icon_128x128@2x.png' = 'acdream-launcher-256.png'
    'icon_256x256.png' = 'acdream-launcher-256.png'
    'icon_256x256@2x.png' = 'acdream-launcher-512.png'
    'icon_512x512.png' = 'acdream-launcher-512.png'
    'icon_512x512@2x.png' = 'acdream-launcher-1024.png'
}
$iconSet = Join-Path $output 'OpenAC.iconset'
if (Test-Path -LiteralPath $iconSet) {
    Remove-Item -LiteralPath $iconSet -Recurse -Force
}
New-Item -ItemType Directory -Path $iconSet -Force | Out-Null
foreach ($entry in $iconSources.GetEnumerator()) {
    $source = Join-Path $repoRoot (Join-Path 'assets/icons' $entry.Value)
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Launcher icon source '$source' is missing."
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $iconSet $entry.Key)
}
$icon = Join-Path $resources 'OpenAC.icns'
& /usr/bin/iconutil --convert icns --output $icon $iconSet
if ($LASTEXITCODE -ne 0) { throw 'iconutil could not create OpenAC.icns.' }
Remove-Item -LiteralPath $iconSet -Recurse -Force

$plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDisplayName</key>
  <string>OpenAC</string>
  <key>CFBundleExecutable</key>
  <string>acdream-launcher</string>
  <key>CFBundleIconFile</key>
  <string>OpenAC.icns</string>
  <key>CFBundleIdentifier</key>
  <string>org.openac.launcher</string>
  <key>CFBundleName</key>
  <string>OpenAC</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>$Version</string>
  <key>CFBundleVersion</key>
  <string>$Version</string>
  <key>LSMinimumSystemVersion</key>
  <string>14.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
</dict>
</plist>
"@
[IO.File]::WriteAllText(
    (Join-Path $contents 'Info.plist'),
    $plist,
    [Text.UTF8Encoding]::new($false))

# The graphical client uses mach_absolute_time for frame pacing on Apple
# silicon. The valid reason declares elapsed-time calculations for timers;
# none of that timing information is used for device fingerprinting.
$privacyManifest = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>NSPrivacyAccessedAPITypes</key>
  <array>
    <dict>
      <key>NSPrivacyAccessedAPIType</key>
      <string>NSPrivacyAccessedAPICategorySystemBootTime</string>
      <key>NSPrivacyAccessedAPITypeReasons</key>
      <array>
        <string>35F9.1</string>
      </array>
    </dict>
  </array>
</dict>
</plist>
"@
[IO.File]::WriteAllText(
    (Join-Path $resources 'PrivacyInfo.xcprivacy'),
    $privacyManifest,
    [Text.UTF8Encoding]::new($false))

& /usr/bin/plutil -lint (Join-Path $contents 'Info.plist')
if ($LASTEXITCODE -ne 0) { throw 'OpenAC.app Info.plist did not validate.' }
foreach ($required in @(
    (Join-Path $macos 'acdream-launcher'),
    (Join-Path $macos 'acdream-bake'),
    (Join-Path $resources 'OpenAC.icns'),
    (Join-Path $resources 'PrivacyInfo.xcprivacy'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "OpenAC.app is missing '$required'."
    }
}

# Sign every executable nested in the bundle before signing the bundle itself.
# This is deliberately ad-hoc: CI has no Apple Developer certificate, yet
# amfid still requires a valid local signature for the renamed app hosts.
foreach ($candidate in Get-ChildItem -LiteralPath $macos -File -Recurse | Sort-Object FullName) {
    $kind = (& /usr/bin/file -b $candidate.FullName).Trim()
    if ($LASTEXITCODE -ne 0) { throw "file could not inspect '$($candidate.FullName)'." }
    if (-not $kind.StartsWith('Mach-O', [StringComparison]::Ordinal)) { continue }
    & /usr/bin/codesign --force --sign - --timestamp=none $candidate.FullName
    if ($LASTEXITCODE -ne 0) { throw "Could not ad-hoc sign '$($candidate.FullName)'." }
    & /usr/bin/codesign --verify --strict $candidate.FullName
    if ($LASTEXITCODE -ne 0) { throw "Nested signature validation failed for '$($candidate.FullName)'." }
}
& /usr/bin/codesign --force --sign - --timestamp=none $bundle
if ($LASTEXITCODE -ne 0) { throw 'Could not ad-hoc sign OpenAC.app.' }
& /usr/bin/codesign --verify --deep --strict $bundle
if ($LASTEXITCODE -ne 0) { throw 'OpenAC.app signature validation failed.' }

New-Item -ItemType Directory -Path (Split-Path -Parent $zip) -Force | Out-Null
& /usr/bin/ditto -c -k --sequesterRsrc --keepParent $bundle $zip
if ($LASTEXITCODE -ne 0) { throw 'ditto could not archive OpenAC.app.' }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    if ($archive.Entries.Count -eq 0 -or @($archive.Entries | Where-Object {
            -not $_.FullName.StartsWith('OpenAC.app/', [StringComparison]::Ordinal)
        }).Count -ne 0) {
        throw 'The macOS launcher ZIP must contain only the OpenAC.app bundle.'
    }
} finally {
    $archive.Dispose()
}

Write-Host "Created macOS launcher bundle: $bundle"
Write-Host "Created macOS launcher archive: $zip"
