<#
.SYNOPSIS
    Regenerates every neutral and release-RID NuGet lock file.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$solutionPath = Join-Path $repoRoot 'AcDream.slnx'

function Invoke-Restore {
    param([Parameter(Mandatory)] [string[]]$Arguments)

    Write-Host "dotnet $($Arguments -join ' ')"
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed with exit code $LASTEXITCODE."
    }
}

Push-Location $repoRoot
try {
    Invoke-Restore -Arguments @('restore', $solutionPath, '--force-evaluate', '--nologo')

    $sourceProjects = @(
        Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src') -Recurse -Filter '*.csproj' -File |
            Sort-Object FullName
    )
    foreach ($runtimeIdentifier in @('win-x64', 'linux-x64')) {
        foreach ($project in $sourceProjects) {
            Invoke-Restore -Arguments @(
                'restore'
                $project.FullName
                '-r'
                $runtimeIdentifier
                '--force-evaluate'
                '--nologo'
            )
        }
    }
} finally {
    Pop-Location
}
