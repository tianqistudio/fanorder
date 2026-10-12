[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PublishRoot,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string] $ExpectedManifestSha256,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string] $ExpectedInstalledServiceSha256,

    [Parameter(Mandatory)]
    [string] $ResultPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$serviceName = 'TianqiStudio.V12UltraController'
$expectedServicePath =
    'C:\Program Files\TianqiStudio\V12UltraController\Service\V12UltraController.Service.exe'
$dataRoot = 'C:\ProgramData\TianqiStudio\V12UltraController'
$manifestPath = Join-Path $PublishRoot 'SHA256SUMS.txt'
$publishedServicePath = Join-Path $PublishRoot 'service\V12UltraController.Service.exe'
$installedServiceRoot = Split-Path -Parent $expectedServicePath
$publishedServiceRoot = Split-Path -Parent $publishedServicePath
$backupRoot = Join-Path $dataRoot 'live-test-backups'
$backupPath = Join-Path $backupRoot (
    "V12UltraController.Service.$($ExpectedInstalledServiceSha256.ToLowerInvariant()).exe")
$stagedPath = Join-Path $installedServiceRoot (
    ".V12UltraController.Service.$PID.$([Guid]::NewGuid().ToString('N')).staged")
$replaceBackupPath = Join-Path $installedServiceRoot (
    ".V12UltraController.Service.$PID.$([Guid]::NewGuid().ToString('N')).replaced")
$expectedServiceFiles = @(
    'V12UltraController.Service.exe'
)

function Write-Result {
    param(
        [Parameter(Mandatory)][bool] $Ok,
        [Parameter(Mandatory)][string] $Message,
        [Parameter()][object] $Details
    )

    $resultDirectory = Split-Path -Parent $ResultPath
    if (-not [string]::IsNullOrWhiteSpace($resultDirectory)) {
        New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
    }

    $payload = [pscustomobject]@{
        timestampUtc = [DateTime]::UtcNow.ToString('o')
        ok = $Ok
        message = $Message
        details = $Details
    } | ConvertTo-Json -Depth 6
    $temporaryPath = "$ResultPath.$PID.tmp"
    [System.IO.File]::WriteAllText(
        $temporaryPath,
        $payload,
        [System.Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryPath -Destination $ResultPath -Force
}

function Get-CommissionedValue {
    param([Parameter(Mandatory)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }

    $document = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $property = $document.PSObject.Properties |
        Where-Object { $_.Name.Equals('commissioned', [StringComparison]::OrdinalIgnoreCase) } |
        Select-Object -First 1
    if ($null -eq $property) {
        throw "Persistent state has no commissioned field: $Path"
    }

    return [bool]$property.Value
}

function Select-ServiceDetails {
    param([Parameter(Mandatory)][object] $Service)

    return [pscustomobject]@{
        name = $Service.Name
        state = $Service.State
        startMode = $Service.StartMode
        startName = $Service.StartName
        pathName = $Service.PathName
        processId = $Service.ProcessId
    }
}

$oldBinaryRestored = $false
$oldInstallVerified = $false
$replacementAttempted = $false
try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'The stopped live-test replacement must run from an elevated administrator process.'
    }

    $machineDataRedirect = [Environment]::GetEnvironmentVariable('V12ULTRA_DATA_DIR', 'Machine')
    if (-not [string]::IsNullOrWhiteSpace($machineDataRedirect)) {
        throw "Machine V12ULTRA_DATA_DIR is set to '$machineDataRedirect'; refusing to assume the default data root."
    }

    $serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
    $serviceRegistry = Get-ItemProperty -LiteralPath $serviceRegistryPath -ErrorAction Stop
    if ($null -ne $serviceRegistry.PSObject.Properties['Environment']) {
        throw 'The service has a service-specific Environment value; refusing to assume the default data root.'
    }

    $serviceState = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    if ($null -eq $serviceState) {
        throw "Service does not exist: $serviceName"
    }

    $configuredPath = $serviceState.PathName.Trim().Trim('"')
    if (-not $configuredPath.Equals($expectedServicePath, [StringComparison]::OrdinalIgnoreCase) -or
        $serviceState.StartName -ne 'LocalSystem' -or
        $serviceState.StartMode -ne 'Manual' -or
        $serviceState.State -ne 'Stopped' -or
        $serviceState.ProcessId -ne 0) {
        throw 'The service is not the exact stopped Manual/LocalSystem attended-test instance.'
    }

    if (Get-CommissionedValue (Join-Path $dataRoot 'controller.json')) {
        throw 'controller.json reports commissioned=true; refusing binary replacement.'
    }
    if (Get-CommissionedValue (Join-Path $dataRoot 'runtime-state.json')) {
        throw 'runtime-state.json reports commissioned=true; refusing binary replacement.'
    }
    if (Test-Path -LiteralPath (Join-Path $dataRoot 'takeover-transition.json') -PathType Leaf) {
        throw 'A takeover transition journal exists; refusing binary replacement.'
    }

    $task = Get-ScheduledTask -TaskPath '\' -TaskName 'Myth.Cool'
    if (-not $task.Settings.Enabled) {
        throw 'The Myth.Cool task is disabled; refusing an uncommissioned live-test replacement.'
    }

    $installedHash = (Get-FileHash -LiteralPath $expectedServicePath -Algorithm SHA256).Hash
    if (-not $installedHash.Equals($ExpectedInstalledServiceSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Installed service hash changed. Expected $ExpectedInstalledServiceSha256, found $installedHash."
    }

    $oldInstallVerified = $true

    $manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
    if (-not $manifestHash.Equals($ExpectedManifestSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Publish manifest hash changed. Expected $ExpectedManifestSha256, found $manifestHash."
    }

    $serviceManifest = @{}
    foreach ($line in Get-Content -LiteralPath $manifestPath) {
        if ($line -match '^([0-9A-Fa-f]{64})  service/(.+)$') {
            if ($serviceManifest.ContainsKey($Matches[2])) {
                throw "The publish manifest contains a duplicate service entry: $($Matches[2])."
            }

            $serviceManifest[$Matches[2]] = $Matches[1]
        }
    }

    if ($serviceManifest.Count -ne $expectedServiceFiles.Count -or
        @($expectedServiceFiles | Where-Object { -not $serviceManifest.ContainsKey($_) }).Count -ne 0) {
        throw 'The publish manifest does not contain the exact expected service file set.'
    }

    $installedFiles = @(Get-ChildItem -LiteralPath $installedServiceRoot -File | Select-Object -ExpandProperty Name)
    if ($installedFiles.Count -ne $expectedServiceFiles.Count -or
        @($expectedServiceFiles | Where-Object { $_ -notin $installedFiles }).Count -ne 0) {
        throw 'The installed service directory does not contain the exact expected file set.'
    }

    foreach ($fileName in $expectedServiceFiles) {
        $publishedFile = Join-Path $publishedServiceRoot $fileName
        $publishedFileHash = (Get-FileHash -LiteralPath $publishedFile -Algorithm SHA256).Hash
        if (-not $publishedFileHash.Equals($serviceManifest[$fileName], [StringComparison]::OrdinalIgnoreCase)) {
            throw "Published service file does not match its manifest entry: $fileName."
        }

        if ($fileName -ne 'V12UltraController.Service.exe') {
            $installedSupportHash = (Get-FileHash -LiteralPath (
                Join-Path $installedServiceRoot $fileName) -Algorithm SHA256).Hash
            if (-not $installedSupportHash.Equals($publishedFileHash, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Installed support file differs from the new publish: $fileName. Refusing a mixed-version replacement."
            }
        }
    }

    $publishedExpectedHash = $serviceManifest['V12UltraController.Service.exe']
    $publishedHash = (Get-FileHash -LiteralPath $publishedServicePath -Algorithm SHA256).Hash
    if (-not $publishedHash.Equals($publishedExpectedHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Published service executable does not match its manifest entry.'
    }

    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
        $backupHash = (Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash
        if (-not $backupHash.Equals($ExpectedInstalledServiceSha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'An existing pre-replacement backup has an unexpected hash.'
        }
    } else {
        Copy-Item -LiteralPath $expectedServicePath -Destination $backupPath
    }

    $verifiedBackupHash = (Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash
    if (-not $verifiedBackupHash.Equals($ExpectedInstalledServiceSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The pre-replacement backup failed its post-copy hash verification.'
    }

    Copy-Item -LiteralPath $publishedServicePath -Destination $stagedPath
    $stagedHash = (Get-FileHash -LiteralPath $stagedPath -Algorithm SHA256).Hash
    if (-not $stagedHash.Equals($publishedHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The staged service executable failed its pre-replacement hash verification.'
    }

    $replacementAttempted = $true
    [System.IO.File]::Replace($stagedPath, $expectedServicePath, $replaceBackupPath, $true)
    $replaceBackupHash = (Get-FileHash -LiteralPath $replaceBackupPath -Algorithm SHA256).Hash
    if (-not $replaceBackupHash.Equals($ExpectedInstalledServiceSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The atomic replacement backup does not match the verified old service executable.'
    }
    $installedNewHash = (Get-FileHash -LiteralPath $expectedServicePath -Algorithm SHA256).Hash
    if (-not $installedNewHash.Equals($publishedHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Updated service executable hash verification failed.'
    }

    $verifiedState = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    if ($verifiedState.State -ne 'Stopped' -or $verifiedState.ProcessId -ne 0) {
        throw 'The service unexpectedly started during the stopped binary replacement.'
    }

    [System.IO.File]::Delete($replaceBackupPath)

    Write-Result -Ok $true -Message 'Stopped live-test service binary replaced without starting it.' -Details (
        [pscustomobject]@{
            service = Select-ServiceDetails -Service $verifiedState
            previousSha256 = $installedHash.ToLowerInvariant()
            currentSha256 = $installedNewHash.ToLowerInvariant()
            backupPath = $backupPath
            manifestSha256 = $manifestHash.ToLowerInvariant()
        })
} catch {
    $originalError = $_.Exception.Message
    try {
        if (Test-Path -LiteralPath $stagedPath -PathType Leaf) {
            Remove-Item -LiteralPath $stagedPath -Force
        }
        if (Test-Path -LiteralPath $replaceBackupPath -PathType Leaf) {
            Remove-Item -LiteralPath $replaceBackupPath -Force
        }
    } catch {
        # Preserve the original failure and continue with the verified rollback attempt.
    }

    try {
        if ($oldInstallVerified -and $replacementAttempted -and
            (Test-Path -LiteralPath $backupPath -PathType Leaf)) {
            Copy-Item -LiteralPath $backupPath -Destination $expectedServicePath -Force
            $oldBinaryRestored = (Get-FileHash -LiteralPath $expectedServicePath -Algorithm SHA256).Hash.Equals(
                $ExpectedInstalledServiceSha256,
                [StringComparison]::OrdinalIgnoreCase)
        }
    } catch {
        $oldBinaryRestored = $false
    }

    try {
        $failureState = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
        Write-Result -Ok $false -Message $originalError -Details ([pscustomobject]@{
            oldBinaryRestored = $oldBinaryRestored
            service = Select-ServiceDetails -Service $failureState
        })
    } catch {
        # Preserve the original failure as the process exit code.
    }

    exit 1
}
