# SPDX-License-Identifier: Unlicense
# Source: http://github.com/mrfootoyou/pstaskframework
#Requires -Version 7.4
Set-StrictMode -Version Latest

Describe 'build-helpers.ps1' {
    BeforeAll {
        $scriptPath = Join-Path $PSScriptRoot 'build-helpers.ps1'
        . $scriptPath
    }

    BeforeEach {
        $global:LASTEXITCODE = 0
        $Error.Clear()

        Mock -CommandName 'Write-Host' -MockWith { } -Verbose:$false
    }

    Context 'Assert-AppExists' {
        It 'returns first discovered application source path with PassThru' {
            Mock -CommandName 'Get-Command' -MockWith {
                @(
                    [PSCustomObject]@{ Source = '/mock/bin/git' }
                    [PSCustomObject]@{ Source = '/mock/bin/git-secondary' }
                )
            }

            $result = Assert-AppExists -AppPath 'git' -PassThru

            $result | Should -Be '/mock/bin/git'
            Should -Invoke -CommandName 'Get-Command' -Times 1 -Exactly
        }

        It 'throws by default when app is missing' {
            Mock -CommandName 'Get-Command' -MockWith { $null }

            { Assert-AppExists -AppPath 'missing-app' } | Should -Throw '*missing-app*not found*'
        }

        It 'includes AppTitle in error message when app is missing' {
            Mock -CommandName 'Get-Command' -MockWith { $null }

            { Assert-AppExists -AppPath 'az' -AppTitle 'Azure CLI' } | Should -Throw '*Azure CLI (az) not found*'
        }

        It 'does not throw when ErrorAction Ignore is specified' {
            Mock -CommandName 'Get-Command' -MockWith { $null }

            $result = Assert-AppExists -AppPath 'missing-app' -ErrorAction Ignore

            $result | Should -BeNullOrEmpty
        }
    }

    Context 'Invoke-Shell' {
        It 'echoes command text and succeeds for zero exit code' {
            Mock -CommandName 'Assert-AppExists' -MockWith { 'pwsh' }

            Invoke-Shell -- pwsh -NoLogo -NoProfile -Command 'exit 0'

            $global:LASTEXITCODE | Should -Be 0
            Should -Invoke -CommandName 'Write-Host' -Times 1 -Exactly
        }

        It 'suppresses command echo when NoEcho is specified' {
            Mock -CommandName 'Assert-AppExists' -MockWith { 'pwsh' }

            Invoke-Shell -NoEcho -- pwsh -NoLogo -NoProfile -Command 'exit 0'

            Should -Invoke -CommandName 'Write-Host' -Times 0 -Exactly
        }

        It 'throws on non-zero exit code by default' {
            Mock -CommandName 'Assert-AppExists' -MockWith { 'pwsh' }

            { Invoke-Shell -ErrorAction Stop -- pwsh -NoLogo -NoProfile -Command 'exit 5' } | Should -Throw '*exit code 5*'
            $global:LASTEXITCODE | Should -Be 5
        }

        It 'accepts configured non-zero exit codes' {
            Mock -CommandName 'Assert-AppExists' -MockWith { 'pwsh' }

            Invoke-Shell -AllowedExitCodes @(0, 5) -- pwsh -NoLogo -NoProfile -Command 'exit 5'

            $global:LASTEXITCODE | Should -Be 5
        }
    }

    Context 'Test-Administrator' {
        It 'returns a boolean value on Windows' -Skip:(-not $IsWindows) {
            $result = Test-Administrator

            $result | Should -BeOfType ([bool])
        }

        It 'returns true for root uid on non-Windows' -Skip:$IsWindows {
            Mock -CommandName 'id' -ParameterFilter { $u -eq '-u' } -MockWith { '0' }

            Test-Administrator | Should -BeTrue
        }

        It 'returns false for non-root uid on non-Windows' -Skip:$IsWindows {
            Mock -CommandName 'id' -ParameterFilter { $u -eq '-u' } -MockWith { '1000' }

            Test-Administrator | Should -BeFalse
        }
    }
}
