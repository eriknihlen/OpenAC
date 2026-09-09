[CmdletBinding()]
param(
    [string]$ArtifactsDirectory = 'artifacts/release-gate',
    [int]$RestoreTimeoutSeconds = 600,
    [int]$BuildTimeoutSeconds = 900,
    [int]$TestTimeoutSeconds = 600,
    [int]$HangTimeoutSeconds = 180,
    [string]$TestFilter = 'Lane!=InstalledDat&Lane!=PreparedPackage&Lane!=Live&Lane!=Manual&Lane!=Timing&Lane!=Windows&Lane!=Linux&Lane!=Vulkan&Lane!=SystemFont&Purpose!=Diagnostic&Status!=KnownFailure',
    [switch]$SkipRestore,
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$solutionPath = Join-Path $repoRoot 'AcDream.slnx'
$artifactRoot = if ([IO.Path]::IsPathRooted($ArtifactsDirectory)) {
    [IO.Path]::GetFullPath($ArtifactsDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $ArtifactsDirectory))
}
$logsDirectory = Join-Path $artifactRoot 'logs'
$resultsDirectory = Join-Path $artifactRoot 'test-results'

if ($RestoreTimeoutSeconds -le 0 -or $BuildTimeoutSeconds -le 0 -or
    $TestTimeoutSeconds -le 0 -or $HangTimeoutSeconds -le 0) {
    throw 'Every timeout must be a positive number of seconds.'
}
if ($TestTimeoutSeconds -le $HangTimeoutSeconds) {
    throw 'TestTimeoutSeconds must exceed HangTimeoutSeconds so blame-hang can collect before the outer watchdog fires.'
}
if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
    throw "Solution not found at '$solutionPath'."
}

foreach ($directory in @($logsDirectory, $resultsDirectory)) {
    if (Test-Path -LiteralPath $directory) {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
}
foreach ($fileName in @('environment.txt', 'release-gate-summary.json', 'SHA256SUMS.txt')) {
    $filePath = Join-Path $artifactRoot $fileName
    if (Test-Path -LiteralPath $filePath -PathType Leaf) {
        Remove-Item -LiteralPath $filePath -Force
    }
}
New-Item -ItemType Directory -Force -Path $logsDirectory, $resultsDirectory | Out-Null
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function ConvertTo-CommandText {
    param([string[]]$Arguments)

    $rendered = foreach ($argument in $Arguments) {
        if ($argument -match '[\s\"]') {
            '"' + $argument.Replace('"', '\"') + '"'
        } else {
            $argument
        }
    }
    return 'dotnet ' + ($rendered -join ' ')
}

function Invoke-BoundedDotnet {
    param(
        [Parameter(Mandatory)] [string]$Label,
        [Parameter(Mandatory)] [string[]]$Arguments,
        [Parameter(Mandatory)] [int]$TimeoutSeconds
    )

    $command = ConvertTo-CommandText -Arguments $Arguments
    $logPath = Join-Path $logsDirectory "$Label.log"
    $startedUtc = [DateTimeOffset]::UtcNow
    Write-Host "[$Label] $command"

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'dotnet'
    $startInfo.WorkingDirectory = $repoRoot
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw "[$Label] failed to start dotnet."
    }

    $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
    $standardErrorTask = $process.StandardError.ReadToEndAsync()
    $completed = $process.WaitForExit($TimeoutSeconds * 1000)
    $timedOut = -not $completed
    if ($timedOut) {
        Write-Error "[$Label] exceeded its hard ${TimeoutSeconds}s timeout; killing PID $($process.Id) and its descendants." -ErrorAction Continue
        try {
            $process.Kill($true)
        } catch {
            Write-Error "[$Label] process-tree kill failed: $($_.Exception.Message)" -ErrorAction Continue
        }
    }

    if ($timedOut -and -not $process.WaitForExit(30000)) {
        throw "[$Label] did not exit within 30 seconds after process-tree termination was requested."
    }
    if (-not $timedOut) {
        $process.WaitForExit()
    }
    $standardOutput = $standardOutputTask.GetAwaiter().GetResult()
    $standardError = $standardErrorTask.GetAwaiter().GetResult()
    $endedUtc = [DateTimeOffset]::UtcNow
    $exitCode = if ($timedOut) { 124 } else { $process.ExitCode }

    $log = @(
        "label: $Label"
        "command: $command"
        "startedUtc: $($startedUtc.ToString('O'))"
        "endedUtc: $($endedUtc.ToString('O'))"
        "durationSeconds: $([Math]::Round(($endedUtc - $startedUtc).TotalSeconds, 3))"
        "hardTimeoutSeconds: $TimeoutSeconds"
        "timedOut: $timedOut"
        "exitCode: $exitCode"
        ''
        '--- stdout ---'
        $standardOutput
        '--- stderr ---'
        $standardError
    )
    Set-Content -LiteralPath $logPath -Encoding utf8 -Value $log

    if ($standardOutput) {
        Write-Host $standardOutput.TrimEnd()
    }
    if ($standardError) {
        Write-Host $standardError.TrimEnd()
    }

    $result = [pscustomobject]@{
        Label = $Label
        Command = $command
        StartedUtc = $startedUtc.ToString('O')
        EndedUtc = $endedUtc.ToString('O')
        DurationSeconds = [Math]::Round(($endedUtc - $startedUtc).TotalSeconds, 3)
        HardTimeoutSeconds = $TimeoutSeconds
        TimedOut = $timedOut
        ExitCode = $exitCode
        Log = [IO.Path]::GetRelativePath($artifactRoot, $logPath).Replace('\', '/')
    }
    $process.Dispose()
    return $result
}

function Get-SolutionProjects {
    [xml]$solution = Get-Content -LiteralPath $solutionPath
    $projectPaths = @(
        $solution.SelectNodes('//Project') |
            ForEach-Object { [string]$_.Path } |
            Sort-Object -Unique
    )
    if ($projectPaths.Count -eq 0) {
        throw 'AcDream.slnx contains no projects.'
    }

    $projects = @(
        foreach ($relativePath in $projectPaths) {
            $fullPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $relativePath))
            if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
                throw "Solution project '$relativePath' does not exist."
            }
            Get-Item -LiteralPath $fullPath
        }
    )

    $maintainedProjects = @(
        foreach ($directoryName in @('src', 'tests', 'tools')) {
            Get-ChildItem -LiteralPath (Join-Path $repoRoot $directoryName) -Recurse -Filter '*.csproj' -File
        }
    ) | Where-Object {
        $relative = [IO.Path]::GetRelativePath($repoRoot, $_.FullName).Replace('\', '/')
        -not $relative.StartsWith('tools/ace-mods/', [StringComparison]::OrdinalIgnoreCase)
    }
    $solutionSet = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($project in $projects) {
        [void]$solutionSet.Add($project.FullName)
    }
    $missing = @($maintainedProjects | Where-Object { -not $solutionSet.Contains($_.FullName) })
    if ($missing.Count -gt 0) {
        $missingText = $missing |
            ForEach-Object { [IO.Path]::GetRelativePath($repoRoot, $_.FullName).Replace('\', '/') } |
            Sort-Object
        throw "Maintained projects missing from AcDream.slnx: $($missingText -join ', ')"
    }

    return $projects
}

function Get-TestProjects {
    $solutionText = Get-Content -LiteralPath $solutionPath -Raw
    $projects = @(
        Get-ChildItem -LiteralPath (Join-Path $repoRoot 'tests') -Recurse -Filter '*.csproj' -File |
            Where-Object {
                $projectText = Get-Content -LiteralPath $_.FullName -Raw
                $projectText -match 'Microsoft\.NET\.Test\.Sdk' -or
                    $projectText -match '<IsTestProject>\s*true\s*</IsTestProject>'
            } |
            Sort-Object FullName
    )

    if ($projects.Count -eq 0) {
        throw 'No test projects were discovered.'
    }

    foreach ($project in $projects) {
        $relative = [IO.Path]::GetRelativePath($repoRoot, $project.FullName).Replace('\', '/')
        if ($solutionText -notmatch [Regex]::Escape("Path=`"$relative`"")) {
            throw "Discovered test project '$relative' is missing from AcDream.slnx."
        }
    }

    return $projects
}

function Get-TrxCounters {
    param([Parameter(Mandatory)] [string]$TrxPath)

    [xml]$document = Get-Content -LiteralPath $TrxPath
    $counters = $document.TestRun.ResultSummary.Counters
    if ($null -eq $counters) {
        throw "TRX '$TrxPath' has no ResultSummary/Counters element."
    }

    return [pscustomobject]@{
        Total = [int]$counters.total
        Executed = [int]$counters.executed
        Passed = [int]$counters.passed
        Failed = [int]$counters.failed
        Skipped = [int]$counters.total - [int]$counters.executed
        Error = [int]$counters.error
        Timeout = [int]$counters.timeout
        Aborted = [int]$counters.aborted
        NotRunnable = [int]$counters.notRunnable
    }
}

function Write-ArtifactHashes {
    $hashPath = Join-Path $artifactRoot 'SHA256SUMS.txt'
    $lines = @(
        Get-ChildItem -LiteralPath $artifactRoot -Recurse -File |
            Where-Object FullName -ne $hashPath |
            Sort-Object FullName |
            ForEach-Object {
                $relative = [IO.Path]::GetRelativePath($artifactRoot, $_.FullName).Replace('\', '/')
                $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                "$hash  $relative"
            }
    )
    Set-Content -LiteralPath $hashPath -Encoding ascii -Value $lines
}

Push-Location $repoRoot
try {
    $gateStartedUtc = [DateTimeOffset]::UtcNow
    $solutionProjects = @(Get-SolutionProjects)
    $testProjects = @(Get-TestProjects)
    $processes = [Collections.Generic.List[object]]::new()
    $testReports = [Collections.Generic.List[object]]::new()
    $gateFailures = [Collections.Generic.List[string]]::new()
    $worktreeStatus = @(git status --porcelain=v1 --untracked-files=all)
    $worktreeDirty = $worktreeStatus.Count -ne 0

    $environmentPath = Join-Path $artifactRoot 'environment.txt'
    $environmentReport = @(
        "commit: $(git rev-parse HEAD)"
        "branch: $(git branch --show-current)"
        "worktreeDirty: $worktreeDirty"
        "runtimeIdentifier: $([Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier)"
        "os: $([Runtime.InteropServices.RuntimeInformation]::OSDescription)"
        "processArchitecture: $([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture)"
        "powershell: $($PSVersionTable.PSVersion)"
        ''
        '--- dotnet --info ---'
        (dotnet --info | Out-String)
        '--- configured NuGet sources ---'
        (dotnet nuget list source | Out-String)
        '--- worktree status at gate start ---'
        ($worktreeStatus -join [Environment]::NewLine)
        '--- supported solution projects ---'
        ($solutionProjects | ForEach-Object {
            [IO.Path]::GetRelativePath($repoRoot, $_.FullName).Replace('\', '/')
        })
        '--- package lock files ---'
        ($solutionProjects | ForEach-Object {
            $relativeProject = [IO.Path]::GetRelativePath($repoRoot, $_.FullName).Replace('\', '/')
            $lockNames = [Collections.Generic.List[string]]::new()
            $lockNames.Add('packages.neutral.lock.json')
            if ($relativeProject.StartsWith('src/', [StringComparison]::OrdinalIgnoreCase)) {
                $lockNames.Add('packages.win-x64.lock.json')
                $lockNames.Add('packages.linux-x64.lock.json')
            }
            foreach ($lockName in $lockNames) {
                $lockPath = Join-Path $_.DirectoryName $lockName
                if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
                    throw "Supported project '$($_.FullName)' has no $lockName."
                }
                $relative = [IO.Path]::GetRelativePath($repoRoot, $lockPath).Replace('\', '/')
                $hash = (Get-FileHash -LiteralPath $lockPath -Algorithm SHA256).Hash.ToLowerInvariant()
                "$hash  $relative"
            }
        })
        '--- discovered test projects ---'
        ($testProjects | ForEach-Object {
            [IO.Path]::GetRelativePath($repoRoot, $_.FullName).Replace('\', '/')
        })
    )
    Set-Content -LiteralPath $environmentPath -Encoding utf8 -Value $environmentReport

    if (-not $SkipRestore) {
        $restore = Invoke-BoundedDotnet -Label 'restore' -TimeoutSeconds $RestoreTimeoutSeconds -Arguments @(
            'restore', $solutionPath, '--locked-mode', '--force-evaluate', '--nologo'
        )
        $processes.Add($restore)
        if ($restore.ExitCode -ne 0) {
            throw "Restore failed or timed out; see '$($restore.Log)'."
        }
    }

    if (-not $SkipBuild) {
        $buildArguments = @('build', $solutionPath, '-c', 'Release', '--nologo')
        if (-not $SkipRestore) {
            $buildArguments += '--no-restore'
        }
        $build = Invoke-BoundedDotnet -Label 'build' -TimeoutSeconds $BuildTimeoutSeconds -Arguments $buildArguments
        $processes.Add($build)
        if ($build.ExitCode -ne 0) {
            throw "Build failed or timed out; see '$($build.Log)'."
        }
    }

    foreach ($project in $testProjects) {
        $projectName = [IO.Path]::GetFileNameWithoutExtension($project.Name)
        $projectResultDirectory = Join-Path $resultsDirectory $projectName
        New-Item -ItemType Directory -Force -Path $projectResultDirectory | Out-Null
        $relativeProject = [IO.Path]::GetRelativePath($repoRoot, $project.FullName).Replace('\', '/')
        $test = Invoke-BoundedDotnet -Label "test-$projectName" -TimeoutSeconds $TestTimeoutSeconds -Arguments @(
            'test'
            $project.FullName
            '-c'
            'Release'
            '--no-restore'
            '--no-build'
            '--nologo'
            '--filter'
            $TestFilter
            '--results-directory'
            $projectResultDirectory
            '--logger'
            "trx;LogFileName=$projectName.trx"
            '--logger'
            'console;verbosity=minimal'
            '--blame-hang'
            '--blame-hang-dump-type'
            'mini'
            '--blame-hang-timeout'
            "${HangTimeoutSeconds}s"
        )
        $processes.Add($test)

        $trxPath = Join-Path $projectResultDirectory "$projectName.trx"
        $counters = if (Test-Path -LiteralPath $trxPath -PathType Leaf) {
            Get-TrxCounters -TrxPath $trxPath
        } else {
            $null
        }
        $testReports.Add([pscustomobject]@{
            Project = $relativeProject
            Assembly = $projectName
            Process = $test
            Trx = if ($null -ne $counters) {
                [IO.Path]::GetRelativePath($artifactRoot, $trxPath).Replace('\', '/')
            } else {
                $null
            }
            Counters = $counters
        })

        if ($test.ExitCode -ne 0) {
            $gateFailures.Add("$projectName exited $($test.ExitCode) (timedOut=$($test.TimedOut)).")
        }
        if ($null -eq $counters) {
            $gateFailures.Add("$projectName produced no TRX result.")
        }
    }

    $totals = [pscustomobject]@{
        Total = ($testReports | ForEach-Object { if ($null -ne $_.Counters) { $_.Counters.Total } } | Measure-Object -Sum).Sum
        Executed = ($testReports | ForEach-Object { if ($null -ne $_.Counters) { $_.Counters.Executed } } | Measure-Object -Sum).Sum
        Passed = ($testReports | ForEach-Object { if ($null -ne $_.Counters) { $_.Counters.Passed } } | Measure-Object -Sum).Sum
        Failed = ($testReports | ForEach-Object { if ($null -ne $_.Counters) { $_.Counters.Failed } } | Measure-Object -Sum).Sum
        Skipped = ($testReports | ForEach-Object { if ($null -ne $_.Counters) { $_.Counters.Skipped } } | Measure-Object -Sum).Sum
    }
    $gateEndedUtc = [DateTimeOffset]::UtcNow
    $summary = [ordered]@{
        SchemaVersion = 1
        Verdict = if ($gateFailures.Count -eq 0) { 'passed' } else { 'failed' }
        Commit = (git rev-parse HEAD)
        Branch = (git branch --show-current)
        WorktreeDirty = $worktreeDirty
        SdkVersion = (dotnet --version)
        RuntimeIdentifier = [Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
        StartedUtc = $gateStartedUtc.ToString('O')
        EndedUtc = $gateEndedUtc.ToString('O')
        DurationSeconds = [Math]::Round(($gateEndedUtc - $gateStartedUtc).TotalSeconds, 3)
        Solution = 'AcDream.slnx'
        TestProjectCount = $testProjects.Count
        TestFilter = $TestFilter
        Timeouts = [ordered]@{
            RestoreSeconds = $RestoreTimeoutSeconds
            BuildSeconds = $BuildTimeoutSeconds
            TestProcessSeconds = $TestTimeoutSeconds
            PerTestHangSeconds = $HangTimeoutSeconds
        }
        Totals = $totals
        Failures = @($gateFailures)
        Tests = @($testReports)
        Processes = @($processes)
        Evidence = [ordered]@{
            Environment = 'environment.txt'
            Hashes = 'SHA256SUMS.txt'
        }
    }
    $summaryPath = Join-Path $artifactRoot 'release-gate-summary.json'
    $summary | ConvertTo-Json -Depth 12 |
        Set-Content -LiteralPath $summaryPath -Encoding utf8
    Write-ArtifactHashes

    Write-Host "Release gate $($summary.Verdict): $($totals.Passed) passed, $($totals.Skipped) skipped, $($totals.Failed) failed across $($testProjects.Count) test assemblies."
    Write-Host "Evidence: $artifactRoot"
    if ($gateFailures.Count -ne 0) {
        foreach ($failure in $gateFailures) {
            Write-Error $failure -ErrorAction Continue
        }
        exit 1
    }
} finally {
    Pop-Location
}
