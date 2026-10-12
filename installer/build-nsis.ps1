#requires -Version 7.0
[CmdletBinding()]
param([string]$CandidateId='0-1-22-tray-startup-20260908',
      [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$BuildId='nsis-0122')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
# Reuse read-only candidate validation; PrepareOnly never invokes WiX or accepts terms.
$plan=(& (Join-Path $PSScriptRoot 'build-setup.ps1') -CandidateId $CandidateId -PrepareOnly) | ConvertFrom-Json
$compilerRoot=Join-Path $PSScriptRoot 'tools/nsis-3.12'
$icon=Join-Path $PSScriptRoot 'assets/FanOrder.ico'
if (-not (Test-Path -LiteralPath $icon -PathType Leaf)) { throw 'FanOrder icon asset missing' }
$archive=Join-Path $PSScriptRoot 'tools/nsis-3.12-official.zip'
if ((Get-FileHash $archive).Hash -ne '56581F90DB321581C5381193D796FFFCF2D24B2F8FED2160A6C6A3BAA67F2C4F') { throw 'NSIS dependency archive changed' }
# Validate extracted compiler/include/plugin bytes against the retained official archive.
$zip=[IO.Compression.ZipFile]::OpenRead($archive)
try { foreach ($entry in $zip.Entries | Where-Object { -not $_.FullName.EndsWith('/') }) {
    $file=Join-Path (Split-Path $compilerRoot) $entry.FullName
    $stream=$entry.Open()
    try { $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
    if ((Get-FileHash -LiteralPath $file).Hash -ne $hash) { throw "NSIS dependency changed: $file" }
} } finally { $zip.Dispose() }
$output=Join-Path $project "artifacts/installer/$BuildId"
if (Test-Path -LiteralPath $output) { throw 'Build ID already exists; use a new ID to retain evidence.' }
$payload=Join-Path $output 'payload'
$null=New-Item -ItemType Directory -Path $payload
foreach ($component in 'service','settings','cli') { Copy-Item -LiteralPath (Join-Path $plan.candidate "publish/$component") -Destination $payload -Recurse }
$licenses=Join-Path $payload 'licenses'; $null=New-Item -ItemType Directory -Path $licenses
foreach ($license in 'MPL-2.0.txt','Apache-2.0.txt','MIT-DotNet.txt','DotNet-10.0.11-ThirdPartyNotices.txt') {
    Copy-Item -LiteralPath (Join-Path $project "docs/licenses/$license") -Destination $licenses
}
Copy-Item -LiteralPath (Join-Path $compilerRoot 'COPYING') -Destination (Join-Path $licenses 'NSIS-3.12-COPYING.txt')
Copy-Item -LiteralPath (Join-Path $project 'THIRD-PARTY-NOTICES.md') -Destination $payload
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'nsis/NOTICE.txt') -Destination (Join-Path $payload 'INSTALLER-NOTICE.txt')
# UTF-8 BOM is required by Windows PowerShell 5.1 for Chinese text.
$helper=Get-Content (Join-Path $PSScriptRoot 'nsis/Maintenance.ps1') -Raw
[IO.File]::WriteAllText((Join-Path $payload 'Maintenance.ps1'),$helper,[Text.UTF8Encoding]::new($true))
$files=@(Get-ChildItem -LiteralPath $payload -File -Recurse | ForEach-Object {
    @{path=[IO.Path]::GetRelativePath($payload,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
@{version=$plan.version;files=$files;candidateZipSha256=$plan.zipSha256} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $payload 'payload.json') -Encoding utf8BOM
$exe=Join-Path $output "FanOrder-$($plan.version)-Setup.exe"
& (Join-Path $compilerRoot 'makensis.exe') /NOCONFIG /INPUTCHARSET UTF8 /V3 "/DVERSION=$($plan.version)" "/DPAYLOAD=$payload" "/DOUTPUT=$exe" "/DICON=$icon" (Join-Path $PSScriptRoot 'nsis/FanOrder.nsi') | Tee-Object -FilePath (Join-Path $output 'compiler.log')
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exe)) { throw 'NSIS compilation failed; evidence retained.' }
$hash=(Get-FileHash -LiteralPath $exe).Hash
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'),$hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($exe)+[Environment]::NewLine)
@{version=$plan.version;compiler='NSIS 3.12';compilerArchiveSha256=(Get-FileHash $archive).Hash;candidate=$plan;exe=$exe;sha256=$hash;installerBuilt=$true;installerExecuted=$false;digitallySigned=$false;payloadFiles=$files.Count} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $output 'build-report.json') -Encoding utf8
Write-Output $exe
