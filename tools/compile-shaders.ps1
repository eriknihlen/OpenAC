[CmdletBinding()]
param(
    [string]$ShadersDirectory,
    [string]$OutputDirectory,
    [bool]$PreferSdk = $true,
    [switch]$ForceRecompile
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $ShadersDirectory) {
    $ShadersDirectory = [System.IO.Path]::Combine(
        $repo, 'src', 'AcDream.App', 'Rendering', 'Shaders')
}
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $ShadersDirectory 'spv'
}

function Write-Step($message) { Write-Host "[shaders] $message" }

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

# --- 1. Locate glslc, if the machine has a Vulkan SDK -------------------------
$glslc = $null
if ($PreferSdk) {
    $onPath = Get-Command glslc -ErrorAction SilentlyContinue
    if ($onPath) {
        $glslc = $onPath.Source
    }
    elseif ($env:VULKAN_SDK) {
        # 'Bin/glslc.exe' on Windows, 'bin/glslc' on the SDK's Linux layout.
        $candidates = @(
            [System.IO.Path]::Combine($env:VULKAN_SDK, 'Bin', 'glslc.exe'),
            [System.IO.Path]::Combine($env:VULKAN_SDK, 'bin', 'glslc')
        )
        foreach ($candidate in $candidates) {
            if (Test-Path $candidate) { $glslc = $candidate; break }
        }
    }
}

if ($glslc) {
    Write-Step "a Vulkan SDK glslc was found at $glslc (recorded; the managed compiler still runs)"
}
else {
    Write-Step 'no Vulkan SDK glslc found; using the managed Silk.NET.Shaderc compiler'
}

$tool = [System.IO.Path]::Combine(
    $repo, 'tools', 'ShaderCompiler', 'ShaderCompiler.csproj')

$architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
$ridArchitecture = switch ($architecture) {
    'X64' { 'x64' }
    'Arm64' { 'arm64' }
    'X86' { 'x86' }
    default { throw "No shaderc native is published for processor architecture $architecture." }
}
$ridOs =
    if ($PSVersionTable.PSVersion.Major -lt 6 -or $IsWindows) { 'win' }
    elseif ($IsMacOS) { 'osx' }
    elseif ($IsLinux) { 'linux' }
    else { throw 'Unrecognised operating system; cannot choose a shaderc native.' }
$rid = "$ridOs-$ridArchitecture"

$toolDirectory = [System.IO.Path]::Combine(
    $repo, 'tools', 'ShaderCompiler', 'bin', 'publish', $rid)
Write-Step "publishing the shader compiler for $rid"
& dotnet publish $tool -c Release -r $rid --self-contained false -o $toolDirectory --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Shader compiler publish failed with exit code $LASTEXITCODE." }

$binary = Join-Path $toolDirectory 'AcDream.Tools.ShaderCompiler.dll'
if (-not (Test-Path $binary)) { throw "Shader compiler not found at $binary." }

$nativeName =
    if ($ridOs -eq 'win') { 'shaderc_shared.dll' }
    elseif ($ridOs -eq 'osx') { 'libshaderc_shared.dylib' }
    else { 'libshaderc_shared.so' }
$native = Join-Path $toolDirectory $nativeName
if (-not (Test-Path $native)) {
    throw ("The shaderc native $nativeName is not beside the shader compiler at " +
        "$toolDirectory. The Silk.NET.Shaderc.Native package did not publish a " +
        "$rid asset.")
}
Write-Step "shaderc native: $native"

Write-Step "compiling $ShadersDirectory -> $OutputDirectory"
$compilerArguments = @($binary, $ShadersDirectory, $OutputDirectory)
if ($ForceRecompile) { $compilerArguments += '--force' }
& dotnet @compilerArguments
if ($LASTEXITCODE -ne 0) { throw "Shader compilation failed with exit code $LASTEXITCODE." }

Write-Step 'done'
