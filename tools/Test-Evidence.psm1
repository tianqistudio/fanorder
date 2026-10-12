# Pure test-evidence readers. Importing this module never runs a test or device command.
Set-StrictMode -Version Latest

function Get-SolutionTestProjects {
    param([Parameter(Mandatory)][string]$SolutionPath)
    $root = Split-Path -Parent ([IO.Path]::GetFullPath($SolutionPath))
    [xml]$solution = Get-Content -LiteralPath $SolutionPath -Raw
    $names = @{}
    $projects = @(foreach ($node in $solution.SelectNodes('//Project[@Path]')) {
        $relative = $node.GetAttribute('Path')
        $path = [IO.Path]::GetFullPath((Join-Path $root $relative))
        if (-not $path.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Test project is outside the solution directory: $relative"
        }
        [xml]$project = Get-Content -LiteralPath $path -Raw
        $testFlag = $project.SelectSingleNode('//IsTestProject')
        $testSdk = $project.SelectSingleNode('//PackageReference[@Include="Microsoft.NET.Test.Sdk"]')
        if (($null -ne $testFlag -and $testFlag.InnerText -eq 'true') -or $null -ne $testSdk) {
            $assemblyNodes = @($project.SelectNodes('//AssemblyName'))
            $assembly = [IO.Path]::GetFileNameWithoutExtension($path)
            if ($assemblyNodes.Count -gt 1) { throw "Ambiguous test assembly name: $relative" }
            if ($assemblyNodes.Count -eq 1) { $assembly = $assemblyNodes[0].InnerText }
            if ($assembly -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]*$' -or $names.ContainsKey($assembly)) {
                throw "Invalid or duplicate test assembly name: $assembly"
            }
            $names[$assembly] = $true
            [pscustomobject]@{ project=$relative; path=$path; assembly=$assembly; trx=$assembly + '.trx' }
        }
    })
    if ($projects.Count -eq 0) { throw 'The solution has no declared test projects.' }
    return $projects
}

function Read-TestProjectEvidence {
    param(
        [Parameter(Mandatory)][string]$TrxText,
        [Parameter(Mandatory)][string]$ExpectedAssembly
    )
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $reader = [Xml.XmlReader]::Create([IO.StringReader]::new($TrxText), $settings)
    try {
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
    } finally { $reader.Dispose() }
    $summary = $document.SelectSingleNode('/*[local-name()="TestRun"]/*[local-name()="ResultSummary"]')
    if ($null -eq $summary -or $summary.GetAttribute('outcome') -ne 'Completed') {
        throw "Test run did not complete successfully: $ExpectedAssembly"
    }
    $counters = $summary.SelectSingleNode('*[local-name()="Counters"]')
    if ($null -eq $counters) { throw "Missing test counters: $ExpectedAssembly" }
    $counts = @{}
    foreach ($name in @('total','executed','passed','failed','error','timeout','aborted','inconclusive','notExecuted','notRunnable','disconnected')) {
        $value = 0
        if (-not [int]::TryParse($counters.GetAttribute($name), [ref]$value) -or $value -lt 0) {
            throw "Missing or invalid test counter '$name': $ExpectedAssembly"
        }
        $counts[$name] = $value
    }
    if ($counts.total -eq 0 -or $counts.executed -ne $counts.total -or $counts.passed -ne $counts.total) {
        throw "Empty, failed, or skipped test suite: $ExpectedAssembly"
    }
    foreach ($name in @('failed','error','timeout','aborted','inconclusive','notExecuted','notRunnable','disconnected')) {
        if ($counts[$name] -ne 0) { throw "Non-passing test counter '$name': $ExpectedAssembly" }
    }
    $definitions = @($document.SelectNodes('/*[local-name()="TestRun"]/*[local-name()="TestDefinitions"]/*[local-name()="UnitTest"]'))
    $knownIds = @{}
    foreach ($definition in $definitions) {
        $assembly = [IO.Path]::GetFileNameWithoutExtension($definition.GetAttribute('storage'))
        $id = $definition.GetAttribute('id')
        if ($assembly -ne $ExpectedAssembly -or -not $id -or $knownIds.ContainsKey($id)) {
            throw "Unexpected assembly or duplicate test definition: $ExpectedAssembly"
        }
        $knownIds[$id] = $true
    }
    $results = @($document.SelectNodes('/*[local-name()="TestRun"]/*[local-name()="Results"]/*[local-name()="UnitTestResult"]'))
    if ($definitions.Count -eq 0 -or $results.Count -ne $counts.total) {
        throw "Test results disagree with counters: $ExpectedAssembly"
    }
    $executions = @{}
    foreach ($result in $results) {
        $id = $result.GetAttribute('executionId')
        if ($result.GetAttribute('outcome') -ne 'Passed' -or -not $knownIds.ContainsKey($result.GetAttribute('testId')) -or
            -not $id -or $executions.ContainsKey($id)) {
            throw "Failed, unknown, or duplicate test result: $ExpectedAssembly"
        }
        $executions[$id] = $true
    }
    return [pscustomobject]@{ assembly=$ExpectedAssembly; total=$counts.total; passed=$counts.passed }
}

function Read-TestSuiteEvidence {
    param(
        [Parameter(Mandatory)][object[]]$TestProjects,
        [Parameter(Mandatory)][string]$ResultsDirectory
    )
    if ($TestProjects.Count -eq 0) { throw 'No expected test projects were supplied.' }
    $expected = @{}
    foreach ($project in $TestProjects) {
        if ($expected.ContainsKey($project.trx)) { throw "Duplicate expected report: $($project.trx)" }
        $expected[$project.trx] = $true
    }
    $files = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter '*.trx' -File -Recurse)
    if ($files.Count -ne $TestProjects.Count) { throw 'Missing or unexpected TRX reports.' }
    $reports = @(foreach ($project in $TestProjects) {
        $path = Join-Path $ResultsDirectory $project.trx
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing report: $($project.trx)" }
        $result = Read-TestProjectEvidence -TrxText (Get-Content -LiteralPath $path -Raw) -ExpectedAssembly $project.assembly
        [pscustomobject]@{ project=$project.project; assembly=$result.assembly; trx=$project.trx;
            sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash; total=$result.total; passed=$result.passed }
    })
    return [pscustomobject]@{ schemaVersion=1; total=[int]($reports | Measure-Object total -Sum).Sum;
        passed=[int]($reports | Measure-Object passed -Sum).Sum; projects=$reports }
}

Export-ModuleMember -Function Get-SolutionTestProjects, Read-TestProjectEvidence, Read-TestSuiteEvidence
