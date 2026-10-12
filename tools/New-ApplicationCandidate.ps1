#requires -Version 7.0
[CmdletBinding()]
param(
 [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
 [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_-]{0,79}$')][string]$CandidateId
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Import-Module (Join-Path $PSScriptRoot 'Test-Evidence.psm1') -Force
$candidate=Join-Path $project "artifacts/candidates/$CandidateId"
if(Test-Path -LiteralPath $candidate){throw 'Candidate is immutable; use a new id.'}
function Get-SourceIdentity {
 $files=foreach($folder in @('src','tests','tools','installer')) {
  Get-ChildItem -LiteralPath (Join-Path $project $folder) -File -Recurse | Where-Object {
   $_.FullName -notmatch '[\\/](bin|obj|artifacts)[\\/]' -and $_.Extension -in @('.cs','.xaml','.csproj','.json','.ps1','.psm1','.wxs','.wixproj','.props','.targets','.ico','.svg','.nsi','.nsh')
  }
 }
 $files+=Get-ChildItem -LiteralPath $project -File | Where-Object { $_.Extension -in @('.json','.props','.targets','.slnx','.config') }
 @($files|Sort-Object FullName -Unique|ForEach-Object { [ordered]@{
  path=[IO.Path]::GetRelativePath($project,$_.FullName).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName).Hash
 } })|ConvertTo-Json -Depth 4
}
$identity=Get-SourceIdentity
Push-Location $project
try {
 & (Join-Path $project 'installer/build.ps1') -Version $Version -CandidateId $CandidateId -PublishOnly
 if($LASTEXITCODE -ne 0){throw 'Application publish failed.'}
 $testProjects=@(Get-SolutionTestProjects -SolutionPath (Join-Path $project 'V12UltraController.slnx'))
 $testEvidence=Read-TestSuiteEvidence -TestProjects $testProjects -ResultsDirectory (Join-Path $candidate 'tests')
 if((Get-SourceIdentity) -ne $identity){throw 'Source changed during publish; do not package.'}
 [IO.File]::WriteAllText((Join-Path $candidate 'source-inputs.json'),$identity)
 & dotnet run --project tools/SettingsPreview -c Release -p:Version=$Version -- --output (Join-Path $candidate 'ui') |
  Tee-Object -FilePath (Join-Path $candidate 'ui.log') | Out-Host
 if($LASTEXITCODE -ne 0){throw 'Synthetic UI validation failed.'}
 $uiLine=Get-Content (Join-Path $candidate 'ui.log')|Where-Object {$_ -match '^\{"checks":'}|Select-Object -Last 1
 if(-not $uiLine){throw 'Missing structured UI evidence.'}
 $ui=$uiLine|ConvertFrom-Json
 if($ui.failures -ne 0 -or $ui.hardwareAccessed -or $ui.realPipeAccessed -or $ui.nativeWindowShown){throw 'UI boundary failed.'}
 # Exercise WPF's real native hover/click lifecycle in a separate synthetic
 # process. Detached rendering cannot validate PopupControlService ownership.
 $tooltipReport=Join-Path $candidate 'tooltip-lifecycle.json'
 & dotnet run --project tools/SettingsTooltipChecks -c Release -p:Version=$Version -- --output $tooltipReport |
  Tee-Object -FilePath (Join-Path $candidate 'tooltip-lifecycle.log') | Out-Host
 if($LASTEXITCODE -ne 0){throw 'Native help popup lifecycle validation failed.'}
 $tooltip=Get-Content -LiteralPath $tooltipReport -Raw|ConvertFrom-Json
 if($tooltip.failures -ne 0 -or $tooltip.checks -lt 43 -or $tooltip.errors.Count -ne 0 -or
    $tooltip.hardwareAccessed -or $tooltip.realPipeAccessed -or $tooltip.productionMainWindowCreated -or
    $tooltip.desktopInputInjected -or $tooltip.mouseCaptureRemaining -or -not $tooltip.cleanupComplete -or
    -not $tooltip.defaultStartupSuppressed -or -not $tooltip.syntheticOwnerHwnd -or
    $tooltip.nativeToolTipHwndObservations -lt 1 -or $tooltip.independentPopupHwndObservations -lt 1){throw 'Native help popup boundary or lifecycle failed.'}
 if((Get-SourceIdentity) -ne $identity){throw 'Source changed during validation.'}
 $publish=Join-Path $candidate 'publish'
 $payloads=@(Get-ChildItem -LiteralPath $publish -File -Recurse)
 $exes=@($payloads|Where-Object Extension -eq '.exe')
 if($exes.Count -ne 3){throw 'Expected exactly three application EXEs.'}
 foreach($exe in $exes){if($exe.VersionInfo.ProductVersion -notmatch ('^'+[regex]::Escape($Version)+'($|[.+-])')){throw "Unexpected version: $($exe.Name)"}}
 $zipPath=Join-Path $candidate "V12UltraController-$Version-candidate.zip"
 $archive=[IO.Compression.ZipFile]::Open($zipPath,[IO.Compression.ZipArchiveMode]::Create)
 $entries=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
 try {
  foreach($file in $payloads){$entries.Add('publish/'+[IO.Path]::GetRelativePath($publish,$file.FullName).Replace('\','/'),$file.FullName)}
  foreach($relative in @('THIRD-PARTY-NOTICES.md','docs/source-release.md')) {
   $entries.Add($relative,(Join-Path $project $relative))
  }
  foreach($file in Get-ChildItem -LiteralPath (Join-Path $project 'docs/licenses') -File){$entries.Add('licenses/'+$file.Name,$file.FullName)}
  $entries.Add('source-inputs.json',(Join-Path $candidate 'source-inputs.json'))
  foreach($entry in $entries.GetEnumerator()){
   $null=[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$entry.Value,$entry.Key,[IO.Compression.CompressionLevel]::Optimal)
  }
 } finally {$archive.Dispose()}
 $archive=[IO.Compression.ZipFile]::OpenRead($zipPath)
 try {
  if($archive.Entries.Count -ne $entries.Count){throw 'Archive entry count mismatch.'}
  foreach($entry in $archive.Entries){
   $stream=$entry.Open()
   try{$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
   if($hash -ne (Get-FileHash -LiteralPath $entries[$entry.FullName]).Hash){throw "Archive mismatch: $($entry.FullName)"}
  }
 } finally {$archive.Dispose()}
 $zipHash=(Get-FileHash -LiteralPath $zipPath).Hash
 [IO.File]::WriteAllText((Join-Path $candidate 'SHA256SUMS.txt'),"$zipHash  $([IO.Path]::GetFileName($zipPath))`n")
 $report=[ordered]@{version=$Version;candidateId=$CandidateId;zip=$zipPath;sha256=$zipHash;entriesVerified=$entries.Count;
  sourceSha256=(Get-FileHash (Join-Path $candidate 'source-inputs.json')).Hash;ui=$ui;tooltip=$tooltip;testsPassed=$testEvidence.passed;testEvidence=$testEvidence;
  installed=$false;realDeviceTested=$false;installerBuilt=$false;wixInvoked=$false}
 [IO.File]::WriteAllText((Join-Path $candidate 'candidate-report.json'),($report|ConvertTo-Json -Depth 6))
 Write-Host "Verified candidate: $zipPath"
} finally {Pop-Location}
