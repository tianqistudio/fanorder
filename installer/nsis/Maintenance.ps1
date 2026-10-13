#requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('Install','Uninstall')][string]$Mode,
      [string]$Payload, [ValidateSet(0,1)][int]$Desktop=1,
      [ValidateSet(0,1)][int]$StartMenu=1, [switch]$LoadFunctionsOnly, [ValidateSet(1033,2052)][int]$Language=2052)
function Get-InstallerText([string]$Chinese,[string]$English) {
    if ($Language -eq 1033) { return $English }
    return $Chinese
}
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest

function Resolve-SafeChild([string]$Root,[string]$Relative) {
    if ([IO.Path]::IsPathRooted($Relative) -or $Relative -match '(^|[\\/])\.\.([\\/]|$)|:') { throw 'Unsafe manifest path' }
    $base=[IO.Path]::GetFullPath($Root).TrimEnd('\')
    $path=[IO.Path]::GetFullPath((Join-Path $base $Relative))
    if (-not $path.StartsWith($base+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Path escaped root' }
    Assert-NoReparse $path
    return $path
}
function Assert-NoReparse([string]$Path) {
    $cursor=[IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Reparse point rejected: $cursor" }
        $cursor=[IO.Path]::GetDirectoryName($cursor)
    }
}
function Read-Manifest([string]$Root) {
    $manifest=Get-Content -LiteralPath (Resolve-SafeChild $Root 'payload.json') -Raw | ConvertFrom-Json
    if ($manifest.version -notmatch '^\d+\.\d+\.\d+$' -or @($manifest.files).Count -lt 8) { throw 'Invalid payload manifest' }
    $seen=@{}
    foreach ($entry in $manifest.files) {
        $path=Resolve-SafeChild $Root $entry.path
        if ($seen.ContainsKey($path) -or $entry.sha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Duplicate/invalid manifest entry' }
        $seen[$path]=$true
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Payload checksum mismatch: $($entry.path)" }
    }
    foreach ($required in 'service\V12UltraController.Service.exe','settings\V12UltraController.Settings.exe','cli\v12ctl.exe') {
        if (-not $seen.ContainsKey((Resolve-SafeChild $Root $required))) { throw "Missing executable: $required" }
    }
    return $manifest
}
function Invoke-Checked([string]$Exe,[string[]]$Arguments) {
    $output=& $Exe @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "$([IO.Path]::GetFileName($Exe)) failed ($LASTEXITCODE): $($output -join ' ')" }
    $output | ForEach-Object { Write-Host $_ }
}
function Set-PrivateDirectory([string]$Path) {
    Assert-NoReparse $Path
    $null=New-Item -ItemType Directory -Path $Path -Force
    $acl=New-Object Security.AccessControl.DirectorySecurity
    $acl.SetSecurityDescriptorSddlForm('D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)')
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function Set-ServiceRunning([bool]$Running) {
    $service=Get-Service -Name $script:ServiceName -ErrorAction Stop
    if ($Running) { Start-Service -InputObject $service; $service.WaitForStatus('Running',[TimeSpan]::FromSeconds(30)) }
    else {
        $processIds=@(Get-CimInstance Win32_Service -Filter "Name='$script:ServiceName'" -ErrorAction SilentlyContinue |
            Where-Object { $_.ProcessId -gt 0 } | ForEach-Object { [int]$_.ProcessId })
        Stop-Service -InputObject $service
        $service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30))
        Wait-ServiceProcessStopped $processIds
    }
}
function Wait-ServiceProcessStopped([int[]]$ProcessIds,[int]$TimeoutSeconds=30) {
    $tracked=@($ProcessIds | Where-Object { $_ -gt 0 } | Select-Object -Unique)
    if (-not $tracked.Count) { return }
    $deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true) {
        $alive=@($tracked | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
        if (-not $alive.Count) { return }
        if ([DateTime]::UtcNow -ge $deadline) {
            throw (Get-InstallerText "后台服务进程仍未退出（PID $($alive -join ',')），无法安全替换服务文件。请退出占用程序后重试。" "The backend service process has not exited (PID $($alive -join ',')); service files cannot be safely replaced. Exit the process and retry.")
        }
        Start-Sleep -Milliseconds 250
    }
}
function Wait-FileAvailable([string]$Path,[int]$TimeoutSeconds=30) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    $deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true) {
        try {
            $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
            $stream.Dispose()
            return
        } catch [IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) {
                throw (Get-InstallerText "文件仍被其他进程占用，无法安全替换：$Path。请退出占用程序后重试。" "File is still in use and cannot be safely replaced: $Path. Exit the process and retry.")
            }
            Start-Sleep -Milliseconds 250
        } catch [UnauthorizedAccessException] {
            throw (Get-InstallerText "没有权限访问待替换文件：$Path。" "Access denied to the file being replaced: $Path.")
        }
    }
}
function Get-OwnedService {
    $service=Get-CimInstance Win32_Service -Filter "Name='$script:ServiceName'"
    if ($service) {
        $expected=Join-Path $script:InstallRoot 'service\V12UltraController.Service.exe'
        if ($service.PathName.Trim('"') -ine $expected -or $service.StartName -ne 'LocalSystem' -or $service.StartMode -notin 'Auto','Manual') { throw 'Existing service ownership/start mode mismatch; nothing changed.' }
    }
    return $service
}
function Assert-SettingsClosed {
    $processes=@(Get-CimInstance Win32_Process -Filter "Name='V12UltraController.Settings.exe' OR Name='v12ctl.exe'")
    if ($processes.Count) { throw (Get-InstallerText '请先从托盘菜单退出风序，并关闭 v12ctl，然后重试。' 'Exit FanOrder from the tray and close v12ctl, then retry.') }
}
function Get-ShortcutPaths {
    return @((Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) '风序 · FanOrder.lnk'),
        (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'tianqistudio\风序 · FanOrder.lnk'))
}
function Set-OwnedShortcut([string]$Path,[bool]$Enabled) {
    Assert-NoReparse $Path
    $shell=New-Object -ComObject WScript.Shell
    try {
        $target=Join-Path $script:InstallRoot 'settings\V12UltraController.Settings.exe'
        if (Test-Path -LiteralPath $Path) {
            $old=$shell.CreateShortcut($Path)
            if ($old.TargetPath -ine $target -or $old.Arguments) { throw "Shortcut belongs to another target: $Path" }
        }
        if ($Enabled) {
            $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($Path)) -Force
            $link=$shell.CreateShortcut($Path); $link.TargetPath=$target
            $link.WorkingDirectory=[IO.Path]::GetDirectoryName($target)
            $link.IconLocation=$target+',0'
            $link.Description='风序 · FanOrder'; $link.Save()
            if ($shell.CreateShortcut($Path).TargetPath -ine $target) { throw 'Shortcut readback failed' }
        } elseif (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force }
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}
function Save-FileSnapshot([string[]]$Paths,[string]$Backup) {
    $result=@(); $index=0
    foreach ($path in $Paths | Select-Object -Unique) {
        Assert-NoReparse $path
        $exists=Test-Path -LiteralPath $path -PathType Leaf
        $copy=Join-Path $Backup ([string]$index++)
        if ($exists) { Copy-Item -LiteralPath $path -Destination $copy }
        $result+=@{path=$path;exists=$exists;copy=$copy}
    }
    return $result
}
function Restore-FileSnapshot($Snapshot) {
    foreach ($item in $Snapshot) {
        Assert-NoReparse $item.path
        if ($item.exists) { Wait-FileAvailable $item.path; Copy-Item -LiteralPath $item.copy -Destination $item.path -Force }
        elseif (Test-Path -LiteralPath $item.path) { Wait-FileAvailable $item.path; Remove-Item -LiteralPath $item.path -Force }
    }
}
function Save-RegistryValues([string]$Path) {
    $key=Get-Item -LiteralPath $Path -ErrorAction SilentlyContinue
    $values=@()
    if ($key) { foreach ($name in $key.GetValueNames()) { $values+=@{name=$name;value=$key.GetValue($name);kind=$key.GetValueKind($name).ToString()} } }
    return @{path=$Path;exists=($null -ne $key);values=$values}
}
function Restore-RegistryValues($Snapshot) {
    if (Test-Path -LiteralPath $Snapshot.path) {
        $key=Get-Item -LiteralPath $Snapshot.path
        foreach ($name in $key.GetValueNames()) { Remove-ItemProperty -LiteralPath $Snapshot.path -Name $name }
    }
    if ($Snapshot.exists) {
        $null=New-Item -Path $Snapshot.path -Force
        foreach ($value in $Snapshot.values) { $null=New-ItemProperty -LiteralPath $Snapshot.path -Name $value.name -Value $value.value -PropertyType $value.kind -Force }
    } elseif (Test-Path -LiteralPath $Snapshot.path) { Remove-Item -LiteralPath $Snapshot.path }
}
function Assert-InstallIdentity([string]$EnrolledSid) {
    $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if ($sid -notmatch '^S-1-5-21-\d+-\d+-\d+-\d+$|^S-1-12-1-\d+-\d+-\d+-\d+$') { throw 'A specific interactive Windows user is required.' }
    if ($EnrolledSid) {
        if ($sid -ne $EnrolledSid) { throw (Get-InstallerText '请使用首次安装时登记的 Windows 用户运行安装或卸载；不会替换已有用户登记。' 'Run setup as the Windows user registered at first installation. Existing user registration will not be replaced.') }
    } else {
        $session=(Get-Process -Id $PID).SessionId
        $shells=@(Get-CimInstance Win32_Process -Filter "Name='explorer.exe' AND SessionId=$session")
        $owners=@($shells | ForEach-Object { (Invoke-CimMethod -InputObject $_ -MethodName GetOwnerSid).Sid } | Select-Object -Unique)
        if ($owners.Count -ne 1 -or $owners[0] -ne $sid) { throw (Get-InstallerText '请在本人 Windows 桌面运行安装器，并用同一账户确认 UAC；不支持借用其他管理员账户安装。' 'Run setup from your own Windows desktop and approve UAC with the same account. Installation using another administrator account is not supported.') }
    }
    return $sid
}
function Get-EnrollmentSid {
    $enrollmentKey=Get-Item -LiteralPath $script:Enrollment -ErrorAction SilentlyContinue
    if ($enrollmentKey) { return $enrollmentKey.GetValue('AuthorizedUserSid') }
    return $null
}
function ConvertTo-InstallerVersion([string]$Value) {
    if ($Value -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw "Invalid installer version: $Value" }
    $version=[version]$Value
    return [version]::new($version.Major,$version.Minor,$version.Build,[Math]::Max(0,$version.Revision))
}
function Assert-NoDowngrade([string]$InstalledVersion,[string]$PackageVersion) {
    if ((ConvertTo-InstallerVersion $InstalledVersion) -gt (ConvertTo-InstallerVersion $PackageVersion)) {
        throw (Get-InstallerText "已安装较新版本（$InstalledVersion），不能安装旧版本（$PackageVersion）。" "A newer version ($InstalledVersion) is installed; cannot install older version ($PackageVersion).")
    }
}
function Assert-InstallPrerequisites($Enrolled,$Manifest) {
    $msiProducts=@(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object {
        $_.PSObject.Properties['WindowsInstaller'] -and $_.WindowsInstaller -eq 1 -and
        $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -match 'FanOrder|V12 Ultra Controller'
    })
    if ($msiProducts.Count) { throw (Get-InstallerText '检测到 MSI 管理的安装，请先通过其卸载程序安全卸载；本 EXE 不覆盖 MSI 的组件登记。' 'An MSI-managed installation was detected. Safely uninstall it first; this EXE will not overwrite MSI component registration.') }
    if (-not $Enrolled) {
        $pawn=Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO' -ErrorAction SilentlyContinue
        if (-not $pawn -or $pawn.DisplayVersion -ne '2.2.0.0') { throw (Get-InstallerText '首次安装需要官方签名版 PawnIO 2.2.0（pawnio.eu）。安装器不包含或自动安装驱动。' 'First installation requires the officially signed PawnIO 2.2.0 (pawnio.eu). Setup does not bundle or automatically install drivers.') }
        if (Test-Path -LiteralPath $script:DataRoot) { throw (Get-InstallerText '检测到未登记的旧数据；请先恢复用户登记，避免误接管旧配置。' 'Unregistered existing data was found. Restore user registration first to avoid taking over old configuration.') }
    }
    $installedExe=Join-Path $script:InstallRoot 'service\V12UltraController.Service.exe'
    if (Test-Path -LiteralPath $installedExe) {
        $oldVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo($installedExe).FileVersion
        Assert-NoDowngrade $oldVersion $Manifest.version
        if (-not $Enrolled) { throw 'Unowned installed files; refusing overwrite.' }
    }
}
function Assert-ShortcutOwnership($Shortcuts) {
    $shell=New-Object -ComObject WScript.Shell
    try { foreach ($path in $Shortcuts) {
        Assert-NoReparse $path
        if (Test-Path -LiteralPath $path) {
            $link=$shell.CreateShortcut($path)
            if ($link.TargetPath -ine (Join-Path $script:InstallRoot 'settings\V12UltraController.Settings.exe') -or $link.Arguments) { throw "Shortcut collision: $path" }
        }
    } } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}
function New-Enrollment([string]$Sid) {
    Set-PrivateDirectory $script:DataRoot
    Set-PrivateDirectory (Join-Path $script:DataRoot 'logs')
    $null=New-Item -Path $script:Enrollment -Force
    $acl=New-Object Security.AccessControl.RegistrySecurity
    $acl.SetSecurityDescriptorSddlForm('D:P(A;;KA;;;SY)(A;;KA;;;BA)')
    Set-Acl -LiteralPath $script:Enrollment -AclObject $acl
    $null=New-ItemProperty -LiteralPath $script:Enrollment -Name AuthorizedUserSid -Value $Sid -PropertyType String
}
function Write-InstallRegistry([string]$Version) {
    $null=New-Item -Path $script:Preferences -Force
    $null=New-ItemProperty $script:Preferences -Name DesktopShortcut -Value $Desktop -PropertyType DWord -Force
    $null=New-ItemProperty $script:Preferences -Name StartMenuShortcut -Value $StartMenu -PropertyType DWord -Force
    $null=New-Item -Path $script:UninstallKey -Force
    $values=@{DisplayName='风序 · FanOrder';DisplayVersion=$Version;Publisher='tianqistudio';URLInfoAbout='https://tianqistudio.com';InstallLocation=$script:InstallRoot;UninstallString=('"'+(Join-Path $script:InstallRoot 'Uninstall.exe')+'"')}
    foreach ($name in $values.Keys) { $null=New-ItemProperty $script:UninstallKey -Name $name -Value $values[$name] -PropertyType String -Force }
    foreach ($name in 'NoModify','NoRepair') { $null=New-ItemProperty $script:UninstallKey -Name $name -Value 1 -PropertyType DWord -Force }
}
function Invoke-Install {
    $manifest=Read-Manifest $Payload
    $service=Get-OwnedService
    $enrolled=Get-EnrollmentSid
    $sid=Assert-InstallIdentity $enrolled
    if ($service -and -not $enrolled) { throw 'Existing service has no enrollment. Repair enrollment first.' }
    $cli=Resolve-SafeChild $Payload 'cli\v12ctl.exe'
    if ($enrolled) { Invoke-Checked $cli @('installer-preflight','existing') }
    else { Invoke-Checked $cli @('installer-preflight','fresh') }
    Assert-InstallPrerequisites $enrolled $manifest
    Assert-SettingsClosed
    $installedExe=Join-Path $script:InstallRoot 'service\V12UltraController.Service.exe'
    $paths=@($manifest.files | ForEach-Object { Resolve-SafeChild $script:InstallRoot $_.path })
    $paths+=Resolve-SafeChild $script:InstallRoot 'payload.json'
    $paths+=Resolve-SafeChild $script:InstallRoot 'Uninstall.exe'
    $shortcuts=Get-ShortcutPaths; $paths+=$shortcuts
    Assert-ShortcutOwnership $shortcuts
    $null=New-Item -ItemType Directory -Path $script:InstallRoot -Force
    $backup=Join-Path $script:InstallRoot ('.nsis-backup-'+[guid]::NewGuid().ToString('N'))
    Set-PrivateDirectory $backup
    $snapshot=Save-FileSnapshot $paths $backup
    $registries=@((Save-RegistryValues $script:Preferences),(Save-RegistryValues $script:UninstallKey))
    @{files=$snapshot;registry=$registries;service=$service;enrolledSid=$enrolled} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $backup 'transaction.json') -Encoding UTF8
    $createdService=$false
    try {
        if ($service) { Set-ServiceRunning $false }
        foreach ($entry in $manifest.files) {
            $target=Resolve-SafeChild $script:InstallRoot $entry.path
            $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force
            Wait-FileAvailable $target
            Copy-Item -LiteralPath (Resolve-SafeChild $Payload $entry.path) -Destination $target -Force
        }
        foreach ($name in 'payload.json','Uninstall.exe') {
            $target=Join-Path $script:InstallRoot $name
            Wait-FileAvailable $target
            Copy-Item -LiteralPath (Join-Path $Payload $name) -Destination $target -Force
        }
        $null=Read-Manifest $script:InstallRoot
        if (-not $enrolled) {
            New-Enrollment $sid
        }
        Invoke-Checked (Join-Path $script:InstallRoot 'cli\v12ctl.exe') @('installer-preflight','existing')
        if (-not $service) {
            Invoke-Checked $script:Sc @('create',$script:ServiceName,'binPath=',('"'+$installedExe+'"'),'start=','auto','obj=','LocalSystem','DisplayName=','风序 · FanOrder 后台服务')
            $createdService=$true
            Invoke-Checked $script:Sc @('failure',$script:ServiceName,'reset=','86400','actions=','restart/5000/restart/15000/restart/60000')
        }
        $null=Get-OwnedService
        Set-OwnedShortcut $shortcuts[0] ($Desktop -eq 1)
        Set-OwnedShortcut $shortcuts[1] ($StartMenu -eq 1)
        Write-InstallRegistry $manifest.version
        if (-not $service -or $service.State -eq 'Running') { Set-ServiceRunning $true }
        'committed' | Set-Content (Join-Path $backup 'result.txt') -Encoding UTF8
        Write-Host (Get-InstallerText '安装完成。配置和登录托盘偏好已保留；首次安装请从开始菜单打开设置完成接管。' 'Installation completed. Configuration and login tray preferences retained. On first use, open Settings from the Start menu to commission the device.')
    } catch {
        $failure=$_
        try {
            if (Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue) { Set-ServiceRunning $false }
            if ($createdService) { Invoke-Checked $script:Sc @('delete',$script:ServiceName) }
            Restore-FileSnapshot $snapshot
            foreach ($registry in $registries) { Restore-RegistryValues $registry }
            # Retain a newly validated enrollment with retained ProgramData, allowing a retry
            # by the same user without silently assigning the data to another account.
            if ($service -and $service.State -eq 'Running') { Set-ServiceRunning $true }
            'rolled back; retained data and backup for inspection' | Set-Content (Join-Path $backup 'result.txt') -Encoding UTF8
        } catch { throw (Get-InstallerText "安装失败：$failure；自动回滚未完成：$_。恢复资料：$backup" "Installation failed: $failure; automatic rollback incomplete: $_. Recovery data: $backup") }
        throw (Get-InstallerText "安装失败，程序文件已回滚；保留数据与恢复资料：$backup。$failure" "Installation failed; application files rolled back. Data and recovery material retained: $backup. $failure")
    }
}
function Invoke-Uninstall {
    $enrolled=Get-EnrollmentSid
    if (-not $enrolled) { throw 'Missing enrollment; repair before uninstall.' }
    $null=Assert-InstallIdentity $enrolled
    Assert-SettingsClosed
    $manifest=Read-Manifest $script:InstallRoot
    $service=Get-OwnedService
    if (-not $service) { throw (Get-InstallerText '后台服务缺失，请先重新安装修复，再卸载。配置和恢复资料已保留。' 'Backend service missing. Reinstall to repair before uninstalling. Configuration and recovery data retained.') }
    Set-ServiceRunning $true
    # The enrolled user uses the normal attended API, never the LocalSystem-only installer flag.
    try { Invoke-Checked (Join-Path $script:InstallRoot 'cli\v12ctl.exe') @('commission','rollback','--attended','--start-mythcool','false') }
    catch {
        if ($service.State -ne 'Running') { Set-ServiceRunning $false }
        throw (Get-InstallerText "接管恢复未通过，卸载已停止，后台和文件均保留。$_" "Ownership recovery failed; uninstall stopped. Backend and files retained. $_")
    }
    Set-ServiceRunning $false
    # Do all potentially failing file/shortcut operations before deleting the service.
    $shortcuts=Get-ShortcutPaths
    $backup=Join-Path $script:InstallRoot ('.nsis-uninstall-backup-'+[guid]::NewGuid().ToString('N'))
    Set-PrivateDirectory $backup
    $paths=@($manifest.files | ForEach-Object { Resolve-SafeChild $script:InstallRoot $_.path })
    $snapshot=Save-FileSnapshot ($paths+$shortcuts) $backup
    $snapshot | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $backup 'files.json') -Encoding UTF8
    try {
        foreach ($shortcut in $shortcuts) { Set-OwnedShortcut $shortcut $false }
        foreach ($path in $paths) { Remove-Item -LiteralPath $path -Force }
        Invoke-Checked $script:Sc @('delete',$script:ServiceName)
    } catch { Restore-FileSnapshot $snapshot; throw (Get-InstallerText "卸载未完成，文件已恢复；接管已撤回。$_" "Uninstallation incomplete; files restored and ownership released. $_") }
    Complete-UninstallRegistry
    Remove-Item -LiteralPath (Join-Path $script:InstallRoot 'payload.json')
    # Enrollment, shortcut preferences, ProgramData and exact recovery backups survive uninstall.
    Write-Host (Get-InstallerText '卸载完成；配置、用户登记、审计和恢复备份已保留。' 'Uninstallation completed; configuration, user registration, audit records and recovery backups retained.')
}
function Complete-UninstallRegistry {
    $run='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $item=Get-ItemProperty -LiteralPath $run -Name FanOrder -ErrorAction SilentlyContinue
    $expected='"'+(Join-Path $script:InstallRoot 'settings\V12UltraController.Settings.exe')+'" --tray'
    if ($item -and $item.FanOrder -ceq $expected) { Remove-ItemProperty -LiteralPath $run -Name FanOrder }
    Remove-Item -LiteralPath $script:UninstallKey -ErrorAction SilentlyContinue
}

if ($LoadFunctionsOnly) { return }
try {
    if (-not [Environment]::Is64BitProcess -or [Environment]::OSVersion.Version.Build -lt 17763) { throw (Get-InstallerText '需要 Windows 10/11 x64 build 17763 或更高版本。' 'Windows 10/11 x64 build 17763 or later is required.') }
    if ((Get-CimInstance Win32_OperatingSystem).ProductType -ne 1) { throw (Get-InstallerText '仅支持 Windows 工作站版本。' 'Only Windows workstation editions are supported.') }
    $principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw (Get-InstallerText '需要管理员权限。' 'Administrator privileges are required.') }
    $script:InstallRoot=Join-Path $env:ProgramFiles 'tianqistudio\V12UltraController'
    $script:DataRoot=Join-Path $env:ProgramData 'TianqiStudio\V12UltraController'
    $script:ServiceName='TianqiStudio.V12UltraController'
    $script:Sc=Join-Path $env:SystemRoot 'System32\sc.exe'
    $script:Enrollment='HKLM:\Software\TianqiStudio\V12UltraController\Enrollment'
    $script:Preferences='HKLM:\Software\TianqiStudio\V12UltraController\Installer'
    $script:UninstallKey='HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\FanOrder.NSIS'
    Assert-NoReparse $script:InstallRoot; Assert-NoReparse $script:DataRoot
    $mutex=New-Object Threading.Mutex($false,'Global\TianqiStudio.FanOrder.NsisMaintenance')
    if (-not $mutex.WaitOne(0)) { throw (Get-InstallerText '另一个安装或卸载正在运行。' 'Another installation or uninstallation is running.') }
    try {
        if ($Mode -eq 'Install') { Invoke-Install }
        elseif ($Mode -eq 'Uninstall') { Invoke-Uninstall }
        else { throw 'Maintenance mode required' }
    } finally { $mutex.ReleaseMutex(); $mutex.Dispose() }
    exit 0
} catch { Write-Host $_.Exception.Message; exit 1 }
