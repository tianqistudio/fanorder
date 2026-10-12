#requires -Version 7.0
[CmdletBinding()]
param(
 [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_-]{0,79}$')][string]$CandidateId,
 [switch]$AcceptWix7Eula,
 [switch]$PrepareOnly
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$candidate=Join-Path $project "artifacts/candidates/$CandidateId"
$report=Get-Content -LiteralPath (Join-Path $candidate 'candidate-report.json') -Raw | ConvertFrom-Json
if($report.version -notmatch '^\d+\.\d+\.\d+$' -or $report.testsPassed -lt 1045 -or $report.ui.failures -ne 0 -or $report.tooltip.failures -ne 0){throw 'Candidate validation is incomplete'}
$zip=Join-Path $candidate "V12UltraController-$($report.version)-candidate.zip"
if((Get-FileHash -LiteralPath $zip).Hash -ne $report.sha256){throw 'Candidate ZIP changed'}
$inputsPath=Join-Path $candidate 'source-inputs.json'
if((Get-FileHash -LiteralPath $inputsPath).Hash -ne $report.sourceSha256){throw 'Candidate source manifest changed'}
$inputs=Get-Content -LiteralPath $inputsPath -Raw | ConvertFrom-Json
foreach($input in $inputs | Where-Object {$_.path.StartsWith('src/') -or $_.path -notmatch '/'}){
 $path=[IO.Path]::GetFullPath((Join-Path $project $input.path))
 if(-not $path.StartsWith($project+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Source outside project'}
 if((Get-FileHash -LiteralPath $path).Hash -ne $input.sha256){throw "Application source changed: $($input.path)"}
}
$publish=Join-Path $candidate 'publish'
$archive=[IO.Compression.ZipFile]::OpenRead($zip)
try {
 $files=@(Get-ChildItem -LiteralPath $publish -File -Recurse)
 if(@($archive.Entries | Where-Object {$_.FullName.StartsWith('publish/') -and -not $_.FullName.EndsWith('/')}).Count -ne $files.Count){throw 'Published payload file count changed'}
 foreach($file in $files){
  $name='publish/'+[IO.Path]::GetRelativePath($publish,$file.FullName).Replace('\','/')
  $entry=$archive.GetEntry($name)
  if($null -eq $entry){throw "Payload missing from ZIP: $name"}
  $stream=$entry.Open()
  try{$expected=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
  if((Get-FileHash -LiteralPath $file.FullName).Hash -ne $expected){throw "Payload changed: $name"}
 }
}finally{$archive.Dispose()}
$plan=[ordered]@{version=$report.version;candidate=$candidate;zipSha256=$report.sha256;testsPassed=$report.testsPassed;uiChecks=$report.ui.checks;tooltipChecks=$report.tooltip.checks;installerBuilt=$false;installerExecuted=$false}
if($PrepareOnly){$plan | ConvertTo-Json;return}
if(-not $AcceptWix7Eula){throw 'WiX 7 requires a user/organization EULA and applicable OSMF decision. No WiX command was invoked. See https://docs.firegiant.com/wix/osmf/ .'}
$output=Join-Path $project "artifacts/installer/$($report.version)"
if(Test-Path -LiteralPath $output){throw "Refusing to overwrite installer artifacts for released version $($report.version)"}
$null=New-Item -ItemType Directory -Path $output
$package=Join-Path $PSScriptRoot 'Package/V12UltraController.Package.wixproj'
$bundle=Join-Path $PSScriptRoot 'Bundle/V12UltraController.Bundle.wixproj'
$work=Join-Path $output 'build'
& dotnet build $package -c Release "-p:ProductVersion=$($report.version)" "-p:PublishRoot=$publish" "-p:OutputPath=$work\" -p:AcceptEula=wix7
if($LASTEXITCODE){throw 'MSI build failed; retain build evidence'}
$msi=Join-Path $work 'V12UltraController.msi'
if(-not (Test-Path -LiteralPath $msi)){throw 'MSI output missing'}
& dotnet build $bundle -c Release "-p:ProductVersion=$($report.version)" "-p:PublishRoot=$publish" "-p:MsiSourcePath=$msi" "-p:OutputPath=$work\" -p:BuildProjectReferences=false -p:AcceptEula=wix7
if($LASTEXITCODE){throw 'EXE bundle build failed; retain build evidence'}
$built=Join-Path $work 'V12UltraController.Setup.exe'
if(-not (Test-Path -LiteralPath $built)){throw 'EXE output missing'}
$final=Join-Path $output "FanOrder-$($report.version)-Setup.exe"
[IO.File]::Copy($built,$final,$false)
$hash=(Get-FileHash -LiteralPath $final).Hash
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'),$hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($final)+[Environment]::NewLine)
$plan.installerBuilt=$true;$plan.exe=$final;$plan.sha256=$hash
$plan | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'build-report.json') -Encoding utf8
$plan | ConvertTo-Json
