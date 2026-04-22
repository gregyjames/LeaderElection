<#
.DESCRIPTION
    Unit tests for PSTaskFramework module.
.NOTES
    SPDX-License-Identifier: Unlicense
    Source: http://github.com/mrfootoyou/pstaskframework
#>
#Requires -Version 7.4

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '', Justification = 'Chokes on Pester keywords.')]
param()

Describe 'PSTaskFramework Module' {
    BeforeAll {
        Import-Module "$PSScriptRoot/PSTaskFramework" -Scope Local -Verbose:$false
        Reset-TaskFramework
    }

    AfterEach {
        Reset-TaskFramework
    }

    It 'fails when no tasks are specified' {
        { Invoke-TaskFramework -WorkingDirectory $TestDrive -TaskName 'foo' } | Should -Throw "Task 'foo' not found."
        $global:LASTEXITCODE | Should -Be -1
    }

    It 'registers and executes tasks via Task command' {
        $shared = [ordered]@{ State = [System.Collections.Generic.List[string]]::new() }

        Task 'alpha' -Description 'first task' -Action { $Shared.State.Add('alpha') } -DependsOn @('beta')
        Task 'beta' -Description 'second task' -Action { $Shared.State.Add('beta') }

        Invoke-TaskFramework -WorkingDirectory $TestDrive -TaskName @('alpha') -Variables @{ Shared = $shared }

        $shared.State | Should -Be @('beta', 'alpha')
    }

    It 'rejects duplicate task names case-insensitively' {
        Task -Name 'Build' -Action {}

        { Task -Name 'build' -Action {} } | Should -Throw '*already exists*'
    }

    It 'executes dependencies before task by default' {
        $shared = [ordered]@{ State = [System.Collections.Generic.List[string]]::new() }

        Task 'dep' { $Shared.State.Add('dep') }
        Task 'main' { $Shared.State.Add('main') } -DependsOn @('dep')

        Invoke-TaskFramework -WorkingDirectory $TestDrive -TaskName @('main') -Variables @{ Shared = $shared }

        $shared.State | Should -Be @('dep', 'main')
    }

    It 'skips dependencies when SkipDependencies is specified' {
        $shared = [ordered]@{ State = [System.Collections.Generic.List[string]]::new() }

        Task 'dep' { $Shared.State.Add('dep') }
        Task 'main' { $Shared.State.Add('main') } -DependsOn @('dep')

        Invoke-TaskFramework -WorkingDirectory $TestDrive -TaskName @('main') -SkipDependencies -Variables @{ Shared = $shared }

        $shared.State | Should -Be @('main')
    }

    It 'passes TaskArgs to a single task' {
        $shared = [ordered]@{ Captured = '' }

        Task 'echo' {
            param([string]$Name, [int]$Count)
            $Shared.Captured = ("{0}:{1}" -f $Name, $Count)
        }

        Invoke-TaskFramework -WorkingDirectory $TestDrive -TaskName @('echo') -TaskArgs @('sample', 3) -Variables @{ Shared = $shared }

        $shared.Captured | Should -Be 'sample:3'
    }

    It 'marks invocation as failed when TaskArgs are used with multiple tasks' {
        Task 'first' {}
        Task 'second' {}

        { Invoke-TaskFramework -WorkingDirectory $TestDrive -TaskName @('first', 'second') -TaskArgs @('x') } |
        Should -Throw '*Task arguments cannot be used when invoking multiple tasks.*'

        $global:LASTEXITCODE | Should -Be -1
    }

    It 'imports variables into task scope' {
        $shared = [ordered]@{ Result = '' }

        Task 'use-vars' {
            $Shared.Result = "$Greeting, $Name"
        }

        Invoke-TaskFramework -WorkingDirectory $TestDrive -TaskName @('use-vars') -Variables @{
            Greeting = 'hello'
            Name     = 'world'
            Shared   = $shared
        }

        $shared.Result | Should -Be 'hello, world'
    }

    It 'imports helper scripts before task invocation' {
        $shared = [ordered]@{ Result = '' }
        $helperPath = Join-Path $TestDrive 'helper.ps1'
        Set-Content -Path $helperPath -Value @'
function Get-Message {
    'from helper'
}
'@

        Task 'use-helper' {
            $Shared.Result = Get-Message
        }

        Invoke-TaskFramework -WorkingDirectory $TestDrive -TaskName @('use-helper') -ImportScripts @($helperPath) -Variables @{ Shared = $shared }

        $shared.Result | Should -Be 'from helper'
    }

    It 'preserves non-zero task exit code on failure' {
        Task 'fails' {
            $global:LASTEXITCODE = 42
        }

        { Invoke-TaskFramework -WorkingDirectory $TestDrive -TaskName 'fails' } |
        Should -Throw "Task 'fails' failed with exit code 42."

        $global:LASTEXITCODE | Should -Be 42
    }

    It 'ignores non-zero AllowedExitCodes' {
        Task 'fails' -AllowedExitCodes @('42') {
            $global:LASTEXITCODE = 42
        }

        Invoke-TaskFramework -WorkingDirectory $TestDrive -TaskName 'fails'
        $global:LASTEXITCODE | Should -Be 0
    }

    It 'ignores exit code when no AllowedExitCodes' {
        Task 'fails' -AllowedExitCodes @() {
            $global:LASTEXITCODE = 42
        }

        Invoke-TaskFramework -WorkingDirectory $TestDrive -TaskName 'fails'
        $global:LASTEXITCODE | Should -Be 0
    }

    It 'does not reset framework state after invocation' {
        Task 'run-once' {}

        Invoke-TaskFramework -WorkingDirectory $TestDrive -TaskName @('run-once')
        Invoke-TaskFramework -WorkingDirectory $TestDrive -TaskName @('run-once')

        $global:LASTEXITCODE | Should -Be 0
    }

    It 'fails when dependency is missing' {
        Task 'main' {} -DependsOn @('missing')

        { Invoke-TaskFramework -WorkingDirectory $TestDrive -TaskName @('main') } | Should -Throw "Dependency 'missing' of task 'main' not found."

        $global:LASTEXITCODE | Should -Be -1
    }

    It 'fails when dependencies are circular' {
        Task 'a' {} -DependsOn @('b')
        Task 'b' {} -DependsOn @('a')

        { Invoke-TaskFramework -WorkingDirectory $TestDrive -TaskName @('a') } | Should -Throw "Circular dependency detected at *"

        $global:LASTEXITCODE | Should -Be -1
    }
}
