# SPDX-License-Identifier: Unlicense
# Source: http://github.com/mrfootoyou/pstaskframework
# spell:ignore pscustomobject
#Requires -Version 7.4

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '', Justification = 'Chokes on Pester keywords.')]
param()

Describe 'PSTaskFramework.PSArgs Module' {
    BeforeAll {
        Import-Module "$PSScriptRoot/PSArgs" -Verbose:$false
    }

    Context 'ConvertTo-PSString' {
        It 'converts null to a null literal' {
            $result = $null | ConvertTo-PSString

            $result | Should -Be '$null'
        }

        It 'returns unquoted safe strings by default' {
            $result = 'simpleValue' | ConvertTo-PSString

            $result | Should -BeExactly 'simpleValue'
        }

        It 'quotes strings that contain spaces' {
            $result = 'hello world' | ConvertTo-PSString

            $result | Should -BeExactly "'hello world'"
        }

        It 'escapes single quotes inside strings' {
            $result = "A'hoy" | ConvertTo-PSString

            $result | Should -BeExactly "'A''hoy'"
        }

        It 'uses double-quoted escaped form for control characters' {
            $result = "line1`n line2" | ConvertTo-PSString

            $result | Should -BeExactly '"line1`n line2"'
        }

        It 'forces quotes when UseQuotes is set' {
            $result = 'simpleValue' | ConvertTo-PSString -UseQuotes

            $result | Should -BeExactly "'simpleValue'"
        }

        It 'converts booleans to PowerShell literals' {
            ($true | ConvertTo-PSString) | Should -BeExactly '$True'
            ($false | ConvertTo-PSString) | Should -BeExactly '$False'
        }

        It 'converts numeric values without quotes' {
            (123 | ConvertTo-PSString) | Should -BeExactly '123'
            (12.5 | ConvertTo-PSString) | Should -BeExactly '12.5'
        }

        It 'converts collections to array literals' {
            $result = ConvertTo-PSString -InputObject @('alpha', 'beta value')

            $result | Should -BeExactly "@('alpha','beta value')"
        }

        It 'converts hashtable to hashtable literals' {
            $result = @{ Name = 'test value' } | ConvertTo-PSString

            $result | Should -BeExactly "@{Name = 'test value'}"
        }

        It 'converts ordered dictionaries to ordered dictionary literals' {
            $ht = [ordered]@{ Name = 'test value'; Count = 2 }
            $result = $ht | ConvertTo-PSString

            $result | Should -BeExactly "([ordered]@{Name = 'test value';Count = 2})"
        }

        It 'converts PSCustomObject to pscustomobject literals' {
            $result = ([pscustomobject]@{ Name = 'n'; Count = 2 }) | ConvertTo-PSString

            $result | Should -BeExactly "([pscustomobject]@{Name = 'n';Count = 2})"
        }

        It 'converts script blocks into brace-wrapped text' {
            $result = { Get-Date } | ConvertTo-PSString

            $result | Should -BeExactly '{ Get-Date }'
        }
    }

    Context 'ConvertTo-CommandArg' {
        It 'returns an empty string for null input' {
            $result = $null | ConvertTo-CommandArg

            $result | Should -BeExactly ''
        }

        It 'converts arrays into space-separated arguments' {
            $result = ConvertTo-CommandArg -InputObject @('alpha', 'beta value', 42)

            $result | Should -BeExactly "alpha 'beta value' 42"
        }

        It 'converts dictionaries into named arguments' {
            $result = ([ordered]@{ Name = 'value with space'; Count = 2 }) | ConvertTo-CommandArg

            $result | Should -BeExactly "-Name:'value with space' -Count:2"
        }

        It 'converts PSCustomObject into named arguments' {
            $result = ([pscustomobject]@{ Name = 'value with space'; Enabled = $true }) | ConvertTo-CommandArg

            $result | Should -BeExactly "-Name:'value with space' -Enabled:`$True"
        }

        It 'converts single scalar values to argument strings' {
            $result = 'value with space' | ConvertTo-CommandArg

            $result | Should -BeExactly "'value with space'"
        }
    }
}
