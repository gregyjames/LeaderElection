# SPDX-License-Identifier: Unlicense
# Source: http://github.com/mrfootoyou/pstaskframework
# spell:ignore psargs,targs
#Requires -Version 7.4

param()

Import-Module "$PSScriptRoot/psargs.psm1" -Verbose:$false

class TaskDefinition {
    [string]$Name
    [string]$Description
    [string[]]$DependsOn
    [ScriptBlock]$Action
    [int[]]$AllowedExitCodes = @(0)

    [string] ToString() { return $this.Name }

    static hidden [System.Collections.Generic.OrderedDictionary[string, TaskDefinition]] $AllTasks = [System.Collections.Generic.OrderedDictionary[string, TaskDefinition]]::new([StringComparer]::OrdinalIgnoreCase)
    static hidden [bool] $TasksSorted = $true

    static [void] Clear() {
        [TaskDefinition]::AllTasks.Clear()
        [TaskDefinition]::TasksSorted = $true
    }

    static [void] AddTask([TaskDefinition]$task) {
        if ([TaskDefinition]::AllTasks.ContainsKey($task.Name)) {
            throw "A task with the name '$($task.Name)' already exists."
        }
        [TaskDefinition]::AllTasks[$task.Name] = $task
        [TaskDefinition]::TasksSorted = $false
    }

    # Returns an ordered array of TaskDefinition objects corresponding to the specified task
    # names and their dependencies (if $includeDependencies is specified). The tasks are returned
    # in dependency order. For example, if taskA depends on taskB, then GetOrderedTasks('taskA', $true)
    # will return an array with taskB first, followed by taskA.
    static [TaskDefinition[]] GetOrderedTasks([string[]]$taskNames, [switch]$includeDependencies) {
        # get all tasks in dependency order...
        $orderedTaskMap = [TaskDefinition]::GetOrderedTasks()

        # get the set of all tasks to execute, including dependencies if specified.
        $execTaskNames = @{}
        $queue = [System.Collections.Generic.Queue[string]]::new($taskNames)
        while ($queue.Count -gt 0) {
            $taskName = $queue.Dequeue()
            if ($execTaskNames.ContainsKey($taskName)) {
                continue # already visited
            }
            $execTaskNames[$taskName] = $true
            $task = $orderedTaskMap[$taskName]
            if (-not $task) {
                throw "Task '$taskName' not found."
            }
            if ($includeDependencies) {
                foreach ($dep in $task.DependsOn) {
                    $queue.Enqueue($dep)
                }
            }
        }

        # Return the tasks in dependency order...
        return @(
            foreach ($task in $orderedTaskMap.Values) {
                if ($execTaskNames.ContainsKey($task.Name)) {
                    $task
                }
            }
        )
    }

    # Returns an ordered dictionary of all defined tasks, sorted in dependency order.
    # The keys are task names and the values are TaskDefinition objects.
    static [System.Collections.Generic.OrderedDictionary[string, TaskDefinition]] GetOrderedTasks() {
        if ([TaskDefinition]::TasksSorted) {
            return [TaskDefinition]::AllTasks
        }

        # Sort tasks in dependency order using a depth-first search.
        # Preserve the original order of tasks as much as possible while ensuring that
        # dependencies are always defined before the tasks that depend on them.
        # This also detects circular dependencies.
        $visited = @{}
        function visit([TaskDefinition]$task) {
            if ($visited.ContainsKey($task.Name)) {
                # already visited this node; if we're visiting it again, we have a
                # circular dependency
                if ($visited[$task.Name] -eq 'visiting') {
                    throw "Circular dependency detected at task '$($task.Name)'."
                }
                return
            }
            $visited[$task.Name] = 'visiting'
            foreach ($dep in $task.DependsOn) {
                $depTask = [TaskDefinition]::AllTasks[$dep]
                if (-not $depTask) {
                    throw "Dependency '$dep' of task '$($task.Name)' not found."
                }
                visit $depTask
            }
            $visited[$task.Name] = ''
            $task
        }

        $orderedTasks = @(
            foreach ($task in [TaskDefinition]::AllTasks.Values) {
                visit $task
            }
        )

        [TaskDefinition]::AllTasks.Clear()
        foreach ($task in $orderedTasks) {
            [TaskDefinition]::AllTasks[$task.Name] = $task
        }
        [TaskDefinition]::TasksSorted = $true
        return [TaskDefinition]::AllTasks
    }
}

function Reset-TaskFramework {
    <#
    .DESCRIPTION
        Resets the state of the task framework by clearing all defined tasks. This can be useful
        to ensure a clean slate when invoking multiple tasks or when reloading the task framework.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessage('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Resetting the task framework is a state change, but it is not something that users would typically want to confirm.')]
    param()
    [TaskDefinition]::Clear()
}

function Add-TaskFrameworkTask {
    <#
    .DESCRIPTION
        Adds a task with an associated action and optional task dependencies to the
        task framework.
    #>
    [CmdletBinding(PositionalBinding = $false)]
    param (
        # The name of the task. Must be unique. Not case-sensitive.
        [Parameter(Mandatory, Position = 0)]
        [string]$Name,

        # The script block to execute when the task is invoked. May be null or empty for
        # tasks that only serve as a grouping of dependencies.
        [Parameter(Mandatory, Position = 1)]
        [AllowNull()]
        [ScriptBlock]$Action,

        # A brief description of the task.
        [string]$Description,

        # An array of task names that this task depends on. These tasks will be executed
        # before this task unless -SkipDependencies is specified when invoking.
        [string[]]$DependsOn = @(),

        # An array of allowed exit codes for the task. If the task completes with an
        # exit code that is not in this array, it will be considered a failure. Pass an
        # empty array to ignore the exit code. Defaults to 0.
        [int[]]$AllowedExitCodes = @(0)
    )
    [TaskDefinition]::AddTask(@{
            Name             = $Name
            Description      = $Description
            DependsOn        = $DependsOn
            Action           = $Action
            AllowedExitCodes = $AllowedExitCodes
        })
}

function Get-TaskFrameworkTasks {
    <#
    .DESCRIPTION
        Gets all tasks defined in the task framework in dependency order. The returned
        objects are of type TaskDefinition, which has the following properties:
            - Name: The name of the task.
            - Description: A brief description of the task.
            - DependsOn: An array of task names that this task depends on.
            - Action: The script block to execute when the task is invoked.

        This can be useful for listing available tasks or for debugging task definitions.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessage('PSUseSingularNouns', '', Justification = 'Tasks is plural because it manages multiple tasks.')]
    param()
    return [TaskDefinition]::GetOrderedTasks().Values
}

function Repair-TaskStackTrace {
    <#
    .DESCRIPTION
        Fixes the stack trace of an error that occurs when invoking a task using
        Invoke-Expression.

        The invocation looks like this:
            Invoke-Expression -Command "&{`n<task action body>`n} <task args>"

        The call stack will look something like this:
            ...
            at <ScriptBlock>, <No file>: line 5
            at <ScriptBlock>, <No file>: line 1
            at Invoke-Task, F:\repo\scripts\task-framework.psm1: line 264
            at Invoke-TaskFramework, F:\repo\scripts\task-framework.psm1: line 346
            at <ScriptBlock>, F:\repo\build.ps1: line 198
            at <ScriptBlock>, <No file>: line 1

        The "<No file>" stack frames above "Invoke-Task" are from Invoke-Expression.
        The frame at line 1 is the script block invocation (&{...}) used to pass
        parameters into the task action. This should be ignored since it would not
        appear in a normal stack trace.
        The next one (at line 5) is an actual task frame. We will replace it
        with the filename and line number of the task action.
    #>
    param(
        [System.Management.Automation.ErrorRecord]$ErrorRecord,
        [TaskDefinition]$Task,
        # The line where the task action starts within the Invoke-Expression command.
        [int]$TaskActionStartLine = 2
    )

    # Unfortunately, we have to use reflection to set the 'StackTrace'. This may
    # break in future versions of PowerShell so we proceed with caution.
    # See https://github.com/PowerShell/PowerShell for the ErrorRecord definition.
    $bindingFlags = [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::NonPublic
    $field = $ErrorRecord.GetType().GetField("_scriptStackTrace", $bindingFlags)
    if (!$field) {
        Write-Verbose "Unable to fix task stacktrace: could not find '_scriptStackTrace' field via reflection."
        return
    }

    $taskFile = $Task.Action.Ast.Extent.File ?? '<No file>'
    $taskLineNumber = $Task.Action.Ast.Extent.StartLineNumber

    # split and reverse the stack frames to simplify things
    $frames = $ErrorRecord.ScriptStackTrace -split '\r?\n'
    [array]::Reverse($frames)

    # use a state machine to rewrite the frames. Remember we reversed the frames so we're going bottom-up.
    $state = 'beforeInvokeTaskFrame'
    $fixedFrames = foreach ($frame in $frames) {
        switch ($state) {
            'beforeInvokeTaskFrame' {
                $frame
                if ($frame.StartsWith('at Invoke-Task,')) { $state = 'afterInvokeTaskFrame' }
            }
            'afterInvokeTaskFrame' {
                # ignore frame at line 1
                if ($frame -eq 'at <ScriptBlock>, <No file>: line 1') {
                    $state = 'afterFrameAtLine1'
                }
                else {
                    # should not happen
                    $frame
                    $state = 'beforeInvokeTaskFrame'
                }
            }
            'afterFrameAtLine1' {
                if ($frame -match '^at <ScriptBlock>, <No file>: line (\d+)') {
                    "at <ScriptBlock>, ${taskFile}: line $($taskLineNumber + $Matches[1] - $TaskActionStartLine)"
                }
                else { $frame; $state = 'afterInvokeExpression'; }
            }
            'afterInvokeExpression' { $frame }
        }
    }

    [array]::Reverse($fixedFrames)
    $fixedStackTrace = $fixedFrames -join [System.Environment]::NewLine
    $field.SetValue($ErrorRecord, $fixedStackTrace)
}

function Invoke-Task {
    <#
    .DESCRIPTION
        Invokes a task defined in the task framework.

        This is typically not called directly; use Invoke-TaskFramework instead.

        The function will import the specified scripts and variables into the scope
        of the invoked task, allowing them to be used in the task action. Mutable variables,
        such as HashTables or lists, can be used to share state across tasks, but be cautious
        of potential side effects.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessage('PSAvoidUsingInvokeExpression', '', Justification = 'Using Invoke-Expression is necessary to allow task actions to accept named arguments.')]
    param(
        [TaskDefinition]$Task,
        [object[]]$TaskArgs,
        [hashtable]$Variables,
        [string[]]$ImportScripts
    )

    $ImportScripts.foreach{
        Write-Verbose "Importing script '$_'."
        . $_
    }

    $Variables.Keys.foreach{
        Write-Verbose "Importing variable '$_' with value $(ConvertTo-PSString $Variables[$_] -UseQuotes)."
        Set-Variable -Name $_ -Value $Variables[$_] -Scope Local -Force -ea Ignore
    }

    $TaskName = $Task.Name

    $private:_taskCommandArgs = ConvertTo-CommandArgs $TaskArgs
    if ($_taskCommandArgs) {
        Write-Verbose "Invoking task '$TaskName' with arguments: $_taskCommandArgs"
    }
    else {
        Write-Verbose "Invoking task '$TaskName' with no arguments."
    }

    $global:LASTEXITCODE = 0

    try {
        # Use Invoke-Expression to invoke the script block with arguments. This enables
        # $TaskArgs to contain named parameters (i.e. '-foo','bar') not just positional
        # parameters ('bar').
        Invoke-Expression -Command "&{`n$($Task.Action)`n} $_taskCommandArgs"
    }
    catch {
        # Since we used Invoke-Expression to execute the task's action, the stack trace will
        # not contain the action's actual filename and line number. Let's fixup the stack
        # trace to include the task action's filename and line number...
        Repair-TaskStackTrace -ErrorRecord $_ -Task $Task -TaskActionStartLine 2
        throw $_
    }

    if ($Task.AllowedExitCodes.Count -gt 0 -and $global:LASTEXITCODE -notin $Task.AllowedExitCodes) {
        Write-Verbose "Task '$TaskName' failed with exit code $global:LASTEXITCODE."
        throw "Task '$TaskName' failed with exit code $global:LASTEXITCODE."
    }
    Write-Verbose "Completed task '$TaskName' with exit code $global:LASTEXITCODE."
    $global:LASTEXITCODE = 0 # reset to avoid affecting the final exit code
}

function Invoke-TaskFramework {
    <#
    .DESCRIPTION
        Invokes one or more tasks defined in the task framework.

        Tasks will be executed in the order they were defined. If a task has dependencies,
        those will be executed first unless $SkipDependencies is specified.

        The function will import the specified scripts and variables into the scope
        of each invoked task, allowing them to be used in task actions. Mutable variables,
        such as HashTables or lists, can be used to share state across tasks, but be cautious
        of potential side effects.
    #>
    [CmdletBinding(PositionalBinding = $false)]
    param(
        # The working directory in which to invoke the task(s).
        [Parameter(Mandatory)]
        [string]$WorkingDirectory,

        # The name(s) of the task(s) to invoke.
        [Parameter(Mandatory)]
        [string[]]$TaskName,

        # Task-specific arguments. Can only be used when invoking a _single_ task.
        [object[]]$TaskArgs = @(),

        # Indicates whether to skip invoking dependencies of specified tasks. Defaults to $false.
        [switch]$SkipDependencies,

        # A list of scripts to import into the scope of invoked tasks. This can be used to share
        # helper functions across tasks.
        [string[]]$ImportScripts = @(),

        # A hashtable of variables to import into the scope of invoked tasks. This can be used to
        # pass configuration or state to tasks.
        [hashtable]$Variables = @{},

        # Indicates that the function should exit the script if a failure occurs. If not specified,
        # the function will throw an exception failure.
        [switch]$ExitOnError
    )
    $ErrorActionPreference = 'Stop'

    Write-Verbose "Working directory: '$WorkingDirectory'."
    Push-Location $WorkingDirectory
    try {
        if ($TaskArgs.Count -gt 0 -and $TaskName.Count -gt 1) {
            throw 'Task arguments cannot be used when invoking multiple tasks.'
        }

        $TasksToExecute = [TaskDefinition]::GetOrderedTasks($TaskName, !$SkipDependencies)

        $private:targs = @{
            Task          = $null
            TaskArgs      = @()
            ImportScripts = $ImportScripts
            Variables     = $Variables
        }

        Write-Verbose "Executing tasks: $($TasksToExecute.Name -join ', ')"

        foreach ($task in $TasksToExecute) {
            $targs.Task = $task
            $targs.TaskArgs = $task.Name -eq $TaskName ? $TaskArgs : @()
            Invoke-Task @targs
            Write-Host ''
        }

        Write-Verbose "Done executing tasks."
    }
    catch {
        if ($global:LASTEXITCODE -eq 0) { $global:LASTEXITCODE = -1 }
        Write-Verbose "Error: $_`n$(($_ | Format-List -Force | Out-String).Trim())"
        if ($ExitOnError) {
            # Output a user-friendly error message with a stack trace
            Write-Host "Error: $_`n$($_.Exception.GetType().FullName)`n$($_.ScriptStackTrace)" -ForegroundColor Red
            Write-Verbose "Exiting with code $global:LASTEXITCODE."
            exit $global:LASTEXITCODE
        }
        throw
    }
    finally {
        Pop-Location
    }
}

# reset the task framework state when the module is [force] imported to ensure a clean slate
Reset-TaskFramework

New-Alias -Name Task -Value Add-TaskFrameworkTask -Force

Export-ModuleMember -Function Reset-TaskFramework, Add-TaskFrameworkTask, Get-TaskFrameworkTasks, Invoke-TaskFramework -Alias Task
