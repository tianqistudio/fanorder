[CmdletBinding()]
param(
    [Parameter()]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version = '0.1.0',

    [Parameter()]
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    [Parameter(HelpMessage = 'Pass only after the WiX v7 OSMF payment status and EULA have been reviewed and accepted by the user or organization.')]
    [switch] $AcceptWix7Eula,

    [Parameter(HelpMessage = 'Build, test, and publish the three applications, then stop before all WiX work.')]
    [switch] $PublishOnly,

    [Parameter(HelpMessage = 'Write application-only output into a new artifacts/candidates/<id> directory; never overwrite an earlier candidate.')]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_-]{0,79}$')]
    [string] $CandidateId
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$installerDirectory = Split-Path -Parent $PSCommandPath
$projectDirectory = [System.IO.Path]::GetFullPath((Join-Path $installerDirectory '..'))
$artifactsDirectory = [System.IO.Path]::GetFullPath((Join-Path $projectDirectory 'artifacts'))
if ($CandidateId) {
    if (-not $PublishOnly) { throw 'CandidateId requires PublishOnly; candidate output is not a released installer.' }
    $artifactsDirectory = Join-Path (Join-Path $artifactsDirectory 'candidates') $CandidateId
    if (Test-Path -LiteralPath $artifactsDirectory) {
        throw "Refusing to overwrite existing candidate: $artifactsDirectory"
    }
}
$publishDirectory = [System.IO.Path]::GetFullPath((Join-Path $artifactsDirectory 'publish'))
$singleFileSmokeDirectory = [System.IO.Path]::GetFullPath((Join-Path $artifactsDirectory 'singlefile-pipe-smoke'))
$installerArtifactsDirectory = [System.IO.Path]::GetFullPath((Join-Path $artifactsDirectory 'installer'))
$outputDirectory = [System.IO.Path]::GetFullPath((Join-Path $installerArtifactsDirectory $Version))
$finalBundle = Join-Path $outputDirectory 'V12UltraController.Setup.exe'
$manifestPath = Join-Path $outputDirectory 'SHA256SUMS.txt'
$solutionPath = Join-Path $projectDirectory 'V12UltraController.slnx'
Import-Module (Join-Path $projectDirectory 'tools/Test-Evidence.psm1') -Force
$testProjects = @(Get-SolutionTestProjects -SolutionPath $solutionPath)
$testResultsDirectory = Join-Path $artifactsDirectory 'tests'
if (-not $CandidateId) {
    $testResultsDirectory = Join-Path $testResultsDirectory ('build-' + [guid]::NewGuid().ToString('N'))
}

function Assert-ProjectChildPath {
    param([Parameter(Mandatory)][string] $LiteralPath)

    $resolved = [System.IO.Path]::GetFullPath($LiteralPath)
    $prefix = $projectDirectory.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the project: $resolved"
    }

    return $resolved
}

function Reset-ProjectDirectory {
    param([Parameter(Mandatory)][string] $LiteralPath)

    $verifiedPath = Assert-ProjectChildPath -LiteralPath $LiteralPath
    if (Test-Path -LiteralPath $verifiedPath) {
        Remove-Item -LiteralPath $verifiedPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $verifiedPath -Force | Out-Null
}

function Test-FileContainsAsciiText {
    param(
        [Parameter(Mandatory)][string] $LiteralPath,
        [Parameter(Mandatory)][string] $Text
    )

    $needleLength = [System.Text.Encoding]::ASCII.GetByteCount($Text)
    $buffer = [byte[]]::new((1024 * 1024) + $needleLength - 1)
    $carry = 0
    $stream = [System.IO.File]::OpenRead($LiteralPath)
    try {
        while (($read = $stream.Read($buffer, $carry, $buffer.Length - $carry)) -gt 0) {
            $count = $carry + $read
            $chunk = [System.Text.Encoding]::ASCII.GetString($buffer, 0, $count)
            # Windows PowerShell 5.1 runs on .NET Framework, whose String.Contains
            # overload does not accept StringComparison. IndexOf is available on
            # every supported PowerShell runtime and preserves ordinal matching.
            if ($chunk.IndexOf($Text, [System.StringComparison]::Ordinal) -ge 0) {
                return $true
            }

            $carry = [System.Math]::Min($needleLength - 1, $count)
            if ($carry -gt 0) {
                [System.Array]::Copy($buffer, $count - $carry, $buffer, 0, $carry)
            }
        }
    }
    finally {
        $stream.Dispose()
    }

    return $false
}

function Assert-PublishedPayloadExcludesMonoPosix {
    param([Parameter(Mandatory)][string] $LiteralPath)

    $forbiddenNames = @(
        'Mono.Posix.NETStandard.dll',
        'MonoPosixHelper.dll',
        'libMonoPosixHelper.dll'
    )
    $files = Get-ChildItem -LiteralPath $LiteralPath -Recurse -File
    $forbiddenFiles = @($files | Where-Object { $forbiddenNames -contains $_.Name })
    if ($forbiddenFiles.Count -ne 0) {
        $paths = $forbiddenFiles.FullName -join [System.Environment]::NewLine
        throw "Mono.Posix runtime assets must not be redistributed by this Windows-only product:`n$paths"
    }

    # Single-file publishing can embed managed and native assets in an EXE. Scan
    # every distributable binary as well as checking sidecar filenames.
    foreach ($file in $files | Where-Object { $_.Extension -in @('.exe', '.dll') }) {
        foreach ($marker in @('Mono.Posix.NETStandard', 'MonoPosixHelper', 'libMonoPosixHelper')) {
            if (Test-FileContainsAsciiText -LiteralPath $file.FullName -Text $marker) {
                throw "Mono.Posix marker '$marker' is embedded in published binary: $($file.FullName)"
            }
        }
    }
}

# Publishing must never erase an older installer. The installer output is only
# refreshed after the caller has explicitly passed the WiX 7 legal gate below.
Reset-ProjectDirectory -LiteralPath $publishDirectory

$projects = @(
    @{ Name = 'service'; Path = 'src\V12UltraController.Service\V12UltraController.Service.csproj' },
    @{ Name = 'settings'; Path = 'src\V12UltraController.Settings\V12UltraController.Settings.csproj' },
    @{ Name = 'cli'; Path = 'src\V12UltraController.Cli\V12UltraController.Cli.csproj' }
)

Write-Host 'Restoring locked application dependencies...'
dotnet restore $solutionPath --locked-mode
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }

Write-Host 'Building and testing without touching the installed service or device...'
dotnet build $solutionPath -c $Configuration --no-restore -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }
foreach ($testProject in $testProjects) {
    dotnet test $testProject.path -c $Configuration --no-build --logger "trx;LogFileName=$($testProject.trx)" --results-directory $testResultsDirectory
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed for $($testProject.assembly) with exit code $LASTEXITCODE" }
}
$testEvidence = Read-TestSuiteEvidence -TestProjects $testProjects -ResultsDirectory $testResultsDirectory
$testEvidence | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $testResultsDirectory 'summary.json') -Encoding UTF8

Write-Host 'Publishing and running the cold-start single-file named-pipe identity smoke test...'
Reset-ProjectDirectory -LiteralPath $singleFileSmokeDirectory
$singleFileSmokeProject = Join-Path $projectDirectory `
    'tests\V12UltraController.SingleFilePipeProbe\V12UltraController.SingleFilePipeProbe.csproj'
dotnet publish $singleFileSmokeProject -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:PublishTrimmed=false -p:DebugType=None `
    -p:Version=$Version -o $singleFileSmokeDirectory --no-restore
if ($LASTEXITCODE -ne 0) { throw "single-file pipe smoke publish failed with exit code $LASTEXITCODE" }
$singleFileSmokeExe = Join-Path $singleFileSmokeDirectory 'V12UltraController.SingleFilePipeProbe.exe'
& $singleFileSmokeExe
if ($LASTEXITCODE -ne 0) { throw "single-file pipe smoke test failed with exit code $LASTEXITCODE" }

foreach ($project in $projects) {
    $projectPath = Join-Path $projectDirectory $project.Path
    $destination = Join-Path $publishDirectory $project.Name
    Write-Host "Publishing $($project.Name) to $destination..."
    dotnet publish $projectPath -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:PublishTrimmed=false -p:DebugType=None `
        -p:Version=$Version -o $destination --no-restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish for $($project.Name) failed with exit code $LASTEXITCODE" }
}

Assert-PublishedPayloadExcludesMonoPosix -LiteralPath $publishDirectory

$publishManifestPath = Join-Path $publishDirectory 'SHA256SUMS.txt'
$publishPathPrefix = $publishDirectory.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar
$publishedFiles = Get-ChildItem -LiteralPath $publishDirectory -Recurse -File |
    Where-Object { $_.FullName -ne $publishManifestPath } |
    Sort-Object FullName
$publishHashLines = foreach ($artifact in $publishedFiles) {
    $hash = Get-FileHash -LiteralPath $artifact.FullName -Algorithm SHA256
    if (-not $artifact.FullName.StartsWith($publishPathPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Published artifact escaped the verified publish root: $($artifact.FullName)"
    }
    # Path.GetRelativePath is unavailable in Windows PowerShell 5.1's .NET
    # Framework runtime. Every enumerated file is already proven below the
    # publish root, so a checked prefix removal is both portable and exact.
    $relativePath = $artifact.FullName.Substring($publishPathPrefix.Length).Replace('\', '/')
    '{0}  {1}' -f $hash.Hash.ToLowerInvariant(), $relativePath
}
[System.IO.File]::WriteAllLines(
    $publishManifestPath,
    $publishHashLines,
    [System.Text.UTF8Encoding]::new($false))

if ($PublishOnly) {
    Write-Host "Application publish complete. Self-contained executables are in $publishDirectory"
    Write-Host 'WiX was not invoked; no installer was generated or executed.'
    return
}

if (-not $AcceptWix7Eula) {
    throw @'
The three applications were built, tested, and published successfully. WiX was
not invoked because WiX Toolset v7 requires explicit acceptance of the wix7
EULA after the user or organization has checked its Open Source Maintenance Fee
status. Review https://docs.firegiant.com/wix/osmf/ and rerun this script with
-AcceptWix7Eula only after that decision has been made. The project does not
accept legal terms on the user's behalf. Use -PublishOnly to make this a
successful application-only build.
'@
}

# A released three-part version is immutable. Preserve every prior version in
# its own artifacts\installer\<version> directory and refuse to replace either
# half of an existing Setup/SHA pair. PublishOnly never reaches this branch.
if ((Test-Path -LiteralPath $finalBundle -PathType Leaf) -or
    (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Refusing to overwrite installer artifacts for released version $Version. Increment the three-part version."
}

Reset-ProjectDirectory -LiteralPath $outputDirectory

$packageProject = Join-Path $installerDirectory 'Package\V12UltraController.Package.wixproj'
$bundleProject = Join-Path $installerDirectory 'Bundle\V12UltraController.Bundle.wixproj'

Write-Host 'Building the WiX MSI...'
dotnet build $packageProject -c $Configuration `
    -p:ProductVersion=$Version -p:PublishRoot=$publishDirectory -p:AcceptEula=wix7
if ($LASTEXITCODE -ne 0) { throw "MSI build failed with exit code $LASTEXITCODE" }

$msiSource = Join-Path $installerDirectory "Package\bin\$Configuration\V12UltraController.msi"
if (-not (Test-Path -LiteralPath $msiSource -PathType Leaf)) {
    throw "Expected MSI was not produced: $msiSource"
}

Write-Host 'Building the WiX Burn bundle...'
dotnet build $bundleProject -c $Configuration `
    -p:ProductVersion=$Version -p:MsiSourcePath=$msiSource -p:AcceptEula=wix7
if ($LASTEXITCODE -ne 0) { throw "Bundle build failed with exit code $LASTEXITCODE" }

$bundleSource = Join-Path $installerDirectory "Bundle\bin\$Configuration\V12UltraController.Setup.exe"
if (-not (Test-Path -LiteralPath $bundleSource -PathType Leaf)) {
    throw "Expected bundle was not produced: $bundleSource"
}

Copy-Item -LiteralPath $bundleSource -Destination $finalBundle

$hashLines = foreach ($artifact in @($finalBundle)) {
    $hash = Get-FileHash -LiteralPath $artifact -Algorithm SHA256
    '{0}  {1}' -f $hash.Hash.ToLowerInvariant(), [System.IO.Path]::GetFileName($artifact)
}
[System.IO.File]::WriteAllLines($manifestPath, $hashLines, [System.Text.UTF8Encoding]::new($false))

Write-Host "Build complete. Artifacts are in $outputDirectory"
Write-Host 'No installer was executed and no service or scheduled task was modified.'
