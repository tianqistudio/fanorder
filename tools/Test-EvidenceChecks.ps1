#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Test-Evidence.psm1') -Force
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$artifactRoot = (Join-Path $projectRoot 'artifacts') + [IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence must remain under project artifacts.' }
if (Test-Path -LiteralPath $output) { throw 'Use a new evidence directory; existing evidence is immutable.' }
$null = New-Item -ItemType Directory -Path $output
$results = [Collections.Generic.List[object]]::new()

function New-SyntheticTrx([string]$Assembly) {
    return @"
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
 <Results><UnitTestResult testId="case-1" executionId="run-1" outcome="Passed" /></Results>
 <TestDefinitions><UnitTest id="case-1" storage="C:\synthetic\$Assembly.dll" /></TestDefinitions>
 <ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" notExecuted="0" notRunnable="0" disconnected="0" /></ResultSummary>
</TestRun>
"@
}
function Assert-Rejected([string]$Name, [scriptblock]$Action, [string]$Reason) {
    $caught = $null
    try { & $Action | Out-Null } catch { $caught = $_.Exception.Message }
    if ($null -eq $caught -or $caught -notmatch $Reason) { throw "Expected rejection for '$Name' ($Reason); actual: $caught" }
    $results.Add([pscustomobject]@{name=$Name;passed=$true;rejection=$caught})
}

$good = New-SyntheticTrx 'First.Tests'
$read = Read-TestProjectEvidence -TrxText $good -ExpectedAssembly 'First.Tests'
if ($read.total -ne 1 -or $read.passed -ne 1) { throw 'Valid test counters were not preserved.' }
$results.Add([pscustomobject]@{name='valid report';passed=$true})
Assert-Rejected 'wrong assembly' { Read-TestProjectEvidence -TrxText $good -ExpectedAssembly 'Other.Tests' } 'Unexpected assembly'
Assert-Rejected 'failed run' { Read-TestProjectEvidence -TrxText ($good.Replace('outcome="Completed"','outcome="Failed"')) -ExpectedAssembly 'First.Tests' } 'did not complete'
Assert-Rejected 'empty suite' { Read-TestProjectEvidence -TrxText ($good.Replace('total="1"','total="0"')) -ExpectedAssembly 'First.Tests' } 'Empty, failed, or skipped'
Assert-Rejected 'skipped test' { Read-TestProjectEvidence -TrxText ($good.Replace('passed="1"','passed="0"').Replace('notExecuted="0"','notExecuted="1"')) -ExpectedAssembly 'First.Tests' } 'Empty, failed, or skipped'
Assert-Rejected 'missing counter' { Read-TestProjectEvidence -TrxText ($good.Replace('error="0"','')) -ExpectedAssembly 'First.Tests' } 'Missing or invalid test counter'
Assert-Rejected 'failed result despite green counters' { Read-TestProjectEvidence -TrxText ($good.Replace('outcome="Passed"','outcome="Failed"')) -ExpectedAssembly 'First.Tests' } 'Failed, unknown, or duplicate'
Assert-Rejected 'unknown test result' { Read-TestProjectEvidence -TrxText ($good.Replace('testId="case-1"','testId="unknown"')) -ExpectedAssembly 'First.Tests' } 'Failed, unknown, or duplicate'
$duplicate = $good.Replace('</Results>','<UnitTestResult testId="case-1" executionId="run-1" outcome="Passed" /></Results>').Replace('total="1"','total="2"').Replace('executed="1"','executed="2"').Replace('passed="1"','passed="2"')
Assert-Rejected 'duplicate execution' { Read-TestProjectEvidence -TrxText $duplicate -ExpectedAssembly 'First.Tests' } 'Failed, unknown, or duplicate'
Assert-Rejected 'counter and result disagreement' { Read-TestProjectEvidence -TrxText ($good.Replace('total="1"','total="2"').Replace('executed="1"','executed="2"').Replace('passed="1"','passed="2"')) -ExpectedAssembly 'First.Tests' } 'results disagree'

$testProjects = @(
    [pscustomobject]@{project='tests/First.Tests.csproj';assembly='First.Tests';trx='First.Tests.trx'},
    [pscustomobject]@{project='tests/Second.Tests.csproj';assembly='Second.Tests';trx='Second.Tests.trx'}
)
foreach ($case in @('complete','missing','unexpected','wrong-assembly')) {
    $directory = Join-Path $output $case
    $null = New-Item -ItemType Directory -Path $directory
    [IO.File]::WriteAllText((Join-Path $directory 'First.Tests.trx'), $good)
    if ($case -ne 'missing') {
        $assembly = 'Second.Tests'
        if ($case -eq 'wrong-assembly') { $assembly = 'First.Tests' }
        [IO.File]::WriteAllText((Join-Path $directory 'Second.Tests.trx'), (New-SyntheticTrx $assembly))
    }
    if ($case -eq 'unexpected') { [IO.File]::WriteAllText((Join-Path $directory 'tests.trx'), $good) }
}
$suite = Read-TestSuiteEvidence -TestProjects $testProjects -ResultsDirectory (Join-Path $output 'complete')
if ($suite.total -ne 2 -or $suite.passed -ne 2 -or $suite.projects.Count -ne 2) { throw 'Suite did not retain both projects.' }
$results.Add([pscustomobject]@{name='two projects aggregate independently';passed=$true})
Assert-Rejected 'missing project report' { Read-TestSuiteEvidence -TestProjects $testProjects -ResultsDirectory (Join-Path $output 'missing') } 'Missing or unexpected TRX'
Assert-Rejected 'unexpected stale report' { Read-TestSuiteEvidence -TestProjects $testProjects -ResultsDirectory (Join-Path $output 'unexpected') } 'Missing or unexpected TRX'
Assert-Rejected 'overwritten report assembly' { Read-TestSuiteEvidence -TestProjects $testProjects -ResultsDirectory (Join-Path $output 'wrong-assembly') } 'Unexpected assembly'
$discovered = @(Get-SolutionTestProjects -SolutionPath (Join-Path $projectRoot 'V12UltraController.slnx'))
if ($discovered.assembly -notcontains 'V12UltraController.Tests' -or $discovered.assembly -notcontains 'TianqiStudio.DesktopShortcuts.Tests') { throw 'Expected solution tests were not discovered.' }
$results.Add([pscustomobject]@{name='current solution includes both test projects';passed=$true})
$report = [ordered]@{checks=$results.Count;failures=0;synthetic=$true;hardwareAccessed=$false;results=$results;projects=$discovered}
$json = $report | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText((Join-Path $output 'report.json'), $json)
$json
