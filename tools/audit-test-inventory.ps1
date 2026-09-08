[CmdletBinding()]
param(
    [string]$OutputPath = 'artifacts/test-audit/test-inventory.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$resolvedOutput = if ([IO.Path]::IsPathRooted($OutputPath)) {
    [IO.Path]::GetFullPath($OutputPath)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputPath))
}

$dotnetCommand = Get-Command dotnet -ErrorAction Stop
$dotnetRoot = Split-Path -Parent $dotnetCommand.Source
$sdkVersion = (& dotnet --version).Trim()
$roslynDirectory = Join-Path $dotnetRoot "sdk/$sdkVersion/Roslyn/bincore"
$loadedRoslyn = @([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object {
        $_.GetName().Name -in @('Microsoft.CodeAnalysis', 'Microsoft.CodeAnalysis.CSharp')
    })
if ($loadedRoslyn.Count -lt 2) {
    foreach ($assemblyName in 'Microsoft.CodeAnalysis.dll', 'Microsoft.CodeAnalysis.CSharp.dll') {
        $assemblyPath = Join-Path $roslynDirectory $assemblyName
        if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
            throw "Roslyn assembly not found at '$assemblyPath'."
        }
        Add-Type -Path $assemblyPath
    }
}
$roslynVersion = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree].Assembly.GetName().Version.ToString()

function Get-ShortAttributeName {
    param([Parameter(Mandatory)]$Attribute)

    $name = $Attribute.Name.ToString().Split('.')[-1]
    if ($name.EndsWith('Attribute', [StringComparison]::Ordinal)) {
        $name = $name.Substring(0, $name.Length - 'Attribute'.Length)
    }
    return $name
}

function Get-NodeLine {
    param(
        [Parameter(Mandatory)]$Tree,
        [Parameter(Mandatory)]$Node
    )

    return $Tree.GetLineSpan($Node.Span).StartLinePosition.Line + 1
}

function Get-ContainingClassName {
    param([Parameter(Mandatory)]$Method)

    $typeNode = Get-ContainingTypeNode $Method
    if ($null -ne $typeNode) {
        return $typeNode.Identifier.Text
    }
    return '<global>'
}

function Get-ContainingTypeNode {
    param([Parameter(Mandatory)]$Method)

    $cursor = $Method.Parent
    while ($null -ne $cursor) {
        if ($cursor.GetType().Name -in @(
                'ClassDeclarationSyntax',
                'RecordDeclarationSyntax',
                'StructDeclarationSyntax')) {
            return $cursor
        }
        $cursor = $cursor.Parent
    }
    return $null
}

function Get-ReturnGateKind {
    param([Parameter(Mandatory)][string]$Condition)

    if ($Condition -match '(?i)(GetEnvironmentVariable|ACDREAM_)') {
        return 'OptIn'
    }
    if ($Condition -match '(?i)(OperatingSystem\.)') {
        return 'Platform'
    }
    if ($Condition -match '(?i)(datDir|datDirectory|Directory\.Exists|File\.Exists|TryOpen|TryBuildScenario|steps is null|csvPath|\.pak)') {
        return 'ExternalAsset'
    }
    return $null
}

function Get-EmptyReturnSites {
    param(
        [Parameter(Mandatory)]$Tree,
        [Parameter(Mandatory)]$Method
    )

    $sites = [Collections.Generic.List[object]]::new()
    foreach ($returnNode in @($Method.DescendantNodes() | Where-Object {
                $_.GetType().Name -eq 'ReturnStatementSyntax' -and
                $null -eq $_.Expression
            })) {
        $condition = '<unconditional>'
        $cursor = $returnNode.Parent
        while ($null -ne $cursor -and $cursor -ne $Method) {
            if ($cursor.GetType().Name -eq 'IfStatementSyntax') {
                $condition = $cursor.Condition.ToString()
                break
            }
            $cursor = $cursor.Parent
        }
        $sites.Add([ordered]@{
                Line = Get-NodeLine $Tree $returnNode
                Condition = $condition
                GateKind = Get-ReturnGateKind $condition
            })
    }
    return @($sites)
}

function Get-InvocationName {
    param([Parameter(Mandatory)]$Invocation)

    $expression = $Invocation.Expression
    if ($expression.GetType().Name -eq 'MemberAccessExpressionSyntax') {
        return $expression.Name.Identifier.Text
    }
    if ($expression.GetType().Name -eq 'IdentifierNameSyntax') {
        return $expression.Identifier.Text
    }
    if ($expression.GetType().Name -eq 'GenericNameSyntax') {
        return $expression.Identifier.Text
    }
    return $expression.ToString()
}

function Test-DirectFailureSignal {
    param([Parameter(Mandatory)]$Method)

    if (@($Method.DescendantNodes() | Where-Object {
                $_.GetType().Name -in @('ThrowStatementSyntax', 'ThrowExpressionSyntax')
            }).Count -gt 0) {
        return $true
    }

    foreach ($invocation in @($Method.DescendantNodes() | Where-Object {
                $_.GetType().Name -eq 'InvocationExpressionSyntax'
            })) {
        $expression = $invocation.Expression.ToString()
        if ($expression -match '(^|\.)Assert(\.|$)' -or
            $expression -match '(^|\.)(Should|Shouldly)(\.|$)' -or
            $expression -match '(^|\.)Verify($|\.)') {
            return $true
        }
    }
    return $false
}

function Test-DirectOutputSignal {
    param([Parameter(Mandatory)]$Method)

    foreach ($invocation in @($Method.DescendantNodes() | Where-Object {
                $_.GetType().Name -eq 'InvocationExpressionSyntax'
            })) {
        $expression = $invocation.Expression.ToString()
        if ($expression -match '(^|\.)(_?out|output|Console|Debug|Trace)\.(Write|WriteLine)$' -or
            $expression -match '(^|\.)File\.(WriteAllText|WriteAllLines|WriteAllBytes|AppendAllText|AppendAllLines)$' -or
            $expression -match '(^|\.)(WriteDiagnostic|Dump|DumpSteps|Print)(\.|$)') {
            return $true
        }
    }
    return $false
}

function Test-CSharpPathLiteral {
    param([Parameter(Mandatory)]$Method)

    foreach ($literal in @($Method.DescendantNodes() | Where-Object {
                $_.GetType().Name -eq 'LiteralExpressionSyntax'
            })) {
        if ($literal.Token.ValueText -match '(?i)\.cs') {
            return $true
        }
    }
    foreach ($text in @($Method.DescendantNodes() | Where-Object {
                $_.GetType().Name -eq 'InterpolatedStringTextSyntax'
            })) {
        if ($text.TextToken.ValueText -match '(?i)\.cs') {
            return $true
        }
    }
    return $false
}

function Test-TextFileRead {
    param([Parameter(Mandatory)]$Method)

    foreach ($member in @($Method.DescendantNodes() | Where-Object {
                $_.GetType().Name -eq 'MemberAccessExpressionSyntax'
            })) {
        if ($member.Name.Identifier.Text -in @('ReadAllText', 'ReadAllLines')) {
            return $true
        }
    }
    return $false
}

function Get-AssertionAuditSites {
    param(
        [Parameter(Mandatory)]$Tree,
        [Parameter(Mandatory)]$Method
    )

    $sites = [Collections.Generic.List[object]]::new()
    foreach ($invocation in @($Method.DescendantNodes() | Where-Object {
                $_.GetType().Name -eq 'InvocationExpressionSyntax'
            })) {
        $expression = $invocation.Expression.ToString()
        $arguments = @($invocation.ArgumentList.Arguments)
        if ($arguments.Count -eq 0) {
            continue
        }

        $first = $arguments[0].Expression.ToString()
        $constantTruthKind = if ($expression -match '(^|\.)Assert\.True$' -and
            $first -ceq 'true') {
            'Assert.True(true)'
        } elseif ($expression -match '(^|\.)Assert\.False$' -and
            $first -ceq 'false') {
            'Assert.False(false)'
        } else {
            $null
        }
        if ($null -ne $constantTruthKind) {
            $sites.Add([ordered]@{
                    Line = Get-NodeLine $Tree $invocation
                    Category = 'ConstantTruth'
                    Kind = $constantTruthKind
                    Invocation = [regex]::Replace($invocation.ToString(), '\s+', ' ').Trim()
                })
        }

        if ($arguments.Count -lt 2 -or
            $expression -notmatch '(^|\.)Assert\.(Equal|StrictEqual|Same|NotEqual|NotSame)$') {
            continue
        }

        $second = $arguments[1].Expression.ToString()
        if ($first -cne $second) {
            continue
        }
        $sites.Add([ordered]@{
                Line = Get-NodeLine $Tree $invocation
                Category = 'SelfComparison'
                Kind = "Assert.$($expression.Split('.')[-1])"
                Invocation = [regex]::Replace($invocation.ToString(), '\s+', ' ').Trim()
            })
    }
    return @($sites)
}

function Get-WaitSites {
    param(
        [Parameter(Mandatory)]$Tree,
        [Parameter(Mandatory)]$Method
    )

    $sites = [Collections.Generic.List[object]]::new()
    foreach ($invocation in @($Method.DescendantNodes() | Where-Object {
                $_.GetType().Name -eq 'InvocationExpressionSyntax'
            })) {
        $expression = $invocation.Expression.ToString()
        $kind = if ($expression -match '(^|\.)Thread\.Sleep$') {
            'ThreadSleep'
        } elseif ($expression -match '(^|\.)Task\.Delay$') {
            'TaskDelay'
        } else {
            $null
        }
        if ($null -eq $kind) {
            continue
        }

        $text = [regex]::Replace($invocation.ToString(), '\s+', ' ').Trim()
        $sites.Add([ordered]@{
                Line = Get-NodeLine $Tree $invocation
                Kind = $kind
                Invocation = $text
                IsCancellableInfiniteDelay = $kind -eq 'TaskDelay' -and
                    $text -match 'Timeout\.InfiniteTimeSpan' -and
                    $invocation.ArgumentList.Arguments.Count -ge 2
            })
    }
    return @($sites)
}

function Get-BodyFingerprint {
    param([Parameter(Mandatory)]$Method)

    $bodyNode = if ($null -ne $Method.Body) {
        $Method.Body
    } else {
        $Method.ExpressionBody
    }
    if ($null -eq $bodyNode) {
        return $null
    }

    $bodyText = $bodyNode.ToString().Replace("`r`n", "`n")
    $bytes = [Text.Encoding]::UTF8.GetBytes($bodyText)
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($bytes))
}

function Test-RecursiveSignal {
    param(
        [Parameter(Mandatory)][string]$MethodKey,
        [Parameter(Mandatory)][hashtable]$MethodMap,
        [Parameter(Mandatory)][hashtable]$DirectSignalMap,
        [Parameter(Mandatory)][hashtable]$CallMap,
        [Parameter(Mandatory)][hashtable]$Visited
    )

    if ($Visited.ContainsKey($MethodKey)) {
        return $false
    }
    $Visited[$MethodKey] = $true
    if ($DirectSignalMap[$MethodKey]) {
        return $true
    }
    foreach ($calledName in @($CallMap[$MethodKey])) {
        foreach ($candidateKey in @($MethodMap.Keys | Where-Object {
                    $_.EndsWith("::$calledName", [StringComparison]::Ordinal)
                })) {
            if (Test-RecursiveSignal $candidateKey $MethodMap $DirectSignalMap $CallMap $Visited) {
                return $true
            }
        }
    }
    return $false
}

function Get-RecursivePrerequisiteGateSites {
    param(
        [Parameter(Mandatory)][string]$MethodKey,
        [Parameter(Mandatory)][hashtable]$MethodMap,
        [Parameter(Mandatory)][hashtable]$GateMap,
        [Parameter(Mandatory)][hashtable]$CallMap,
        [Parameter(Mandatory)][hashtable]$Visited
    )

    if ($Visited.ContainsKey($MethodKey)) {
        return @()
    }
    $Visited[$MethodKey] = $true

    $sites = [Collections.Generic.List[object]]::new()
    foreach ($calledName in @($CallMap[$MethodKey])) {
        foreach ($candidateKey in @($MethodMap.Keys | Where-Object {
                    $_.EndsWith("::$calledName", [StringComparison]::Ordinal)
                })) {
            foreach ($gate in @($GateMap[$candidateKey] | Where-Object {
                        $null -ne $_.GateKind
                    })) {
                $sites.Add([ordered]@{
                        Helper = $candidateKey
                        Line = $gate.Line
                        Condition = $gate.Condition
                        GateKind = $gate.GateKind
                    })
            }
            foreach ($nestedSite in @(Get-RecursivePrerequisiteGateSites `
                        $candidateKey $MethodMap $GateMap $CallMap $Visited)) {
                $sites.Add($nestedSite)
            }
        }
    }
    return @($sites | Sort-Object Helper, Line, Condition -Unique)
}

$trackedFiles = @(& git -C $repoRoot ls-files tests | Where-Object {
        $_.EndsWith('.cs', [StringComparison]::OrdinalIgnoreCase)
    })
if ($LASTEXITCODE -ne 0) {
    throw 'git ls-files failed while enumerating test sources.'
}

$records = [Collections.Generic.List[object]]::new()
$parsedFileCount = 0
foreach ($relativePath in $trackedFiles) {
    $absolutePath = Join-Path $repoRoot $relativePath
    if (-not (Test-Path -LiteralPath $absolutePath -PathType Leaf)) {
        continue
    }

    $source = [IO.File]::ReadAllText($absolutePath)
    $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source)
    $root = $tree.GetRoot()
    $parsedFileCount++

    $allMethods = @($root.DescendantNodes() | Where-Object {
            $_.GetType().Name -eq 'MethodDeclarationSyntax'
        })
    $methodMap = @{}
    $failureMap = @{}
    $outputMap = @{}
    $textReadMap = @{}
    $csharpPathMap = @{}
    $gateMap = @{}
    $callMap = @{}

    foreach ($method in $allMethods) {
        $className = Get-ContainingClassName $method
        $key = "$className::$($method.Identifier.Text)"
        $methodMap[$key] = $method
        $failureMap[$key] = Test-DirectFailureSignal $method
        $outputMap[$key] = Test-DirectOutputSignal $method
        $textReadMap[$key] = Test-TextFileRead $method
        $csharpPathMap[$key] = Test-CSharpPathLiteral $method
        $gateMap[$key] = @(Get-EmptyReturnSites $tree $method)
        $callMap[$key] = @($method.DescendantNodes() |
            Where-Object { $_.GetType().Name -eq 'InvocationExpressionSyntax' } |
            ForEach-Object { Get-InvocationName $_ } |
            Sort-Object -Unique)
    }

    foreach ($method in $allMethods) {
        $methodAttributes = @($method.AttributeLists | ForEach-Object { $_.Attributes })
        $attributeNames = @($methodAttributes | ForEach-Object {
                Get-ShortAttributeName $_
            })
        $testAttributes = @($attributeNames | Where-Object {
                $_ -in @('Fact', 'Theory', 'AvaloniaFact', 'InstalledDatFact')
            })
        if ($testAttributes.Count -eq 0) {
            continue
        }

        $className = Get-ContainingClassName $method
        $key = "$className::$($method.Identifier.Text)"
        $hasFailureSignal = Test-RecursiveSignal `
            $key $methodMap $failureMap $callMap @{}
        $hasOutputSignal = Test-RecursiveSignal `
            $key $methodMap $outputMap $callMap @{}
        $readsFileText = Test-RecursiveSignal `
            $key $methodMap $textReadMap $callMap @{}
        $usesCsharpPath = Test-RecursiveSignal `
            $key $methodMap $csharpPathMap $callMap @{}
        $readsSourceText = $readsFileText -and $usesCsharpPath

        $emptyReturns = @($gateMap[$key])
        $helperPrerequisiteReturns = @(Get-RecursivePrerequisiteGateSites `
            $key $methodMap $gateMap $callMap @{})

        $traits = [Collections.Generic.List[string]]::new()
        $staticSkip = $null
        $typeNode = Get-ContainingTypeNode $method
        $traitAttributes = @($methodAttributes)
        if ($null -ne $typeNode) {
            $traitAttributes += @($typeNode.AttributeLists | ForEach-Object {
                    $_.Attributes
                })
        }
        foreach ($attribute in $traitAttributes) {
            $attributeName = Get-ShortAttributeName $attribute
            $arguments = if ($null -eq $attribute.ArgumentList) {
                ''
            } else {
                $attribute.ArgumentList.ToString()
            }
            if ($attributeName -eq 'Trait') {
                $traits.Add($arguments.Trim('(', ')').Replace('"', ''))
            }
        }
        foreach ($attribute in $methodAttributes) {
            $arguments = if ($null -eq $attribute.ArgumentList) {
                ''
            } else {
                $attribute.ArgumentList.ToString()
            }
            if ($arguments -match '(^|[,\s])Skip\s*=') {
                $staticSkip = $arguments
            }
        }

        $inlineDataRows = @($methodAttributes | Where-Object {
                (Get-ShortAttributeName $_) -eq 'InlineData'
            } | ForEach-Object {
                if ($null -eq $_.ArgumentList) { '()' } else { $_.ArgumentList.ToString() }
            })
        $duplicateInlineDataRows = @($inlineDataRows |
            Group-Object -CaseSensitive |
            Where-Object { $_.Count -gt 1 } |
            ForEach-Object { $_.Name })

        $bodyText = $method.ToString()
        $waitSites = @(Get-WaitSites $tree $method)
        $assertionAuditSites = @(Get-AssertionAuditSites $tree $method)
        $environmentVariables = @([regex]::Matches(
                $bodyText,
                'GetEnvironmentVariable\s*\(\s*"([A-Za-z0-9_]+)"') |
            ForEach-Object { $_.Groups[1].Value } |
            Sort-Object -Unique)

        $records.Add([ordered]@{
                Path = $relativePath.Replace('\', '/')
                Line = Get-NodeLine $tree $method
                Class = $className
                Method = $method.Identifier.Text
                Attributes = $testAttributes
                Traits = @($traits)
                StaticSkip = $staticSkip
                BodyFingerprint = Get-BodyFingerprint $method
                InlineDataRows = $inlineDataRows
                DuplicateInlineDataRows = $duplicateInlineDataRows
                EmptyReturns = @($emptyReturns)
                HelperPrerequisiteReturns = @($helperPrerequisiteReturns)
                PrerequisiteReturnCandidate = @($emptyReturns | Where-Object {
                        $null -ne $_.GateKind
                    }).Count -gt 0
                HelperPrerequisiteReturnCandidate = $helperPrerequisiteReturns.Count -gt 0
                HasFailureSignal = $hasFailureSignal
                HasOutputSignal = $hasOutputSignal
                OutputOnlyCandidate = $hasOutputSignal -and -not $hasFailureSignal
                ConstantTruthAssertions = @($assertionAuditSites | Where-Object {
                        $_.Category -eq 'ConstantTruth'
                    })
                SelfComparisonAssertions = @($assertionAuditSites | Where-Object {
                        $_.Category -eq 'SelfComparison'
                    })
                EnvironmentVariables = $environmentVariables
                WaitSites = $waitSites
                HasThreadSleep = @($waitSites | Where-Object {
                        $_.Kind -eq 'ThreadSleep'
                    }).Count -gt 0
                HasTaskDelay = @($waitSites | Where-Object {
                        $_.Kind -eq 'TaskDelay'
                    }).Count -gt 0
                HasOnlyCancellableInfiniteDelay = $waitSites.Count -gt 0 -and
                    @($waitSites | Where-Object {
                            -not $_.IsCancellableInfiniteDelay
                        }).Count -eq 0
                ReadsSourceTextDirectly = [bool]($textReadMap[$key] -and
                    $csharpPathMap[$key])
                ReadsSourceText = $readsSourceText
            })
    }
}

$orderedRecords = @($records | Sort-Object Path, Line, Method)
$duplicateBodyGroups = @($orderedRecords |
    Where-Object { $null -ne $_.BodyFingerprint } |
    Group-Object { $_.BodyFingerprint } |
    Where-Object { $_.Count -gt 1 } |
    ForEach-Object {
        [ordered]@{
            Fingerprint = $_.Name
            Count = $_.Count
            Tests = @($_.Group | ForEach-Object {
                    [ordered]@{
                        Path = $_.Path
                        Line = $_.Line
                        Class = $_.Class
                        Method = $_.Method
                    }
                })
        }
    })
$duplicateDataRowsAcrossEquivalentBodies = [Collections.Generic.List[object]]::new()
foreach ($bodyGroup in $duplicateBodyGroups) {
    $occurrences = @($bodyGroup.Tests | ForEach-Object {
            $test = $_
            $record = $orderedRecords | Where-Object {
                $_.Path -eq $test.Path -and
                $_.Line -eq $test.Line -and
                $_.Class -eq $test.Class -and
                $_.Method -eq $test.Method
            } | Select-Object -First 1
            foreach ($row in @($record.InlineDataRows)) {
                [ordered]@{
                    Row = $row
                    Path = $test.Path
                    Line = $test.Line
                    Class = $test.Class
                    Method = $test.Method
                }
            }
        })
    foreach ($rowGroup in @($occurrences |
            Group-Object { $_.Row } -CaseSensitive |
            Where-Object { $_.Count -gt 1 })) {
        $duplicateDataRowsAcrossEquivalentBodies.Add([ordered]@{
                BodyFingerprint = $bodyGroup.Fingerprint
                Row = $rowGroup.Name
                Occurrences = @($rowGroup.Group)
            })
    }
}
$workingTreeStatus = @(& git -C $repoRoot status --short --untracked-files=no)
$summary = [ordered]@{
    GeneratedUtc = [DateTime]::UtcNow.ToString('O')
    RepositoryCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
    WorkingTreeDirty = $workingTreeStatus.Count -gt 0
    DotnetSdkVersion = $sdkVersion
    RoslynAssemblyVersion = $roslynVersion
    TrackedCSharpFiles = $trackedFiles.Count
    ParsedCSharpFiles = $parsedFileCount
    AttributedTestMethods = $orderedRecords.Count
    StaticSkipMethods = @($orderedRecords | Where-Object { $null -ne $_.StaticSkip }).Count
    DuplicateInlineDataMethods = @($orderedRecords | Where-Object {
            $_.DuplicateInlineDataRows.Count -gt 0
        }).Count
    ExactBodyDuplicateGroups = $duplicateBodyGroups.Count
    MethodsInExactBodyDuplicateGroups = @($duplicateBodyGroups |
        ForEach-Object { $_.Tests }).Count
    DuplicateDataRowsAcrossEquivalentBodies =
        $duplicateDataRowsAcrossEquivalentBodies.Count
    EmptyReturnMethods = @($orderedRecords | Where-Object { $_.EmptyReturns.Count -gt 0 }).Count
    EmptyReturnSites = @($orderedRecords | ForEach-Object { $_.EmptyReturns }).Count
    PrerequisiteReturnCandidates = @($orderedRecords | Where-Object {
            $_.PrerequisiteReturnCandidate
        }).Count
    HelperPrerequisiteReturnCandidates = @($orderedRecords | Where-Object {
            $_.HelperPrerequisiteReturnCandidate
        }).Count
    HelperPrerequisiteReturnSites = @($orderedRecords |
        ForEach-Object { $_.HelperPrerequisiteReturns }).Count
    AnyPrerequisiteReturnCandidates = @($orderedRecords | Where-Object {
            $_.PrerequisiteReturnCandidate -or
            $_.HelperPrerequisiteReturnCandidate
        }).Count
    OutputOnlyCandidates = @($orderedRecords | Where-Object { $_.OutputOnlyCandidate }).Count
    ConstantTruthAssertionMethods = @($orderedRecords | Where-Object {
            $_.ConstantTruthAssertions.Count -gt 0
        }).Count
    ConstantTruthAssertionSites = @($orderedRecords |
        ForEach-Object { $_.ConstantTruthAssertions }).Count
    SelfComparisonAssertionMethods = @($orderedRecords | Where-Object {
            $_.SelfComparisonAssertions.Count -gt 0
        }).Count
    SelfComparisonAssertionSites = @($orderedRecords |
        ForEach-Object { $_.SelfComparisonAssertions }).Count
    DiagnosticMethods = @($orderedRecords | Where-Object {
            $_.Traits -contains 'Purpose, Diagnostic'
        }).Count
    KnownFailureMethods = @($orderedRecords | Where-Object {
            $_.Traits -contains 'Status, KnownFailure'
        }).Count
    LaneMethods = [ordered]@{
        InstalledDat = @($orderedRecords | Where-Object {
                $_.Traits -contains 'Lane, InstalledDat'
            }).Count
        PreparedPackage = @($orderedRecords | Where-Object {
                $_.Traits -contains 'Lane, PreparedPackage'
            }).Count
        Live = @($orderedRecords | Where-Object {
                $_.Traits -contains 'Lane, Live'
            }).Count
        Manual = @($orderedRecords | Where-Object {
                $_.Traits -contains 'Lane, Manual'
            }).Count
        Windows = @($orderedRecords | Where-Object {
                $_.Traits -contains 'Lane, Windows'
            }).Count
        Linux = @($orderedRecords | Where-Object {
                $_.Traits -contains 'Lane, Linux'
            }).Count
        SystemFont = @($orderedRecords | Where-Object {
                $_.Traits -contains 'Lane, SystemFont'
            }).Count
    }
    ManualTaskMethods = [ordered]@{
        FixtureGeneration = @($orderedRecords | Where-Object {
                $_.Traits -contains 'ManualTask, FixtureGeneration'
            }).Count
        LiveMountProbe = @($orderedRecords | Where-Object {
                $_.Traits -contains 'ManualTask, LiveMountProbe'
            }).Count
        PowerbarProbe = @($orderedRecords | Where-Object {
                $_.Traits -contains 'ManualTask, PowerbarProbe'
            }).Count
    }
    ThreadSleepMethods = @($orderedRecords | Where-Object { $_.HasThreadSleep }).Count
    TaskDelayMethods = @($orderedRecords | Where-Object { $_.HasTaskDelay }).Count
    CancellableInfiniteDelayOnlyMethods = @($orderedRecords | Where-Object {
            $_.HasOnlyCancellableInfiniteDelay
        }).Count
    FixedWallClockWaitMethods = @($orderedRecords | Where-Object {
            $_.WaitSites.Count -gt 0 -and -not $_.HasOnlyCancellableInfiniteDelay
        }).Count
    DirectEnvironmentVariableMethods = @($orderedRecords | Where-Object {
            $_.EnvironmentVariables.Count -gt 0
        }).Count
    DirectSourceTextReadMethods = @($orderedRecords | Where-Object {
            $_.ReadsSourceTextDirectly
        }).Count
    SourceTextReadMethods = @($orderedRecords | Where-Object { $_.ReadsSourceText }).Count
}

$report = [ordered]@{
    Summary = $summary
    ExactBodyDuplicateGroups = $duplicateBodyGroups
    DuplicateDataRowsAcrossEquivalentBodies =
        @($duplicateDataRowsAcrossEquivalentBodies)
    Tests = $orderedRecords
}
$outputDirectory = Split-Path -Parent $resolvedOutput
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resolvedOutput -Encoding utf8

$summary | Format-List
Write-Output "Inventory: $resolvedOutput"
