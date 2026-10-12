. "$PSScriptRoot/Maintenance.ps1" -LoadFunctionsOnly

Describe 'Installer version comparisons' {
    It 'allows the same release with three or four fields' {
        { Assert-NoDowngrade '0.1.22.0' '0.1.22' } | Should Not Throw
        { Assert-NoDowngrade '0.1.22' '0.1.22.0' } | Should Not Throw
    }
    It 'allows numerically ordered upgrades' {
        { Assert-NoDowngrade '0.1.9.0' '0.1.22' } | Should Not Throw
        { Assert-NoDowngrade '0.1.22.0' '0.2.0' } | Should Not Throw
    }
    It 'rejects downgrades including nonzero revisions' {
        { Assert-NoDowngrade '0.1.23.0' '0.1.22' } | Should Throw
        { Assert-NoDowngrade '0.1.22.1' '0.1.22' } | Should Throw
    }
    It 'rejects missing and malformed versions' {
        foreach ($value in '', '0.1', '0.1.22-beta', 'invalid') {
            { Assert-NoDowngrade $value '0.1.22' } | Should Throw
            { Assert-NoDowngrade '0.1.22' $value } | Should Throw
        }
    }
}

Describe 'NSIS payload and path validation (no device or service access)' {
    It 'rejects traversal, absolute paths and alternate streams' {
        foreach ($relative in '..\outside','a\..\..\outside','C:\outside','file:stream','\outside') {
            { Resolve-SafeChild $TestDrive $relative } | Should Throw
        }
    }
    It 'resolves a normal child beneath the exact root' {
        (Resolve-SafeChild $TestDrive 'settings\app.exe') | Should Be (Join-Path $TestDrive 'settings\app.exe')
    }
    It 'restores replaced files and removes only newly introduced files' {
        $folder=Join-Path $TestDrive 'snapshot'; $null=New-Item -ItemType Directory $folder
        $old=Join-Path $folder 'old'; $new=Join-Path $folder 'new'
        'before' | Set-Content $old
        $backup=Join-Path $folder 'backup'; $null=New-Item -ItemType Directory $backup
        $snapshot=Save-FileSnapshot @($old,$new) $backup
        'after' | Set-Content $old; 'new' | Set-Content $new
        Restore-FileSnapshot $snapshot
        (Get-Content $old) | Should Be 'before'
        (Test-Path $new) | Should Be $false
    }
    It 'accepts exact bytes and rejects tampering and duplicate manifest entries' {
        $root=Join-Path $TestDrive 'manifest'; $null=New-Item -ItemType Directory $root
        $names=@('service\V12UltraController.Service.exe','settings\V12UltraController.Settings.exe','cli\v12ctl.exe','a','b','c','d','e')
        $files=@(foreach ($name in $names) {
            $path=Join-Path $root $name; $null=New-Item -ItemType Directory ([IO.Path]::GetDirectoryName($path)) -Force
            'fake payload, never executable' | Set-Content $path
            @{path=$name;sha256=(Get-FileHash $path).Hash}
        })
        $manifest=@{version='0.1.22';files=$files}
        $manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $root 'payload.json')
        (Read-Manifest $root).version | Should Be '0.1.22'
        'tampered' | Set-Content (Join-Path $root 'a')
        { Read-Manifest $root } | Should Throw
        $manifest.files=@($files[0],$files[0])+$files
        $manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $root 'payload.json')
        { Read-Manifest $root } | Should Throw
    }
}

Describe 'NSIS process and file-lock handling' {
    It 'waits for a stopped service process to disappear' {
        Mock Get-Process { $null }
        Mock Start-Sleep { }
        { Wait-ServiceProcessStopped @(4321) -TimeoutSeconds 1 } | Should Not Throw
        Assert-MockCalled Get-Process -Times 1 -Exactly
    }
    It 'reports a locked file without changing it' {
        $path=Join-Path $TestDrive 'locked.exe'
        'locked' | Set-Content $path
        $stream=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        try { { Wait-FileAvailable $path -TimeoutSeconds 0 } | Should Throw }
        finally { $stream.Dispose() }
        (Get-Content $path) | Should Be 'locked'
    }
}

Describe 'NSIS installation transaction with fake SCM and registry' {
    BeforeEach {
        $script:InstallRoot=Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $script:Payload=Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $script:DataRoot=Join-Path $TestDrive 'never-touch-data'
        $script:Preferences='FAKE-PREFERENCES'; $script:UninstallKey='FAKE-ARP'
        $script:ServiceName='FAKE-SERVICE'; $script:Sc='NEVER-EXECUTE'
        $script:Desktop=1; $script:StartMenu=1
        $script:failStart=$false; $script:failRollback=$false; $script:failDelete=$false
        $null=New-Item -ItemType Directory $script:Payload
        $null=New-Item -ItemType Directory $script:InstallRoot
        foreach ($name in 'app.bin','payload.json','Uninstall.exe') { 'new' | Set-Content (Join-Path $script:Payload $name) }
        'old' | Set-Content (Join-Path $script:InstallRoot 'app.bin')
        'manifest' | Set-Content (Join-Path $script:InstallRoot 'payload.json')
        $script:fakeService=[pscustomobject]@{State='Running';StartMode='Manual'}
        Mock Read-Manifest { [pscustomobject]@{version='0.1.22';files=@([pscustomobject]@{path='app.bin'})} }
        Mock Get-OwnedService { $script:fakeService }
        Mock Get-EnrollmentSid { 'S-1-5-21-1-2-3-1001' }
        Mock Assert-InstallIdentity { 'S-1-5-21-1-2-3-1001' }
        Mock Invoke-Checked {
            if ($script:failRollback -and $Arguments[0] -eq 'commission') { throw 'rollback failed' }
            if ($script:failDelete -and $Arguments[0] -eq 'delete') { throw 'SCM deletion failed' }
        }
        Mock Assert-InstallPrerequisites { }
        Mock Assert-SettingsClosed { }
        Mock Get-ShortcutPaths { @((Join-Path $script:InstallRoot 'desktop.lnk'),(Join-Path $script:InstallRoot 'menu.lnk')) }
        Mock Assert-ShortcutOwnership { }
        Mock Set-PrivateDirectory { param($Path) $null=New-Item -ItemType Directory $Path -Force }
        Mock Set-ServiceRunning { if ($script:failStart -and $Running) { throw 'injected start failure' } }
        Mock Set-OwnedShortcut { }
        Mock Save-RegistryValues { param($Path) @{path=$Path;exists=$false;values=@()} }
        Mock Restore-RegistryValues { }
        Mock Write-InstallRegistry { }
        Mock New-Enrollment { }
        Mock Complete-UninstallRegistry { }
        Mock Get-Service { [pscustomobject]@{Status='Running'} }
    }
    It 'upgrades files and preserves service startup configuration' {
        Invoke-Install
        (Get-Content (Join-Path $script:InstallRoot 'app.bin')) | Should Be 'new'
        Assert-MockCalled -Scope It Set-ServiceRunning -Times 1 -ParameterFilter { $Running -eq $false }
        Assert-MockCalled -Scope It Set-ServiceRunning -Times 1 -ParameterFilter { $Running -eq $true }
        Assert-MockCalled -Scope It Invoke-Checked -Times 0 -ParameterFilter { $Arguments[0] -in 'create','config','failure','delete' }
        Assert-MockCalled -Scope It New-Enrollment -Times 0
    }
    It 'checks each destination before replacing an existing file' {
        Mock Wait-FileAvailable { }
        Invoke-Install
        Assert-MockCalled -Scope It Wait-FileAvailable -Times 1 -ParameterFilter { $Path -like '*app.bin' }
    }
    It 'keeps a stopped existing service stopped after upgrade' {
        $script:fakeService.State='Stopped'
        Invoke-Install
        Assert-MockCalled -Scope It Set-ServiceRunning -Times 0 -ParameterFilter { $Running -eq $true }
    }
    It 'passes two unchecked shortcut choices independently' {
        $script:Desktop=0; $script:StartMenu=0
        Invoke-Install
        Assert-MockCalled -Scope It Set-OwnedShortcut -Times 2 -Exactly -ParameterFilter { $Enabled -eq $false }
    }
    It 'passes desktop off and start menu on independently' {
        $script:Desktop=0; $script:StartMenu=1
        Invoke-Install
        Assert-MockCalled -Scope It Set-OwnedShortcut -Times 1 -ParameterFilter { $Path -like '*desktop.lnk' -and -not $Enabled }
        Assert-MockCalled -Scope It Set-OwnedShortcut -Times 1 -ParameterFilter { $Path -like '*menu.lnk' -and $Enabled }
    }
    It 'passes desktop on and start menu off independently' {
        $script:Desktop=1; $script:StartMenu=0
        Invoke-Install
        Assert-MockCalled -Scope It Set-OwnedShortcut -Times 1 -ParameterFilter { $Path -like '*desktop.lnk' -and $Enabled }
        Assert-MockCalled -Scope It Set-OwnedShortcut -Times 1 -ParameterFilter { $Path -like '*menu.lnk' -and -not $Enabled }
    }
    It 'blocks a running settings process before stopping the service or copying files' {
        Mock Assert-SettingsClosed { throw 'UI running' }
        { Invoke-Install } | Should Throw
        (Get-Content (Join-Path $script:InstallRoot 'app.bin')) | Should Be 'old'
        Assert-MockCalled -Scope It Set-ServiceRunning -Times 0
    }
    It 'rejects a missing enrollment for an existing service without mutations' {
        Mock Get-EnrollmentSid { $null }
        { Invoke-Install } | Should Throw
        Assert-MockCalled -Scope It Set-ServiceRunning -Times 0
        Assert-MockCalled -Scope It New-Enrollment -Times 0
    }
    It 'rolls back files and registry when registration fails' {
        Mock Write-InstallRegistry { throw 'injected registry failure' }
        { Invoke-Install } | Should Throw
        (Get-Content (Join-Path $script:InstallRoot 'app.bin')) | Should Be 'old'
        (Test-Path (Join-Path $script:InstallRoot 'Uninstall.exe')) | Should Be $false
        Assert-MockCalled -Scope It Restore-RegistryValues -Times 2 -Exactly
        Assert-MockCalled -Scope It Set-ServiceRunning -Times 1 -ParameterFilter { $Running -eq $true }
    }
    It 'rolls back files after a service start failure' {
        $script:failStart=$true
        { Invoke-Install } | Should Throw
        (Get-Content (Join-Path $script:InstallRoot 'app.bin')) | Should Be 'old'
    }
    It 'creates the fixed service only on fresh install and validates enrollment' {
        $script:fakeService=$null
        Mock Get-EnrollmentSid { $null }
        Invoke-Install
        Assert-MockCalled -Scope It New-Enrollment -Times 1 -Exactly
        Assert-MockCalled -Scope It Invoke-Checked -Times 1 -ParameterFilter { $Arguments[0] -eq 'create' -and $Arguments[1] -eq 'FAKE-SERVICE' }
        Assert-MockCalled -Scope It Invoke-Checked -Times 1 -ParameterFilter { $Arguments[0] -eq 'installer-preflight' -and $Arguments[1] -eq 'existing' }
    }
    It 'fails uninstall before deleting files when verified rollback fails' {
        $script:failRollback=$true
        { Invoke-Uninstall } | Should Throw
        (Get-Content (Join-Path $script:InstallRoot 'app.bin')) | Should Be 'old'
        Assert-MockCalled -Scope It Invoke-Checked -Times 0 -ParameterFilter { $Arguments[0] -eq 'delete' }
        Assert-MockCalled -Scope It Set-OwnedShortcut -Times 0
    }
    It 'does not treat a missing service as proof that rollback succeeded' {
        $script:fakeService=$null
        { Invoke-Uninstall } | Should Throw
        (Get-Content (Join-Path $script:InstallRoot 'app.bin')) | Should Be 'old'
        Assert-MockCalled -Scope It Invoke-Checked -Times 0
    }
    It 'uninstalls only after the enrolled-user rollback succeeds' {
        Invoke-Uninstall
        (Test-Path (Join-Path $script:InstallRoot 'app.bin')) | Should Be $false
        Assert-MockCalled -Scope It Invoke-Checked -Times 1 -ParameterFilter { $Arguments[0] -eq 'commission' -and $Arguments -contains '--attended' -and $Arguments -notcontains '--installer' }
        Assert-MockCalled -Scope It Invoke-Checked -Times 1 -ParameterFilter { $Arguments[0] -eq 'delete' }
        Assert-MockCalled -Scope It Complete-UninstallRegistry -Times 1
    }
    It 'restores files if service removal fails and keeps uninstall registration' {
        $script:failDelete=$true
        { Invoke-Uninstall } | Should Throw
        (Get-Content (Join-Path $script:InstallRoot 'app.bin')) | Should Be 'old'
        Assert-MockCalled -Scope It Complete-UninstallRegistry -Times 0
    }
}
