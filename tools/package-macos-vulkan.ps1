[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ClientDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7 -or -not $IsMacOS) {
    throw 'package-macos-vulkan requires PowerShell 7 on macOS.'
}

$expected = @{
    'molten-vk' = '1.4.2'
    'vulkan-loader' = '1.4.357.0'
}
$client = [IO.Path]::GetFullPath($ClientDirectory)
if (-not (Test-Path -LiteralPath $client -PathType Container)) {
    throw "Published client directory '$client' does not exist."
}

function Get-BrewFormula {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Version)

    $json = & brew info --json=v2 $Name
    if ($LASTEXITCODE -ne 0) { throw "brew info failed for '$Name'." }
    $formula = ($json | ConvertFrom-Json).formulae | Where-Object { $_.name -eq $Name }
    if ($null -eq $formula) { throw "brew did not report formula '$Name'." }
    if ($formula.versions.stable -ne $Version) {
        throw "Homebrew formula '$Name' stable version '$($formula.versions.stable)' does not match pinned $Version."
    }
    $installed = @($formula.installed | Where-Object { $_.version -eq $Version })
    if ($installed.Count -ne 1) {
        throw "Expected Homebrew '$Name' version $Version, found '$($formula.installed.version -join ', ')'."
    }

    $prefix = (& brew --prefix $Name).Trim()
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $prefix -PathType Container)) {
        throw "Homebrew prefix for '$Name' is unavailable."
    }
    $bottleTag = Get-CurrentBottleTag
    $bottle = if ($null -eq $bottleTag) {
        $null
    } else {
        $formula.bottle.stable.files.PSObject.Properties |
            Where-Object Name -eq $bottleTag |
            Select-Object -First 1
    }
    return [pscustomobject]@{
        Name = $Name
        Version = $Version
        Prefix = [IO.Path]::GetFullPath($prefix)
        SourceUrl = [string]$formula.urls.stable.url
        SourceSha256 = [string]$formula.urls.stable.checksum
        InstalledPouredFromBottle = [bool]$installed[0].poured_from_bottle
        BottleMetadataTag = $bottleTag
        BottleMetadataUrl = if ($null -eq $bottle) { $null } else { [string]$bottle.Value.url }
        BottleMetadataSha256 = if ($null -eq $bottle) { $null } else { [string]$bottle.Value.sha256 }
    }
}

function Get-CurrentBottleTag {
    $major = ((& /usr/bin/sw_vers -productVersion).Trim().Split('.')[0])
    if ($LASTEXITCODE -ne 0) { throw 'Could not determine the macOS release for Homebrew metadata.' }
    switch ($major) {
        '14' { return 'arm64_sonoma' }
        '15' { return 'arm64_sequoia' }
        '26' { return 'arm64_tahoe' }
        default { return $null }
    }
}

function Resolve-PhysicalFile {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required native library '$Path' is missing."
    }
    $item = Get-Item -LiteralPath $Path -Force
    $target = $item.ResolveLinkTarget($true)
    if ($null -eq $target) { return $item.FullName }
    return $target.FullName
}

function Get-LinkedHomebrewLibraries {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$HomebrewPrefix
    )

    $output = & /usr/bin/otool -L $Path
    if ($LASTEXITCODE -ne 0) { throw "otool failed for '$Path'." }
    return @($output | Select-Object -Skip 1 | ForEach-Object {
        $value = $_.Trim()
        $index = $value.IndexOf(' (', [StringComparison]::Ordinal)
        if ($index -gt 0) { $value = $value.Substring(0, $index) }
        if ($value.StartsWith($HomebrewPrefix + '/', [StringComparison]::Ordinal)) { $value }
    } | Where-Object { $_ })
}

$molten = Get-BrewFormula 'molten-vk' ($expected['molten-vk'])
$loader = Get-BrewFormula 'vulkan-loader' ($expected['vulkan-loader'])
$homebrewPrefix = (& brew --prefix).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the Homebrew prefix.' }

$frameworks = Join-Path $client 'Frameworks'
$clientResources = Join-Path $client 'Resources'
$resources = Join-Path $clientResources 'vulkan'
$icdDirectory = Join-Path $resources 'icd.d'
if (Test-Path -LiteralPath $frameworks) { Remove-Item -LiteralPath $frameworks -Recurse -Force }
if (Test-Path -LiteralPath $resources) { Remove-Item -LiteralPath $resources -Recurse -Force }
New-Item -ItemType Directory -Path $frameworks -Force | Out-Null
New-Item -ItemType Directory -Path $icdDirectory -Force | Out-Null

# The client calls mach_absolute_time while pacing frames. Its own privacy
# manifest is required independently of the launcher's app-bundle manifest.
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
    (Join-Path $clientResources 'PrivacyInfo.xcprivacy'),
    $privacyManifest,
    [Text.UTF8Encoding]::new($false))

$queue = [Collections.Generic.Queue[string]]::new()
$queue.Enqueue((Resolve-PhysicalFile (Join-Path $loader.Prefix 'lib/libvulkan.1.dylib')))
$queue.Enqueue((Resolve-PhysicalFile (Join-Path $molten.Prefix 'lib/libMoltenVK.dylib')))
$copied = @{}
while ($queue.Count -gt 0) {
    $source = $queue.Dequeue()
    $name = [IO.Path]::GetFileName($source)
    if ($copied.ContainsKey($name)) { continue }
    $destination = Join-Path $frameworks $name
    Copy-Item -LiteralPath $source -Destination $destination -Force
    $copied[$name] = $destination
    foreach ($dependency in Get-LinkedHomebrewLibraries $source $homebrewPrefix) {
        $queue.Enqueue((Resolve-PhysicalFile $dependency))
    }
}

# Resolve-PhysicalFile deliberately dereferences Homebrew's versioned symlinks
# before copying. Keep these two stable filenames as physical files because the
# launched client and the generated ICD manifest address them by these names.
foreach ($required in @(
    @{ Source = (Resolve-PhysicalFile (Join-Path $loader.Prefix 'lib/libvulkan.1.dylib')); Name = 'libvulkan.1.dylib' },
    @{ Source = (Resolve-PhysicalFile (Join-Path $molten.Prefix 'lib/libMoltenVK.dylib')); Name = 'libMoltenVK.dylib' })) {
    $destination = Join-Path $frameworks $required.Name
    Copy-Item -LiteralPath $required.Source -Destination $destination -Force
    $copied[$required.Name] = $destination
}

foreach ($destination in $copied.Values) {
    & /usr/bin/install_name_tool -id ('@loader_path/' + [IO.Path]::GetFileName($destination)) $destination
    if ($LASTEXITCODE -ne 0) { throw "Could not set bundled library identity for '$destination'." }
    $links = & /usr/bin/otool -L $destination
    if ($LASTEXITCODE -ne 0) { throw "otool failed for '$destination'." }
    foreach ($line in $links | Select-Object -Skip 1) {
        $dependency = $line.Trim()
        $index = $dependency.IndexOf(' (', [StringComparison]::Ordinal)
        if ($index -gt 0) { $dependency = $dependency.Substring(0, $index) }
        if (-not $dependency.StartsWith($homebrewPrefix + '/', [StringComparison]::Ordinal)) { continue }
        $replacement = '@loader_path/' + [IO.Path]::GetFileName($dependency)
        & /usr/bin/install_name_tool -change $dependency $replacement $destination
        if ($LASTEXITCODE -ne 0) {
            throw "Could not rewrite Homebrew dependency '$dependency' in '$destination'."
        }
    }
}

foreach ($native in @($copied.Values) + @(
        (Join-Path $client 'acdream-client'),
        (Join-Path $client 'acdream-headless'))) {
    if (-not (Test-Path -LiteralPath $native -PathType Leaf)) {
        throw "Native macOS payload '$native' is missing."
    }
    & /usr/bin/codesign --force --sign - --timestamp=none $native
    if ($LASTEXITCODE -ne 0) { throw "Could not ad-hoc sign '$native'." }
    & /usr/bin/codesign --verify --strict $native
    if ($LASTEXITCODE -ne 0) { throw "Ad-hoc signature validation failed for '$native'." }
}

foreach ($destination in $copied.Values) {
    $links = & /usr/bin/otool -L $destination
    if ($LASTEXITCODE -ne 0 -or ($links -join "`n").Contains($homebrewPrefix, [StringComparison]::Ordinal)) {
        throw "Bundled native library '$destination' still links to Homebrew."
    }
}

$icd = @"
{
  "file_format_version": "1.0.0",
  "ICD": {
    "library_path": "../../../Frameworks/libMoltenVK.dylib",
    "api_version": "1.3.0"
  }
}
"@
[IO.File]::WriteAllText((Join-Path $icdDirectory 'MoltenVK_icd.json'), $icd, [Text.UTF8Encoding]::new($false))

$licenseDirectory = Join-Path $resources 'licenses'
New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
foreach ($component in @($molten, $loader)) {
    $license = Get-ChildItem -LiteralPath $component.Prefix -File -Recurse |
        Where-Object { $_.Name -match '^(LICENSE|COPYING)(\..*)?$' } |
        Select-Object -First 1
    if ($null -eq $license) {
        throw "No installed license text was found for Homebrew '$($component.Name)'."
    }
    Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $licenseDirectory ($component.Name + '-LICENSE')) -Force
}

$metadata = [ordered]@{
    schemaVersion = 1
    components = @(@($molten, $loader) | ForEach-Object {
        [ordered]@{
            name = $_.Name
            version = $_.Version
            sourceFormula = [ordered]@{
                url = $_.SourceUrl
                sha256 = $_.SourceSha256
            }
            installed = [ordered]@{
                prefix = $_.Prefix
                pouredFromBottle = $_.InstalledPouredFromBottle
            }
            bottleMetadata = [ordered]@{
                tag = $_.BottleMetadataTag
                url = $_.BottleMetadataUrl
                sha256 = $_.BottleMetadataSha256
            }
            license = 'Apache-2.0'
        }
    })
}
$metadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $resources 'dependencies.json') -Encoding utf8NoBOM
Write-Host "Bundled $($copied.Count) validated Homebrew native library file(s) for Apple-silicon Vulkan."
