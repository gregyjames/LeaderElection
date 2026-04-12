# SPDX-License-Identifier: Unlicense
# Source: http://github.com/mrfootoyou/pstaskframework
#Requires -Version 7.4

[Diagnostics.CodeAnalysis.SuppressMessage('PSAvoidUsingConvertToSecureStringWithPlainText', '', Justification = 'test code')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '', Justification = 'Chokes on Pester keywords.')]
param()

Describe 'PSTaskFramework.Secrets Module' {
    BeforeAll {
        Import-Module "$PSScriptRoot\Secrets" -Scope Local -ArgumentList Local -Verbose:$false

        # Use a private secret store for testing to avoid interference with
        # any global secrets.
        $state = @{
            secrets = [PSCustomObject]@{
                values = @{}
                regex  = $null
            }
            isCI    = $false
        }
        Mock getState -ModuleName Secrets { $state.secrets }

        # Mock isContinuousIntegration to return false by default
        Mock isContinuousIntegration -ModuleName Secrets { $state.isCI }
    }

    AfterEach {
        # Reset state after each test to avoid cross-test contamination
        $state.secrets.values.Clear()
        $state.secrets.regex = $null
        $state.isCI = $false
    }

    Context 'Push-Secret' {
        It 'reference counts pushed secrets' {
            Push-Secret 'top secret'
            Push-Secret 'top secret'
            Push-Secret 'top secret'

            $state.secrets.values['top secret'] | Should -Be 3
        }

        It 'rejects empty secret values' {
            { Push-Secret '' } | Should -Throw '*Cannot bind argument to parameter*'
            { Push-Secret $null } | Should -Throw '*Cannot bind argument to parameter*'
        }
    }

    Context 'Pop-Secret' {
        It 'reference counts popped secrets' {
            Push-Secret 'top secret'
            Push-Secret 'top secret'
            Pop-Secret 'top secret'

            $state.secrets.values['top secret'] | Should -Be 1
        }

        It 'is okay when popping a secret that was never pushed' {
            { Pop-Secret 'nonexistent-secret' } | Should -Not -Throw
        }

        It 'rejects empty secret values' {
            { Pop-Secret '' } | Should -Throw '*Cannot bind argument to parameter*'
            { Pop-Secret $null } | Should -Throw '*Cannot bind argument to parameter*'
        }
    }

    Context 'Protect-Secret' {
        It 'returns the input unchanged when no secrets are registered' {
            $result = Protect-Secret -Message 'hello world'

            $result | Should -BeExactly 'hello world'
            $state.secrets.regex | Should -BeNull
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
        BeforeAll {
            # Mock Read-Host to avoid hanging tests
            Mock Read-Host -ModuleName Secrets {
                throw "Read-Host called unexpectedly."
            }
        }

        It 'returns plain text from secure input' {
            Mock Read-Host -ModuleName Secrets {
                ConvertTo-SecureString 'my-secret' -AsPlainText -Force
            }

            $result = Read-Secret -Prompt 'Enter value'

            $result | Should -BeExactly 'my-secret'
            Should -Invoke -CommandName Read-Host -ModuleName Secrets `
                -ParameterFilter { $Prompt -eq 'Enter value' -and $AsSecureString } `
                -Times 1 -Exactly
        }

        It 'writes an error when no value is provided and AllowEmpty is not set' {
            Mock Read-Host -ModuleName Secrets {
                [System.Security.SecureString]::new()
            }

            { Read-Secret -Prompt 'Enter value' -ErrorAction Stop } | `
                Should -Throw '*No value provided.*'
        }

        It 'returns empty string and warns in CI when AllowEmpty is set' {
            $state.isCI = $true

            $result = Read-Secret -Prompt 'Enter value' -AllowEmpty `
                -WarningAction SilentlyContinue -WarningVariable warnings

            $result | Should -BeExactly ''
            ($warnings -join ' ') | Should -Match 'CI environment detected'
        }

        It 'writes an error in CI when AllowEmpty is not set' {
            $state.isCI = $true

            { Read-Secret -Prompt 'Enter value' -ErrorAction Stop } | `
                Should -Throw '*Cannot read input in CI environment.*'
        }

        It 'allows empty values when AllowEmpty is set' {
            Mock Read-Host -ModuleName Secrets {
                [System.Security.SecureString]::new()
            }

            $result = Read-Secret -Prompt 'Enter value' -AllowEmpty

            $result | Should -BeExactly ''
        }
    }

    Context 'Clear-SecretStore' {
        It 'clears all secrets from the store' {
            Push-Secret 'secret1'
            Push-Secret 'secret2'
            Push-Secret 'secret1'
            $null = Protect-Secret -Message 'secret1 and secret2 are here'

            Clear-SecretStore

            $state.secrets.values.Count | Should -Be 0
            $state.secrets.regex | Should -BeNull
        }
    }
}
