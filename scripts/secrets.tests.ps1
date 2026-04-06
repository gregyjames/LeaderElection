# SPDX-License-Identifier: Unlicense
# Source: http://github.com/mrfootoyou/pstaskframework
#Requires -Version 7.4

[Diagnostics.CodeAnalysis.SuppressMessage('PSAvoidUsingConvertToSecureStringWithPlainText', '', Justification = 'test code')]
param()

Set-StrictMode -Version Latest

Describe 'secrets.psm1' {
    BeforeEach {
        $modulePath = Join-Path $PSScriptRoot 'secrets.psm1'
        Import-Module $modulePath -Force

        $script:previousCi = $env:CI
        Remove-Item Env:\CI -ErrorAction Ignore
    }

    AfterEach {
        if ($null -eq $script:previousCi) {
            Remove-Item Env:\CI -ErrorAction Ignore
        }
        else {
            $env:CI = $script:previousCi
        }

        Remove-Module -Name secrets -ErrorAction Ignore
    }

    Context 'Protect-Secret' {
        It 'returns the input unchanged when no secrets are registered' {
            $result = Protect-Secret -Message 'hello world'

            $result | Should -BeExactly 'hello world'
        }

        It 'masks registered secrets with the default mask' {
            Push-Secret 'token123'

            $result = Protect-Secret -Message 'Authorization: token123'

            $result | Should -BeExactly 'Authorization: ****'
        }

        It 'uses a custom mask when provided' {
            Push-Secret 'token123'

            $result = Protect-Secret -Message 'Authorization: token123' -Mask '[REDACTED]'

            $result | Should -BeExactly 'Authorization: [REDACTED]'
        }

        It 'mask with $0 cannot be used to reveal part of the secret' {
            Push-Secret 'token123'

            $result = Protect-Secret -Message 'Authorization: token123' -Mask '$0'

            $result | Should -BeExactly 'Authorization: $0'
        }

        It 'supports pipeline input for message values' {
            Push-Secret 'top secret'

            $result = 'a top secret value' | Protect-Secret

            $result | Should -BeExactly 'a **** value'
        }

        It 'honors push and pop reference counting' {
            Push-Secret 'shared-secret'
            Push-Secret 'shared-secret'

            Pop-Secret 'shared-secret'
            $stillMasked = Protect-Secret -Message 'shared-secret'

            Pop-Secret 'shared-secret'
            $unmasked = Protect-Secret -Message 'shared-secret'

            $stillMasked | Should -BeExactly '****'
            $unmasked | Should -BeExactly 'shared-secret'
        }

        It 'rejects empty secret values' {
            { Push-Secret '' } | Should -Throw '*Cannot bind argument to parameter*'
        }

        It 'supports pushing and popping from the pipeline' {
            'pipelined-secret' | Push-Secret
            $masked = Protect-Secret -Message 'pipelined-secret'

            'pipelined-secret' | Pop-Secret
            $unmasked = Protect-Secret -Message 'pipelined-secret'

            $masked | Should -BeExactly '****'
            $unmasked | Should -BeExactly 'pipelined-secret'
        }
    }

    Context 'Read-Secret' {
        It 'returns empty string and warns in CI when AllowEmpty is set' {
            $env:CI = 'true'

            $warnings = @()
            $result = Read-Secret -Prompt 'Enter value' -AllowEmpty -WarningVariable warnings -WarningAction SilentlyContinue

            $result | Should -BeExactly ''
            ($warnings -join ' ') | Should -Match 'CI environment detected'
        }

        It 'writes an error in CI when AllowEmpty is not set' {
            $env:CI = 'true'

            { Read-Secret -Prompt 'Enter value' -ErrorAction Stop } | Should -Throw '*Cannot read input in CI environment.*'
        }

        It 'returns plain text from secure input outside CI' {
            Mock -CommandName Read-Host -ModuleName secrets -MockWith {
                ConvertTo-SecureString 'my-secret' -AsPlainText -Force
            }

            $result = Read-Secret -Prompt 'Enter value'

            $result | Should -BeExactly 'my-secret'
            Should -Invoke -CommandName Read-Host -ModuleName secrets -Times 1 -Exactly
        }

        It 'writes an error when no value is provided and AllowEmpty is not set' {
            Mock -CommandName Read-Host -ModuleName secrets -MockWith {
                [System.Security.SecureString]::new()
            }

            { Read-Secret -Prompt 'Enter value' -ErrorAction Stop } | Should -Throw '*No value provided.*'
        }

        It 'allows empty values when AllowEmpty is set outside CI' {
            Mock -CommandName Read-Host -ModuleName secrets -MockWith {
                [System.Security.SecureString]::new()
            }

            $result = Read-Secret -Prompt 'Enter value' -AllowEmpty

            $result | Should -BeExactly ''
        }
    }
}
