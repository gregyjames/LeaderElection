<#
.DESCRIPTION
    Unit tests for InstallHelpers module.
.NOTES
    SPDX-License-Identifier: Unlicense
    Source: http://github.com/mrfootoyou/pstaskframework
#>
#Requires -Version 7.4
# spell:ignore appx,winget,choco,mytool,Contoso

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '', Justification = 'Chokes on Pester keywords.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Mocked functions may have unused parameters.')]
param()

Describe 'PSTaskFramework.InstallHelpers Module' {
    BeforeAll {
        Import-Module "$PSScriptRoot/InstallHelpers" -Scope Local -Verbose:$false

        # Mock Install-Module to prevent actual module installation in a poorly written test.
        Mock Install-Module -ModuleName InstallHelpers {
            throw 'Install-Module called from unit test!'
        }
    }

    BeforeEach {
        # Cache original PATH to restore after tests
        $script:originalPath = $env:PATH
    }
    AfterEach {
        # Restore original PATH after each test
        $env:PATH = $script:originalPath
    }

    Context 'Get-PackageManager' {
        It 'returns supported package managers for current platform with AllSupported' {
            $expected = @(
                if ($IsWindows) { 'winget'; 'choco' }
                if ($IsLinux) { 'apt'; 'dnf'; 'brew', 'brew:linux' }
                if ($IsMacOS) { 'brew', 'brew:macos' }
            )

            $result = @(Get-PackageManager -AllSupported)

            $result | Should -Be $expected
        }

        It 'returns only detected package managers when AllSupported is not specified' {
            Mock Get-Command -ModuleName InstallHelpers {
                param($Name)
                if ($Name -in @('winget', 'brew')) {
                    [PSCustomObject]@{ Name = $Name }
                }
            }

            $result = @(Get-PackageManager)

            if ($IsWindows) {
                $result | Should -Be @('winget')
                $result | Should -Not -Contain 'brew'
            }
            if ($IsLinux) {
                $result | Should -Contain 'brew'
                $result | Should -Contain 'brew:linux'
                $result | Should -Not -Contain 'winget'
            }
            if ($IsMacOS) {
                $result | Should -Contain 'brew'
                $result | Should -Contain 'brew:macos'
                $result | Should -Not -Contain 'winget'
            }
        }
    }

    Context 'Get-WellKnownAppInfo' {
        It 'returns all app info by default' {
            $result = Get-WellKnownAppInfo

            $result.Count | Should -BeGreaterThan 1
            $result.Name | Should -Contain 'dotnet-sdk-10'
            $result.Name | Should -Contain 'git'
            $result.Name | Should -Contain 'docker'
        }

        It 'returns app metadata for an exact app name' {
            $result = Get-WellKnownAppInfo -Name 'git'

            $result.Name | Should -BeExactly 'git'
            $result.Info | Should -BeOfType ([System.Collections.IDictionary])
            $result.Info['winget'] | Should -BeExactly 'Git.Git'
        }

        It 'supports wildcard app name lookup' {
            $result = @(Get-WellKnownAppInfo -Name 'dotnet*')

            $result.Count | Should -Be 1
            $result[0].Name | Should -BeExactly 'dotnet-sdk-10'
        }

        It 'throws for unknown app names with ErrorAction Stop' {
            { Get-WellKnownAppInfo -Name 'not-a-real-app' -ErrorAction Stop } | `
                Should -Throw "*not-a-real-app*not a well-known app*"
        }

        It 'does not throw for unknown wildcard' {
            $result = Get-WellKnownAppInfo -Name 'not-a-real-app*' -ErrorAction Stop

            $result | Should -BeNullOrEmpty
        }
    }

    Context 'Install-PackageManager' {
        It 'installs explicit package manager and returns its name' {
            Mock installWinget -ModuleName InstallHelpers { }

            $result = @(Install-PackageManager -PackageManager 'winget')

            $result | Should -Contain 'winget'
            Should -Invoke installWinget -ModuleName InstallHelpers -Times 1 -Exactly
        }

        It 'tries alternates for any and succeeds when a later manager installs' {
            Mock Get-PackageManager -ModuleName InstallHelpers -ParameterFilter { $AllSupported } {
                @('winget', 'choco')
            }

            Mock installWinget -ModuleName InstallHelpers {
                throw 'winget failed'
            }

            Mock installChocolatey -ModuleName InstallHelpers { }

            $result = @(
                Install-PackageManager -PackageManager 'any' `
                    -ErrorAction SilentlyContinue `
                    -WarningAction SilentlyContinue `
                    -WarningVariable installWarnings
            )
            $warningMessages = @($installWarnings).Message

            $result | Should -Contain 'choco'
            Should -Invoke installWinget -ModuleName InstallHelpers -Times 1 -Exactly
            Should -Invoke installChocolatey -ModuleName InstallHelpers -Times 1 -Exactly
            $warningMessages | Should -Contain 'winget failed'
        }

        It 'writes an error when any cannot install any supported package manager' {
            Mock Get-PackageManager -ModuleName InstallHelpers -ParameterFilter { $AllSupported } {
                @('winget', 'choco')
            }

            Mock installWinget -ModuleName InstallHelpers {
                throw 'winget failed'
            }

            Mock installChocolatey -ModuleName InstallHelpers {
                throw 'choco failed'
            }

            Mock Write-Error -ModuleName InstallHelpers { }

            $null = Install-PackageManager -PackageManager 'any' `
                -ErrorAction SilentlyContinue `
                -WarningAction SilentlyContinue `
                -WarningVariable installWarnings
            $warningMessages = @($installWarnings).Message

            Should -Invoke Write-Error -ModuleName InstallHelpers -Times 1
            $warningMessages | Should -Contain 'winget failed'
            $warningMessages | Should -Contain 'choco failed'
        }
    }

    Context 'Install-RequiredApp' {
        It 'throws when no install information exists for an app' {
            { Install-RequiredApp -AppsToInstall @{ 'missing-app' = $null } } |
            Should -Throw '*No install information found for*'
        }

        It 'skips installation for apps that are already up to date' {
            Mock installWithWinget -ModuleName InstallHelpers { }

            $apps = [ordered]@{
                'mytool' = [ordered]@{
                    executable = ''
                    version    = '1.2.3'
                    isUpToDate = { $true }
                    winget     = 'Contoso.MyTool'
                }
            }

            Install-RequiredApp -AppsToInstall $apps

            Should -Invoke installWithWinget -ModuleName InstallHelpers -Times 0 -Exactly
        }

        It 'uses ordered app method precedence over package manager discovery order' {
            Mock Get-PackageManager -ModuleName InstallHelpers {
                @('winget', 'choco')
            }

            Mock installWithWinget -ModuleName InstallHelpers { }
            Mock installWithChocolatey -ModuleName InstallHelpers { }

            $apps = [ordered]@{
                'mytool' = [ordered]@{
                    executable = ''
                    isUpToDate = { $false }
                    choco      = 'mytool'
                    winget     = 'Contoso.MyTool'
                }
            }

            Install-RequiredApp -AppsToInstall $apps

            Should -Invoke installWithChocolatey -ModuleName InstallHelpers -Times 1 -Exactly
            Should -Invoke installWithWinget -ModuleName InstallHelpers -Times 0 -Exactly
        }

        It 'writes an error when no supported installation method is available' {
            Mock Get-PackageManager -ModuleName InstallHelpers { @('winget') }
            Mock Write-Error -ModuleName InstallHelpers { }

            $apps = [ordered]@{
                'mytool' = [ordered]@{
                    executable = ''
                    isUpToDate = { $false }
                    custom     = 'value'
                }
            }

            Install-RequiredApp -AppsToInstall $apps -ErrorAction SilentlyContinue

            Should -Invoke Write-Error -ModuleName InstallHelpers -Times 1
        }
    }

    Context 'Internal helper functions' {
        Context 'installWinget' {
            It 'should throw on non-Windows' -Skip:$IsWindows {
                InModuleScope 'InstallHelpers' {
                    { installWinget } | Should -Throw '*not supported*'
                }
            }

            It 'should not install when already installed' -Skip:(-not $IsWindows) {
                InModuleScope 'InstallHelpers' {
                    Mock -Verifiable Get-Command -ParameterFilter { $name -eq 'winget' } {
                        @{Path = 'C:\winget.exe' }
                    }

                    installWinget

                    Should -InvokeVerifiable
                }
            }

            It 'should install on Windows with refreshed path' -Skip:(-not $IsWindows) {
                InModuleScope 'InstallHelpers' {
                    $script:wingetInstalled = $false
                    Mock -Verifiable Get-Command -param { $name -eq 'winget' } { $script:wingetInstalled ? @{Path = 'C:\winget.exe' } : $null }
                    Mock -Verifiable Get-AppxPackage { [PSCustomObject]@{ PackageFamilyName = 'Microsoft.DesktopAppInstaller_8wekyb3d8bbwe' } }
                    Mock -Verifiable Add-AppxPackage { }
                    Mock -Verifiable refreshEnvironment { $script:wingetInstalled = $true }

                    installWinget

                    Should -InvokeVerifiable
                    Should -Invoke Get-Command -Times 3 -Exactly
                }
            }

            It 'should fail when Get-AppXPackage not found' -Skip:(-not $IsWindows) {
                InModuleScope 'InstallHelpers' {
                    Mock -Verifiable Get-Command -ParameterFilter { $name -eq 'winget' } { $null }
                    Mock -Verifiable Get-Command -ParameterFilter { $name -eq 'Get-AppxPackage' } { $null }

                    { installWinget } | Should -Throw '*Your version of Windows may not support Winget*'

                    Should -InvokeVerifiable
                }
            }
            It 'should fail when App Installer package not found' -Skip:(-not $IsWindows) {
                InModuleScope 'InstallHelpers' {
                    Mock -Verifiable Get-Command -ParameterFilter { $name -eq 'winget' } { $null }
                    Mock -Verifiable Get-AppxPackage { }

                    { installWinget } | Should -Throw '*Install ''App Installer'' from the Microsoft Store*'

                    Should -InvokeVerifiable
                }
            }
            It 'should fail when Add-AppxPackage fails' -Skip:(-not $IsWindows) {
                InModuleScope 'InstallHelpers' {
                    Mock -Verifiable Get-Command -ParameterFilter { $name -eq 'winget' } { $null }
                    Mock -Verifiable Get-AppxPackage { [PSCustomObject]@{ PackageFamilyName = 'Microsoft.DesktopAppInstaller_8wekyb3d8bbwe' } }
                    Mock -Verifiable Add-AppxPackage { Write-Error "failed" }

                    { installWinget } | Should -Throw '*failed to re-register ''App Installer'' package*'

                    Should -InvokeVerifiable
                }
            }
        }

        Context 'installChocolatey' {
            It 'should throw on non-Windows' -Skip:$IsWindows {
                InModuleScope 'InstallHelpers' {
                    { installChocolatey } | Should -Throw '*not supported*'
                }
            }

            It 'should not install when already installed' -Skip:(-not $IsWindows) {
                InModuleScope 'InstallHelpers' {
                    Mock -Verifiable Get-Command -ParameterFilter { $name -eq 'choco' } {
                        @{Path = 'C:\choco.exe' }
                    }

                    installChocolatey

                    Should -InvokeVerifiable
                }
            }

            It 'should install on Windows' -Skip:(-not $IsWindows) {
                InModuleScope 'InstallHelpers' {
                    $script:installed = $false
                    Mock -Verifiable Get-Command -ParameterFilter { $name -eq 'choco' } {
                        $script:installed ? @{Path = 'C:\choco.exe' } : $null
                    }
                    Mock -Verifiable Get-ExecutionPolicy { 'Restricted' }
                    Mock -Verifiable Set-ExecutionPolicy -param { $Scope -eq 'Process' -and $ExecutionPolicy -eq 'RemoteSigned' -and $Force } { }
                    Mock -Verifiable Invoke-WebRequest -param { $Uri -like 'https://*chocolatey.org/install.ps1' } {
                        [PSCustomObject]@{ Content = 'choco install script' }
                    }
                    Mock -Verifiable Invoke-Expression -param { $Command -eq 'choco install script' } { }
                    Mock -Verifiable refreshEnvironment { $script:installed = $true }

                    installChocolatey

                    Should -InvokeVerifiable
                }
            }
        }

        Context 'installApt' {
            It 'should throw on Windows' -Skip:($IsMacOS -or $IsLinux) {
                InModuleScope 'InstallHelpers' {
                    { installApt } | Should -Throw '*not supported*'
                }
            }

            It 'should succeed when apt-get already installed' -Skip:(-not ($IsMacOS -or $IsLinux)) {
                InModuleScope 'InstallHelpers' {
                    Mock -Verifiable Get-Command -param { $name -eq 'apt-get' } { @{Path = "/usr/bin/apt-get" } }

                    installApt

                    Should -InvokeVerifiable
                }
            }

            It 'should fail when apt-get is not installed' -Skip:(-not ($IsMacOS -or $IsLinux)) {
                InModuleScope 'InstallHelpers' {
                    Mock -Verifiable Get-Command -param { $name -eq 'apt-get' } { $null }

                    { installApt } | Should -Throw '*Manually install APT and try again*'

                    Should -InvokeVerifiable
                }
            }
        }

        Context 'installDNF' {
            It 'should throw on Windows' -Skip:($IsMacOS -or $IsLinux) {
                InModuleScope 'InstallHelpers' {
                    { installDNF } | Should -Throw '*not supported*'
                }
            }

            It 'should succeed when dnf already installed' -Skip:(-not ($IsMacOS -or $IsLinux)) {
                InModuleScope 'InstallHelpers' {
                    Mock -Verifiable Get-Command -param { $name -eq 'dnf' } { @{Path = "/usr/bin/dnf" } }

                    installDNF

                    Should -InvokeVerifiable
                }
            }

            It 'should fail when dnf is not installed' -Skip:(-not ($IsMacOS -or $IsLinux)) {
                InModuleScope 'InstallHelpers' {
                    Mock -Verifiable Get-Command -param { $name -eq 'dnf' } { $null }

                    { installDNF } | Should -Throw '*Manually install DNF and try again*'

                    Should -InvokeVerifiable
                }
            }
        }

        Context 'installBrew' {
            It 'should throw on Windows' -Skip:($IsMacOS -or $IsLinux) {
                InModuleScope 'InstallHelpers' {
                    { installBrew } | Should -Throw '*not supported*'
                }
            }

            It 'should succeed when brew already installed' -Skip:(-not ($IsMacOS -or $IsLinux)) {
                InModuleScope 'InstallHelpers' {
                    Mock -Verifiable Get-Command -param { $name -eq 'brew' } { @{Path = "/usr/bin/brew" } }

                    installBrew

                    Should -InvokeVerifiable
                }
            }

            It 'should fail when brew is not installed' -Skip:(-not ($IsMacOS -or $IsLinux)) {
                InModuleScope 'InstallHelpers' {
                    Mock -Verifiable Get-Command -param { $name -eq 'brew' } { $null }

                    { installBrew } | Should -Throw '*Manually install Homebrew*'

                    Should -InvokeVerifiable
                }
            }
        }

        It 'installWithPackageManager batches package ids and executes custom methods' {
            InModuleScope 'InstallHelpers' {
                $script:execCalls = @()
                $script:installCalls = @()

                $apps = @(
                    [PSCustomObject]@{
                        Name = 'pkg-a'
                        Info = @{ winget = 'Contoso.A' }
                    }
                    [PSCustomObject]@{
                        Name = 'pkg-b'
                        Info = @{ winget = [string[]]@('upgrade', 'Contoso.B') }
                    }
                    [PSCustomObject]@{
                        Name = 'pkg-c'
                        Info = @{
                            winget = {
                                param($appName, $appInfo, $execute)
                                & $execute -- install 'Contoso.C'
                            }
                        }
                    }
                    [PSCustomObject]@{
                        Name = 'pkg-d'
                        Info = @{
                            winget = [ordered]@{
                                PMArgs           = 'install', 'Contoso.D'
                                DoNotAppendArgs  = $true
                                AllowedExitCodes = 0, 3010
                            }
                        }
                    }
                    [PSCustomObject]@{
                        Name = 'pkg-a'
                        Info = @{ winget = 'Contoso.E' }
                    }
                )

                $execute = { $script:execCalls += , @($args) }
                $installPackages = { $script:installCalls += , @($args) }

                installWithPackageManager `
                    -AppsToInstall $apps `
                    -MethodName 'winget' `
                    -PackageManagerName 'Winget' `
                    -Execute $execute `
                    -InstallPackages $installPackages

                $apps | Should -HaveCount 5
                $script:installCalls | Should -HaveCount 1
                $script:installCalls[0] | Should -Be ('Contoso.A', 'Contoso.E')
                $script:execCalls | Should -HaveCount 3
                $script:execCalls[0] | Should -Be ('upgrade', 'Contoso.B')
                $script:execCalls[1] | Should -Be ('install', 'Contoso.C')
                $script:execCalls[2] | Should -Be @('-PMArgs:', @('install', 'Contoso.D'), '-DoNotAppendArgs:', $true, '-AllowedExitCodes:', @(0, 3010))
            }
        }

        It 'installWithPackageManager works with hash table installation method' {
            InModuleScope 'InstallHelpers' {
                $script:execCalls = @()
                $script:installCalls = @()

                $apps = @(
                    [PSCustomObject]@{
                        Name = 'pkg-a'
                        Info = @{ winget = 'Contoso.A' }
                    }
                    [PSCustomObject]@{
                        Name = 'pkg-b'
                        Info = @{ winget = [string[]]@('upgrade', 'Contoso.B') }
                    }
                    [PSCustomObject]@{
                        Name = 'pkg-c'
                        Info = @{
                            winget = {
                                param($appName, $appInfo, $execute)
                                & $execute -- install 'Contoso.C'
                            }
                        }
                    }
                )

                $execute = { $script:execCalls += , @($args) }
                $installPackages = { $script:installCalls += , @($args) }

                installWithPackageManager `
                    -AppsToInstall $apps `
                    -MethodName 'winget' `
                    -PackageManagerName 'Winget' `
                    -Execute $execute `
                    -InstallPackages $installPackages

                $script:execCalls.Count | Should -Be 2
                $script:installCalls.Count | Should -Be 1
                $script:installCalls[0] | Should -Contain 'Contoso.A'
            }
        }

        It 'installWithPackageManager throws for unsupported method type' {
            InModuleScope 'InstallHelpers' {
                $apps = @(
                    [PSCustomObject]@{
                        Name = 'bad-app'
                        Info = @{ winget = 42 }
                    }
                )

                { installWithPackageManager -AppsToInstall $apps -MethodName 'winget' -PackageManagerName 'Winget' -Execute {} -InstallPackages {} } | `
                    Should -Throw '*Unexpected installation type*'
            }
        }

        It 'installWithWinget applies default flags and resets LASTEXITCODE' {
            InModuleScope 'InstallHelpers' {
                $script:wingetCalls = @()
                Mock Invoke-Shell {
                    param($Command, $CommandArgs, $AllowedExitCodes)
                    $script:wingetCalls += , [PSCustomObject]@{
                        Command          = $Command
                        CommandArgs      = @($CommandArgs)
                        AllowedExitCodes = @($AllowedExitCodes)
                    }
                    $global:LASTEXITCODE = 0
                }

                $global:LASTEXITCODE = 99
                $apps = @(
                    [PSCustomObject]@{
                        Name = 'git'
                        Info = @{ winget = 'Git.Git' }
                    }
                )

                installWithWinget -AppsToInstall $apps

                Should -Invoke Invoke-Shell -Times 1 -Exactly
                $global:LASTEXITCODE | Should -Be 0
                $script:wingetCalls[0].Command | Should -BeExactly 'winget'
                $script:wingetCalls[0].CommandArgs | Should -BeExactly @('install', 'Git.Git', '--exact', '--source', 'winget', '--silent', '--force', '--accept-package-agreements', '--accept-source-agreements')
            }
        }

        It 'installWithChocolatey as admin applies default flags and resets LASTEXITCODE' {
            InModuleScope 'InstallHelpers' {
                $script:chocoCalls = @()
                Mock Test-Administrator { $true }
                Mock Invoke-Shell {
                    param($Command, $CommandArgs, $AllowedExitCodes)
                    $script:chocoCalls += , [PSCustomObject]@{
                        Command          = $Command
                        CommandArgs      = @($CommandArgs)
                        AllowedExitCodes = @($AllowedExitCodes)
                    }
                    $global:LASTEXITCODE = 0
                }

                $global:LASTEXITCODE = 99
                $apps = @(
                    [PSCustomObject]@{
                        Name = 'git'
                        Info = @{ choco = 'git' }
                    }
                )

                installWithChocolatey -AppsToInstall $apps

                Should -Invoke Invoke-Shell -Times 1 -Exactly
                $global:LASTEXITCODE | Should -Be 0
                $script:chocoCalls[0].Command | Should -BeExactly 'choco'
                $script:chocoCalls[0].CommandArgs | Should -BeExactly @('upgrade', 'git', '--yes')
            }
        }

        It 'installWithChocolatey as non-admin uses Start-Process' {
            InModuleScope 'InstallHelpers' {
                $script:spCalls = @()
                Mock Test-Administrator { $false }
                Mock Assert-AppExists { 'c:\path\to\choco.exe' }
                Mock Start-Process {
                    param($FilePath, $ArgumentList, $Verb)
                    $script:spCalls += , [PSCustomObject]@{
                        FilePath     = $FilePath
                        ArgumentList = @($ArgumentList)
                        Verb         = $Verb
                    }
                    [PSCustomObject]@{ ExitCode = 2 } # nothing to do
                }

                $apps = @(
                    [PSCustomObject]@{
                        Name = 'foo-app'
                        Info = @{ choco = 'foo' }
                    }
                )

                installWithChocolatey -AppsToInstall $apps

                Should -Invoke Start-Process -Times 1 -Exactly
                $global:LASTEXITCODE | Should -Be 0
                $script:spCalls[0].FilePath | Should -BeExactly 'c:\path\to\choco.exe'
                $script:spCalls[0].ArgumentList | Should -BeExactly @('upgrade', 'foo', '--yes')
            }
        }

        It 'installWithAPT runs update once then installs all packages with yes flag' {
            InModuleScope 'InstallHelpers' {
                $script:aptCalls = @()
                Mock Invoke-Shell {
                    param($Command, $CommandArgs, $AllowedExitCodes)
                    $script:aptCalls += , [PSCustomObject]@{
                        Command     = $Command
                        CommandArgs = @($CommandArgs)
                    }
                    $global:LASTEXITCODE = 0
                }

                $apps = @(
                    [PSCustomObject]@{ Name = 'git'; Info = @{ apt = 'git' } }
                    [PSCustomObject]@{ Name = 'curl'; Info = @{ apt = 'curl' } }
                )

                installWithAPT -AppsToInstall $apps

                Should -Invoke Invoke-Shell -Times 2 -Exactly
                $script:aptCalls[0].Command | Should -BeExactly 'sudo'
                $script:aptCalls[0].CommandArgs | Should -BeExactly @('apt-get', 'update', '-y')
                $script:aptCalls[1].Command | Should -BeExactly 'sudo'
                $script:aptCalls[1].CommandArgs | Should -BeExactly @('apt-get', 'install', 'git', 'curl', '--yes')
            }
        }

        It 'installWithDNF installs all packages with yes flag' {
            InModuleScope 'InstallHelpers' {
                $script:dnfCalls = @()
                Mock Invoke-Shell {
                    param($Command, $CommandArgs, $AllowedExitCodes)
                    $script:dnfCalls += , [PSCustomObject]@{
                        Command     = $Command
                        CommandArgs = @($CommandArgs)
                    }
                    $global:LASTEXITCODE = 0
                }

                $apps = @(
                    [PSCustomObject]@{ Name = 'git'; Info = @{ dnf = 'git' } }
                    [PSCustomObject]@{ Name = 'curl'; Info = @{ dnf = 'curl' } }
                )

                installWithDNF -AppsToInstall $apps

                Should -Invoke Invoke-Shell -Times 1 -Exactly
                $script:dnfCalls[0].Command | Should -BeExactly 'sudo'
                $script:dnfCalls[0].CommandArgs | Should -BeExactly @('dnf', 'install', 'git', 'curl', '-y')
            }
        }

        It 'installWithBrew installs all packages with yes flag' {
            InModuleScope 'InstallHelpers' {
                $script:brewCalls = @()
                Mock Invoke-Shell {
                    param($Command, $CommandArgs, $AllowedExitCodes)
                    $script:brewCalls += , [PSCustomObject]@{
                        Command     = $Command
                        CommandArgs = @($CommandArgs)
                    }
                    if ($CommandArgs[0] -eq 'list' -and $CommandArgs[1] -eq 'git') {
                        $global:LASTEXITCODE = 1 # not installed
                    }
                    elseif ($CommandArgs[0] -eq 'upgrade' -and $CommandArgs[1] -eq 'curl') {
                        $global:LASTEXITCODE = 1 # nothing to upgrade
                    }
                    else {
                        $global:LASTEXITCODE = 0
                    }
                }

                $apps = @(
                    [PSCustomObject]@{ Name = 'git'; Info = @{ brew = 'git' } }
                    [PSCustomObject]@{ Name = 'curl'; Info = @{ brew = 'curl' } }
                )

                installWithBrew -AppsToInstall $apps

                Should -Invoke Invoke-Shell -Times 6 -Exactly
                $script:brewCalls[0].Command | Should -BeExactly 'brew'
                $script:brewCalls[0].CommandArgs | Should -BeExactly @('list', 'git')
                $script:brewCalls[1].Command | Should -BeExactly 'brew'
                $script:brewCalls[1].CommandArgs | Should -BeExactly @('update')
                $script:brewCalls[2].Command | Should -BeExactly 'brew'
                $script:brewCalls[2].CommandArgs | Should -BeExactly @('install', 'git', '--quiet')
                $script:brewCalls[3].Command | Should -BeExactly 'brew'
                $script:brewCalls[3].CommandArgs | Should -BeExactly @('list', 'curl')
                $script:brewCalls[4].Command | Should -BeExactly 'brew'
                $script:brewCalls[4].CommandArgs | Should -BeExactly @('upgrade', 'curl', '--quiet')
                $script:brewCalls[5].Command | Should -BeExactly 'brew'
                $script:brewCalls[5].CommandArgs | Should -BeExactly @('list', 'curl')
            }
        }

        It 'installWithScript runs script method for each app' {
            InModuleScope 'InstallHelpers' {
                $script:ran = @()
                $apps = @(
                    [PSCustomObject]@{
                        Name = 'app1'
                        Info = @{ script = { param($appName, $appInfo) $script:ran += $appName } }
                    }
                    [PSCustomObject]@{
                        Name = 'app2'
                        Info = @{ script = { param($appName, $appInfo) $script:ran += $appName } }
                    }
                )

                installWithScript -AppsToInstall $apps -MethodName 'script'

                $script:ran | Should -Be @('app1', 'app2')
            }
        }
    }

    Context "isPowerShellUpToDate" {
        It 'caches last version response' {
            InModuleScope 'InstallHelpers' {
                $latestVersion = $PSVersionTable.PSVersion.ToString()
                Mock Invoke-WebRequest {
                    [PSCustomObject]@{
                        StatusCode = 302
                        Headers    = @{ Location = @("https://github.com/PowerShell/PowerShell/releases/tag/v$latestVersion") }
                    }
                }

                $appInfo = [ordered]@{
                    data = @{
                        LatestVersion    = $latestVersion
                        NextVersionCheck = [DateTime]::Now.AddMinutes(-1) # past
                    }
                }

                isPowerShellUpToDate 'powershell' $appInfo | Should -BeTrue

                Should -Invoke Invoke-WebRequest -Times 1 -Exactly
                $appInfo.data.LatestVersion.ToString() | Should -BeExactly $latestVersion
                $appInfo.data.NextVersionCheck | Should -BeGreaterThan ([DateTime]::Now.AddMinutes(1))
            }
        }
        It 'uses cached latest version when NextVersionCheck is in the future' {
            InModuleScope 'InstallHelpers' {
                Mock Invoke-WebRequest { }

                $latestVersion = $PSVersionTable.PSVersion.ToString()
                $appInfo = [ordered]@{
                    data = @{
                        LatestVersion    = $latestVersion
                        NextVersionCheck = [DateTime]::Now.AddMinutes(1) # future
                    }
                }

                isPowerShellUpToDate 'powershell' $appInfo | Should -BeTrue

                Should -Invoke Invoke-WebRequest -Times 0 -Exactly
            }
        }
        It 'return false when a newer version exists' {
            InModuleScope 'InstallHelpers' {
                $latestVersion = [version]::new($PSVersionTable.PSVersion.Major, $PSVersionTable.PSVersion.Minor + 1, 0).ToString()
                Mock Invoke-WebRequest {
                    [PSCustomObject]@{
                        StatusCode = 302
                        Headers    = @{ Location = @("https://github.com/PowerShell/PowerShell/releases/tag/v$latestVersion") }
                    }
                }

                $appInfo = [ordered]@{
                    data = @{
                        LatestVersion    = '1.0.0'
                        NextVersionCheck = [DateTime]::Now.AddMinutes(-1)
                    }
                }

                isPowerShellUpToDate 'powershell' $appInfo | Should -BeFalse

                Should -Invoke Invoke-WebRequest -Times 1 -Exactly
                $appInfo.data.LatestVersion | Should -BeExactly $latestVersion
                $appInfo.data.NextVersionCheck | Should -BeGreaterThan ([DateTime]::Now.AddMinutes(1))
            }
        }
        It 'uses cached latest version when web request fails' {
            InModuleScope 'InstallHelpers' {
                Mock Invoke-WebRequest {
                    Write-Error "Web request failed" -ErrorAction SilentlyContinue
                    [PSCustomObject]@{ StatusCode = 501 }
                }

                $latestVersion = $PSVersionTable.PSVersion.ToString()
                $appInfo = [ordered]@{
                    data = @{
                        LatestVersion    = $latestVersion
                        NextVersionCheck = [DateTime]::Now.AddMinutes(-1) # past
                    }
                }

                $WarningPreference = 'Ignore'
                $result = isPowerShellUpToDate 'powershell' $appInfo

                $result | Should -BeTrue
                Should -Invoke Invoke-WebRequest -Times 1 -Exactly
                $appInfo.data.LatestVersion | Should -BeExactly $latestVersion
                $appInfo.data.NextVersionCheck | Should -BeGreaterThan ([DateTime]::Now.AddMinutes(1))
            }
        }
        It 'uses cached latest version when web request does not return 302' {
            InModuleScope 'InstallHelpers' {
                Mock Invoke-WebRequest {
                    [PSCustomObject]@{ StatusCode = 200 }
                }

                $latestVersion = $PSVersionTable.PSVersion.ToString()
                $appInfo = [ordered]@{
                    data = @{
                        LatestVersion    = $latestVersion
                        NextVersionCheck = [DateTime]::Now.AddMinutes(-1) # past
                    }
                }

                $WarningPreference = 'Ignore'
                (& isPowerShellUpToDate 'powershell' $appInfo) | Should -BeTrue

                Should -Invoke Invoke-WebRequest -Times 1 -Exactly
                $appInfo.data.LatestVersion | Should -BeExactly $latestVersion
                $appInfo.data.NextVersionCheck | Should -BeGreaterThan ([DateTime]::Now.AddMinutes(1))
            }
        }
    }

    Context 'refreshEnvironment' {
        It 'refreshes the PATH variable' -Skip:(-not $IsWindows) {
            inModuleScope 'InstallHelpers' {
                $env:PATH += ";$TestDrive"

                refreshEnvironment

                $env:PATH -split ';' | Should -Not -Contain $TestDrive
            }
        }
    }

    Context 'Install-PowerShellModule' {
        It 'does not install modules that already satisfy minimum version' {
            Mock Get-Module -ModuleName InstallHelpers {
                [PSCustomObject]@{ Version = [version]'5.2.0' }
            }

            Mock Install-Module -ModuleName InstallHelpers { }

            Install-PowerShellModule -ModuleVersions @{ Pester = [version]'5.1.0' }

            Should -Invoke Install-Module -ModuleName InstallHelpers -Times 0 -Exactly
        }

        It 'installs modules when minimum version is missing' {
            $script:getModuleCallCount = 0

            Mock Get-Module -ModuleName InstallHelpers {
                $script:getModuleCallCount++
                if ($script:getModuleCallCount -ge 2) {
                    [PSCustomObject]@{ Version = [version]'5.1.0' }
                }
            }

            Mock Install-Module -ModuleName InstallHelpers { }

            Install-PowerShellModule -ModuleVersions @{ Pester = [version]'5.1.0' }

            Should -Invoke Install-Module -ModuleName InstallHelpers -Times 1 -Exactly
        }

        It 'writes an error when installation does not produce required version' {
            Mock Get-Module -ModuleName InstallHelpers { $null }
            Mock Install-Module -ModuleName InstallHelpers { }
            Mock Write-Error -ModuleName InstallHelpers { }

            Install-PowerShellModule -ModuleVersions @{ Pester = [version]'5.1.0' } -ErrorAction SilentlyContinue

            Should -Invoke Write-Error -ModuleName InstallHelpers -Times 1
        }
    }
}
