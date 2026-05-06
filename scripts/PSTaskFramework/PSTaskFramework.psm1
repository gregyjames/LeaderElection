<#
.DESCRIPTION
    Task management helpers for PowerShell.

    Q: Why is the PSTaskFramework implemented as a PowerShell module?
    A: It ensures that tasks defined in the user's build script have access to all variables
       and functions in the build script.

       When a script block (SB) is invoked, PowerShell uses the SB's Module property to determine
       its execution context (session state and scope):
       - If the Module property is the current module (or is unset), then the script block will
         be executed in the current context (in a new child scope when using the call operator).
       - Otherwise the script block will be executed using the session state of the module it was
         defined in. The exact scope within the session depends on the call stack: PowerShell
         will search the call stack for the nearest (most recent) stack frame belonging to the
         SB's module and will execute the SB in a child scope of that frame. If a matching frame
         is not found, the nearest global-scope frame is used.
       This has a few critically important implications:
       1. SBs _defined_ in other modules will be executed in the context (and nearest scope!)
          of the module/script they were defined in. For external tasks, that will be the scope
          where the `Invoke-TaskFramework` function is called (usually script scope), thus they
          will have access to all variables and functions defined in their script, including
          `$TaskContext` returned by `Initialize-TaskFramework`.
       2. SBs _defined_ in this module do have access to this module's session state. Thus tasks
          defined in this module will have access to all stack variables going back to the
          `Invoke-TaskFramework` invocation, including `$TaskContext`.
       3. SBs without a module session (e.g. created via `[scriptblock]::Create("...")`) will be
          executed as if they were defined in this module (see above).

.NOTES
    SPDX-License-Identifier: Unlicense
    Source: http://github.com/mrfootoyou/pstaskframework
#>
#Requires -Version 7.4
# spell:ignore maml

param()

class TaskContext {
    # A hashtable for storing arbitrary information.
    [hashtable]$State = @{}

    # Details about the build script.
    [System.IO.FileInfo]$BuildScriptPath
    [string]$TaskNameArgName = 'TaskName'
    [string]$TaskArgsArgName = 'TaskArgs'

    # All defined tasks. The keys are task names.
    [ordered]$AllTasks = [ordered]@{}
    [bool]$AllTasksSorted = $true
    [string]$HelpTaskName

    # Execution metadata for the current run.
    [System.IO.DirectoryInfo]$WorkingDirectory
    [string[]]$TasksToExecute = @()
    [bool]$SkipDependencies
    [Nullable[DateTime]]$Start
    [Nullable[TimeSpan]]$Duration
    [Nullable[int]]$ExitCode
    [System.Management.Automation.ErrorRecord]$Error

    # Task execution metadata. This is updated as tasks are executed.
    [TaskDefinition]$CurrentTask
    [ordered]$Results = [ordered]@{}
}

# The definition of a task. Store the task metadata and action.
class TaskDefinition {
    [string]$Name
    [string]$Description = ''
    [string[]]$DependsOn = @()
    [int[]]$AllowedExitCodes = @(0)
    [ScriptBlock]$Action

    [string] ToString() { return $this.Name }
}

# The result of a task execution. Stores the execution metadata and result.
class TaskResult {
    [DateTime]$Start = [DateTime]::UtcNow
    [TaskArgs]$TaskArgs
    [Nullable[TimeSpan]]$Duration
    [Nullable[int]]$ExitCode
    [System.Management.Automation.ErrorRecord]$Error
}

# The arguments passed to a task. Stores the raw, bound, and unbound arguments.
class TaskArgs {
    [object[]]$Raw = @()
    [System.Collections.IDictionary]$Bound = @{}
    [object[]]$Unbound = @()
}

# The name of the external variable containing the current TaskContext object.
# This is set when calling Initialize-TaskFramework and is used by Get-TaskFrameworkContext
# to retrieve the callers TaskContext when it is not passed explicitly.
# This is, unfortunately, global data, but should be okay as long as the callers always use
# the same variable name, or do not interleave runs (it's hard to imagine what that would
# even look like).
$script:ExternalTaskContextVariableName = 'TaskContext'

function Initialize-TaskFramework {
    <#
    .DESCRIPTION
        Initializes the task framework by creating a new TaskContext object.

        !IMPORTANT! The caller must save the returned object to a variable named `TaskContext`.
        If this is not possible for some reason, then the replacement name must be specified
        using the -TaskContextVariableName parameter.
    #>
    [CmdletBinding(PositionalBinding = $false)]
    [OutputType([TaskContext])]
    param(
        # The path to the callers build script.
        # Defaults to caller's `$MyInvocation.MyCommand.Path`.
        [ValidateNotNullOrEmpty()]
        [string]$BuildScriptPath,
        # The name of the parameter used to specify the task name when invoking the build script.
        # This is used for help generation. Defaults to 'TaskName'.
        [ValidateNotNullOrEmpty()]
        [string]$TaskNameArgName = 'TaskName',
        # The name of the parameter used to specify task arguments when invoking the build script.
        # This is used for help generation. Defaults to 'TaskArgs'.
        [ValidateNotNullOrEmpty()]
        [string]$TaskArgsArgName = 'TaskArgs',

        # The name of the variable to save the TaskContext object to. Defaults to 'TaskContext'.
        # Note that this function does _NOT_ create this variable. The caller's must do that,
        # e.g. `$MyTaskContext = Initialize-TaskFramework -TaskContextVariableName 'MyTaskContext'`.
        [ValidateNotNullOrEmpty()]
        [string]$TaskContextVariableName = 'TaskContext'
    )
    & $PSScriptRoot/syncCallerPreferences.ps1 $MyInvocation

    if (!$BuildScriptPath) {
        $callersInvocation = $ExecutionContext.SessionState.Module.GetVariableFromCallersModule("MyInvocation")
        if (!$callersInvocation.Value.MyCommand.Path) {
            Write-Error -Exception "Could not determine caller's script path. Please provide the path via the -BuildScriptPath parameter."
            return
        }
        $BuildScriptPath = $callersInvocation.Value.MyCommand.Path
    }
    if (!(Test-Path $BuildScriptPath -PathType Leaf)) {
        Write-Error -Exception "The specified build script path '$BuildScriptPath' does not exist."
        return
    }

    $TaskContext = [TaskContext]@{
        BuildScriptPath = Get-Item $BuildScriptPath -Force
        TaskNameArgName = $TaskNameArgName
        TaskArgsArgName = $TaskArgsArgName
    }

    # Save the name in a global variable so that Get-TaskFrameworkContext can retrieve it.
    $script:ExternalTaskContextVariableName = $TaskContextVariableName

    return $TaskContext
}

function Get-TaskFrameworkContext {
    <#
    .DESCRIPTION
        Gets the current task framework context object.
    #>
    [OutputType([TaskContext])]
    param(
        [ValidateNotNullOrEmpty()]
        [string]$Name = $script:ExternalTaskContextVariableName
    )

    # First, try the current session state where it is always called 'TaskContext'...
    # Get-Variable will also return variables defined in the global scope...
    $TaskContextVar = Get-Variable -Name 'TaskContext' -ErrorAction Ignore
    if ($TaskContextVar.Module -eq $ExecutionContext.SessionState.Module) {
        $TaskContext = $TaskContextVar.Value
    }
    else {
        $TaskContext = $null
    }

    # if not found, try from caller's session state...
    if ($null -eq $TaskContext -and $ExecutionContext.SessionState.Module) {
        $TaskContext = $ExecutionContext.SessionState.Module.GetVariableFromCallersModule($Name).Value
    }

    if ($null -eq $TaskContext) {
        throw "Task context variable '$Name' not found. Make sure to define it in the build script, e.g., `$$Name = Initialize-TaskFramework."
    }
    if ($TaskContext -isnot [TaskContext]) {
        throw "Task context variable '$Name' is not a TaskContext object. Make sure to define it in the build script, e.g., `$$Name = Initialize-TaskFramework."
    }

    # looks good!
    return $TaskContext
}

function getTask {
    [CmdletBinding(PositionalBinding = $false)]
    [OutputType([TaskDefinition])]
    param(
        [Parameter(Mandatory, Position = 0)]
        [string]$TaskName,
        [ValidateNotNull()]
        [TaskContext]$TaskContext = (Get-TaskFrameworkContext)
    )

    if (!$TaskContext.AllTasks.Contains($TaskName)) {
        Write-Error -Exception "Task '$TaskName' not found." -CategoryActivity 'Get task' -Category ObjectNotFound -TargetObject $TaskName
        return
    }
    return $TaskContext.AllTasks[$TaskName]
}

function getAllOrderedTasks {
    <#
    .DESCRIPTION
        Gets an ordered dictionary of all defined tasks, sorted in dependency order.
        The keys are task names and the values are [TaskDefinition] objects.
    .OUTPUTS
        [System.Collections.Specialized.OrderedDictionary]
        An ordered dictionary of all defined tasks, sorted in dependency order.
    #>
    [CmdletBinding(PositionalBinding = $false)]
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param(
        [ValidateNotNull()]
        [TaskContext]$TaskContext = (Get-TaskFrameworkContext)
    )

    if ($TaskContext.AllTasksSorted -eq $true) {
        return $TaskContext.AllTasks
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
            $depTask = $TaskContext.AllTasks[$dep]
            if (-not $depTask) {
                throw "Dependency '$dep' of task '$($task.Name)' not found."
            }
            visit $depTask
        }
        $visited[$task.Name] = ''
        $task
    }

    $orderedTasks = @(
        foreach ($task in $TaskContext.AllTasks.Values) {
            visit $task
        }
    )

    $allTasks = [ordered]@{}
    foreach ($task in $orderedTasks) {
        $allTasks[$task.Name] = $task
    }

    $TaskContext.AllTasks = $allTasks
    $TaskContext.AllTasksSorted = $true
    return $allTasks
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
    [CmdletBinding(PositionalBinding = $false)]
    param(
        # The task context hashtable. Defaults to the current task context returned by Get-TaskFrameworkContext.
        [ValidateNotNull()]
        [TaskContext]$TaskContext = (Get-TaskFrameworkContext)
    )
    & $PSScriptRoot/syncCallerPreferences.ps1 $MyInvocation -PreferencesToSync ErrorAction
    try {
        return (getAllOrderedTasks @PSBoundParameters).Values
    }
    catch {
        Write-Error -Exception $_ -CategoryActivity 'Get-TaskFrameworkTasks' -Category InvalidOperation
    }
}

function addTask {
    <#
    .DESCRIPTION
        Private implementation of the `Task` function that adds a task to the task framework.
        This is separated from the public Task function to allow for easier error handling and
        to avoid syncing caller preferences multiple times when adding multiple tasks.
    #>
    [CmdletBinding()]
    param (
        [Parameter(Mandatory, Position = 0)]
        [string]$Name,
        [Parameter(Mandatory, Position = 1)]
        [AllowNull()]
        [ScriptBlock]$Action,
        [ValidateNotNull()]
        [string]$Description,
        [ValidateNotNull()]
        [string[]]$DependsOn = @(),
        [ValidateNotNull()]
        [int[]]$AllowedExitCodes = @(0),
        [ValidateNotNull()]
        [TaskContext]$TaskContext = (Get-TaskFrameworkContext)
    )
    if ($TaskContext.AllTasks.Contains($Name)) {
        Write-Error -Exception "A task with the name '$Name' already exists." -CategoryActivity 'Add task' -Category ResourceExists -TargetObject $Name
        return
    }
    $TaskContext.AllTasks[$Name] = [TaskDefinition]@{
        Name             = $Name
        Description      = $Description
        DependsOn        = $DependsOn
        AllowedExitCodes = $AllowedExitCodes
        Action           = $Action
    }
    $TaskContext.AllTasksSorted = $false
}

function Task {
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
        [ValidateNotNull()]
        [string]$Description,

        # An array of task names that this task depends on. These tasks will be executed
        # before this task unless -SkipDependencies is specified when invoking.
        [ValidateNotNull()]
        [string[]]$DependsOn = @(),

        # An array of allowed exit codes for the task. If the task completes with an
        # exit code that is not in this array, it will be considered a failure. Pass an
        # empty array to ignore the exit code. Defaults to 0.
        [ValidateNotNull()]
        [int[]]$AllowedExitCodes = @(0),

        # The task context hashtable. Defaults to the current task context returned by Get-TaskFrameworkContext.
        [ValidateNotNull()]
        [TaskContext]$TaskContext = (Get-TaskFrameworkContext)
    )
    & $PSScriptRoot/syncCallerPreferences.ps1 $MyInvocation -PreferencesToSync ErrorAction, InformationAction
    addTask @PSBoundParameters
}

function Add-TaskFrameworkDefaultTasks {
    <#
    .DESCRIPTION
        Adds default tasks to the task framework, such as 'list' and 'help'.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessage('PSUseSingularNouns', '')]
    [CmdletBinding(PositionalBinding = $false)]
    param(
        # The tasks to include.
        [Parameter(Position = 0)]
        [ValidateSet('help', 'list', 'null')]
        [string[]] $Include = @('help', 'list'),

        # A hashtable specifying custom names for the default tasks.
        [ValidateNotNull()]
        [hashtable] $NameMap = @{},

        # The task context hashtable. Defaults to the current task context returned by Get-TaskFrameworkContext.
        [ValidateNotNull()]
        [TaskContext]$TaskContext = (Get-TaskFrameworkContext)

    )
    & $PSScriptRoot/syncCallerPreferences.ps1 $MyInvocation -PreferencesToSync WarningAction, Verbose

    switch ($Include.where{ !(getTask ($NameMap[$_] ?? $_) -ea Ignore -TaskContext $TaskContext) }) {
        'null' {
            $name = $NameMap[$_] ?? $_
            Write-Verbose "Including default 'null' task as '$name'."
            addTask $name -Description 'An empty task that does nothing.' -TaskContext $TaskContext -Action $null
        }
        'list' {
            $name = $NameMap[$_] ?? $_
            Write-Verbose "Including default 'list' task as '$name'."
            addTask $name -Description 'List all defined tasks' -TaskContext $TaskContext {
                <#
                .DESCRIPTION
                    Lists all tasks defined in the task framework along with their descriptions
                    and dependencies.
                #>
                Get-TaskFrameworkTasks |
                Format-Table Name, Description, DependsOn -AutoSize |
                Out-Host
            }
        }
        'help' {
            $name = $NameMap[$_] ?? $_
            Write-Verbose "Including default 'help' task as '$name'."
            $TaskContext.HelpTaskName = $name
            addTask $name -Description 'Show detailed help for a task' -TaskContext $TaskContext {
                <#
                .DESCRIPTION
                    Generates help for a task defined in the task framework. If a task name is not
                    specified, it generates help for the build script itself.
                .EXAMPLE
                    PS> .\build.ps1 help

                    Shows help for the build script.
                .EXAMPLE
                    PS> .\build.ps1 help test -full

                    Shows detailed help for the 'test' task, including its description, parameters,
                    usage, dependencies, and examples.
                .EXAMPLE
                    PS> .\build.ps1 help test -examples

                    Shows the 'test' task examples.
                #>
                [CmdletBinding(PositionalBinding = $false)]
                param(
                    # The name of the task to show help for. If not specified, shows help for the build script itself.
                    [Parameter(Position = 0)]
                    [string]$TaskName,

                    # Displays the entire help article for a cmdlet. Full includes parameter descriptions and attributes,
                    # examples, input and output object types, and additional notes.
                    [Parameter(ParameterSetName = 'Full')]
                    [switch]$Full,

                    # Adds parameter descriptions and examples to the basic help display.
                    [Parameter(ParameterSetName = 'Detailed', Mandatory)]
                    [switch]$Detailed,

                    # Displays only the name, synopsis, and examples.
                    [Parameter(ParameterSetName = 'Examples', Mandatory)]
                    [switch]$Examples,

                    # When specified, the help output will not be paged. By default, the help output is paged
                    # if it exceeds the console height.
                    [switch]$NoPaging
                )

                $getHelpArgs = [hashtable]$PSBoundParameters
                $null = $getHelpArgs.Remove('TaskName')
                $null = $getHelpArgs.Remove('NoPaging')
                $help = Get-TaskFrameworkHelp -TaskName $TaskName -GetHelpArgs $getHelpArgs

                if ($NoPaging) { $help | Out-Host }
                elseif ($IsWindows -and (Get-Command 'more' -ErrorAction Ignore)) { $help | more }
                elseif (!$IsWindows -and (Get-Command 'less' -ErrorAction Ignore)) { $help | less }
                else { $help | Out-Host -Paging }
            }
        }
        default {
            Write-Warning "Unknown default task requested: '$_'."
        }
    }
}

function Get-TaskFrameworkHelp {
    <#
    .DESCRIPTION
        Generates help for a task defined in the task framework. If a task name is not
        specified, it generates help for the build script itself.

        The help output is generated by merging the help info from the build script and the
        task action. This allows the task help to include information about the build script's
        parameters and syntax, which is relevant when invoking the task via the build script.
    .OUTPUTS
        [System.String]
        The formatted help string.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        # The name of the task to show help for. If not specified, shows help for the build script itself.
        [string]$TaskName,

        # The arguments to pass to Get-Help when retrieving help info for the build script and task action.
        [ValidateNotNull()]
        [hashtable]$GetHelpArgs = @{},

        # The task context hashtable. Defaults to the current task context returned by Get-TaskFrameworkContext.
        [ValidateNotNull()]
        [TaskContext]$TaskContext = (Get-TaskFrameworkContext)
    )
    & $PSScriptRoot/syncCallerPreferences.ps1 $MyInvocation

    $helpTaskName = $TaskContext.HelpTaskName ?? $TaskContext.CurrentTask.Name ?? 'help'
    $buildScriptPath = $TaskContext.BuildScriptPath ?? (Get-Item './build.ps1')
    $taskNameArgName = $TaskContext.TaskNameArgName ?? 'TaskName'
    $taskArgsArgName = $TaskContext.TaskArgsArgName ?? 'TaskArgs'

    # Get the help info for the build script. We will merge it's parameters and syntax
    # with the task help info to produce the final help output.
    $buildHelp = Get-Help -Name $buildScriptPath @GetHelpArgs
    if (!$buildHelp) { return }

    if (-not ($buildHelp.parameters.parameter.name -eq $taskNameArgName)) {
        Write-Warning "Parameter '$taskNameArgName' was not found in the build script. Specify the actual parameter name in Initialize-TaskFramework."
    }
    if (-not ($buildHelp.parameters.parameter.name -eq $taskArgsArgName)) {
        Write-Warning "Parameter '$taskArgsArgName' was not found in the build script. Specify the actual parameter name in Initialize-TaskFramework."
    }

    function normalizeText([string]$text) {
        # trim and normalize line endings to CR to simplify regexes
        $text = $text -creplace '\s*?\r?\n', "`n"
        return $text
    }
    function finalizeText([string]$text) {
        # Assumes CR line endings.
        # remove empty section
        $text = $text -creplace '(?m)^(SYNOPSIS|DESCRIPTION|INPUTS|OUTPUTS|RELATED LINKS)\n\n+', ''
        # collapse consecutive empty lines
        $text = $text -creplace '\n\n\n+', "`n`n"
        # trim trailing empty lines to at most 1
        $text = $text -creplace '\n\n$', "`n"
        # convert to the environment's newline
        $text = $text -creplace '\r?\n', [Environment]::NewLine
        return $text
    }

    # If no task name specified, show the build script help
    if (!$TaskName) {
        $text = normalizeText ($buildHelp | Out-String)
        return finalizeText $text
    }

    # Get the specified task...
    $task = getTask $TaskName -TaskContext $TaskContext -ea Ignore
    if (-not $task) {
        throw "Task '$TaskName' not found."
    }

    $TaskName = $task.Name # use the canonical name
    $taskDescription = $task.Description
    if ($taskDescription) {
        # ensure ends with a period
        $taskDescription = $taskDescription -replace '\.\s*$', '.'
    }
    $taskAction = $task.Action ?? ([scriptblock]::Create("
        <#
        .DESCRIPTION
            This is a `"meta-task`" that only serves as a grouping of dependencies.
            No additional help is available for this task.
        #>
        param()"))

    # Use the taskAction to generate a temporary function that we can call Get-Help on to generate the
    # help info. We use a unique name to aid in regex replacements later.
    $functionName = "_$([Guid]::NewGuid().ToString('N'))"
    $tempModule = New-Module -ScriptBlock ([scriptblock]::Create("function $functionName {$taskAction.Ast.ParamBlock}"))
    $taskHelp = Get-Help $functionName @GetHelpArgs
    $tempModule | Remove-Module

    # Fixup the help object...
    function mamlParaTextItem($text) {
        return [PSObject]@{ Text = $text; type = 'text' } |
        Add-Member -TypeName 'MamlParaTextItem' -PassThru
    }
    function mamlParaText([string[]]$text) {
        # must return an array of ParaTextItem
        return , $text.ForEach{ mamlParaTextItem $_ }
    }

    # remove the ModuleName member
    if ($taskHelp.ModuleName) {
        $null = $taskHelp.PSObject.Members.Remove('ModuleName')
    }

    # fix the name
    $taskHelp.details | Add-Member 'name' $TaskName -Force
    $taskHelp | Add-Member 'Name' $TaskName -Force

    # set Synopsis using the task description, if missing
    if ($taskDescription) {
        if (!$taskHelp.details.description) {
            $taskHelp.details | Add-Member 'description' (mamlParaText $taskDescription) -Force
        }
        if (!$taskHelp.Synopsis) {
            $taskHelp | Add-Member 'Synopsis' $taskDescription -Force
        }
    }

    # merge the build script's parameters and syntax into the task's help...
    $taskCommonParameters = $task.Action -and ($taskHelp.CommonParameters ?? $true)
    $buildCommonParameters = $task.Action -and ($buildHelp.CommonParameters ?? $true)

    $dashSwitch = [PSObject]@{
        name          = '-'
        description   = mamlParaText 'A meta-parameter used to separate build script parameters from task parameters.'
        required      = $false
        globbing      = $false
        pipelineInput = $false
        position      = 'named'
    } | Add-Member -TypeName 'MamlCommandHelpInfo#parameter' -PassThru

    # Update the description of the 'TaskName' parameter to indicate the value it should have to invoke the task.
    foreach ($param in $buildHelp.parameters.parameter) {
        if ($param.name -eq $taskNameArgName) {
            $param.description = mamlParaText "The name of the task to execute. '$TaskName' in this case."
            break
        }
    }

    # Merge parameters:
    # We omit the build script's 'TaskArgs' parameters since the actual task args are already included.
    if ($null -eq $taskHelp.parameters) {
        $taskHelp | Add-Member 'parameters' ([PSCustomObject]@{ parameter = @() } | Add-Member -TypeName 'ExtendedCmdletHelpInfo#parameters' -PassThru) -Force
    }
    if ($null -eq $taskHelp.parameters.parameter) {
        $taskHelp.parameters | Add-Member 'parameter' @() -Force
    }
    $taskHelp.parameters.parameter = @(
        $buildHelp.parameters.parameter.where{ $_.name -ne $taskArgsArgName }
        if ($taskHelp.parameters.parameter.Count -or $taskCommonParameters) { $dashSwitch }
        $taskHelp.parameters.parameter
    )

    # Merge Syntax:
    # We omit the 'TaskName' parameter itself since the task name is fixed in this context.
    # We only include those build script syntax items that include the 'TaskName' parameter
    # since those are the ones relevant to invoking tasks.
    $buildSyntaxItems = $buildHelp.syntax.syntaxItem.where{ $_.parameter.name -eq $TaskNameArgName }

    if ($null -eq $taskHelp.syntax) {
        $taskHelp | Add-Member 'syntax' ([PSCustomObject]@{ syntaxItem = @() }) -Force
    }
    if ($null -eq $taskHelp.syntax.syntaxItem) {
        $taskHelp.syntax | Add-Member 'syntaxItem' @() -Force
    }
    if ($taskHelp.syntax.syntaxItem.Count -eq 0) {
        # add a default syntaxItem so that we have somewhere to merge the build script syntax into
        $taskHelp.syntax.syntaxItem += [PSCustomObject]@{
            name      = $functionName
            parameter = @()
        } | Add-Member -TypeName 'MamlCommandHelpInfo#syntaxItem' -PassThru
    }
    $taskHelp.syntax.syntaxItem = @(
        foreach ($buildSyntaxItem in $buildSyntaxItems) {
            $buildParams = $buildSyntaxItem.parameter.where{ $_.name -notin $taskNameArgName, $taskArgsArgName }
            foreach ($item in $taskHelp.syntax.syntaxItem) {
                $copy = $item.PSObject.Copy() # shallow copy
                $copy.name = "$buildScriptPath [-$taskNameArgName] $TaskName"
                $copy | Add-Member 'parameter' @(
                    $buildParams
                    if ($item.parameter.Count -or $taskCommonParameters) { $dashSwitch }
                    $item.parameter
                ) -Force
                $copy
            }
        }
    )

    # The remaining fixups are done via text manipulation on the help output.
    $text = normalizeText ($taskHelp | Out-String)
    function escapeRegex([string]$s) { return [regex]::Escape($s) }
    function escapeReplacement([string]$s) { return $s.Replace('$', '$$') }

    # change NAME heading to TASK NAME
    $text = $text -creplace '(?m)^NAME$', 'TASK NAME'
    # replace the temp function name with the build script help invocation.
    $text = $text -creplace $functionName, (escapeReplacement "$buildScriptPath $TaskName")

    # Insert 'DEPENDS ON' section before RELATED LINKS or REMARKS
    if ($text -match '(?m)^(RELATED LINKS|REMARKS)$') {
        $insertBefore = $Matches[1]
        $dependsOnText = @(
            if ($task.DependsOn) {
                'This task depends on the following tasks:'
                $task.DependsOn.foreach{ "- $_" }
            }
            else {
                'This task has no task dependencies.'
            }
        )
        $dependsOnText = "DEPENDS ON`n    $($dependsOnText -join "`n    ")"
        $text = $text -creplace "(?m)^$insertBefore`$", "$(escapeReplacement $dependsOnText)`n`n`$0"
    }

    if ($buildCommonParameters) {
        # Insert build script's "[<CommonParameters>]" before the '--' separator
        $text = $text -creplace "($(escapeRegex $buildScriptPath) .*?)(\[--])", '$1[<CommonParameters>] $2'
    }
    if ($taskCommonParameters) {
        # Rename the task's [<CommonParameters>] (the one after the '--') to avoid confusion with the
        # build script's common parameters
        $text = $text -creplace "($(escapeRegex $buildScriptPath) .*? \[--] .*?)\[\<CommonParameters\>\]", '$1[<TaskCommonParameters>]'
    }

    # fixup the additional help remarks
    $text = $text -creplace "Get-Help $(escapeRegex $TaskName) ", "$(escapeReplacement "$buildScriptPath $HelpTaskName $TaskName") -- "

    return finalizeText $text
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
        [Parameter(Mandatory)]
        [System.Management.Automation.ErrorRecord]$ErrorRecord,
        [Parameter(Mandatory)]
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
    #>
    [Diagnostics.CodeAnalysis.SuppressMessage('PSAvoidUsingInvokeExpression', '', Justification = 'Using Invoke-Expression is necessary to allow task actions to accept named arguments.')]
    [CmdletBinding(PositionalBinding = $false)]
    param(
        [Parameter(Mandatory)]
        [TaskDefinition]$Task,
        [ValidateNotNull()]
        [object[]]$TaskArgs = @(),
        [ValidateNotNull()]
        [TaskContext]$TaskContext = (Get-TaskFrameworkContext)
    )

    $TaskName = $Task.Name

    $global:LASTEXITCODE = 0
    $result = [TaskResult]@{
        Start    = [datetime]::UtcNow
        TaskArgs = [TaskArgs]@{
            Raw = $TaskArgs
        }
    }
    $TaskContext.CurrentTask = $Task
    $TaskContext.Results[$TaskName] = $result

    if ($null -eq $Task.Action) {
        Write-Verbose "Skipping task '$TaskName' since it has no action."
        $result.Duration = [TimeSpan]::Zero
        $result.ExitCode = 0
        $TaskContext.CurrentTask = $null
        return
    }

    $taskCommandArgs = @()
    if ($TaskArgs) {
        Import-Module PSArgs -Verbose:$false
        $taskCommandArgs = ConvertTo-CommandArg $TaskArgs
        Write-Verbose "Invoking task '$TaskName' with arguments: $taskCommandArgs"
    }
    else {
        Write-Verbose "Invoking task '$TaskName' with no arguments."
    }

    # Use Invoke-Expression to parse $TaskArgs in the context of the task's action.
    # This enables $TaskArgs to contain named arguments (i.e. '-foo','bar') not
    # just positional arguments ('bar')...
    try {
        $tArgs = Invoke-Expression "&{`n$($Task.Action.Ast.ParamBlock) return @{Bound=`$PSBoundParameters;Unbound=`$args}} $taskCommandArgs"
        $result.TaskArgs.Bound = $tArgs.Bound
        $result.TaskArgs.Unbound = $tArgs.Unbound
    }
    catch {
        Repair-TaskStackTrace -ErrorRecord $_ -Task $Task -TaskActionStartLine 2
        $result.Error = $_
        $result.Duration = [datetime]::UtcNow - $result.Start
        throw
    }

    # Now invoke the task action with the parsed arguments...
    try {
        $bound = $result.TaskArgs.Bound
        $unbound = $result.TaskArgs.Unbound
        & $Task.Action @bound @unbound
    }
    catch {
        $result.Error = $_
        throw
    }
    finally {
        $result.Duration = [datetime]::UtcNow - $result.Start
    }

    $result.ExitCode = $global:LASTEXITCODE
    if ($Task.AllowedExitCodes.Count -gt 0 -and $result.ExitCode -notin $Task.AllowedExitCodes) {
        Write-Verbose "Task '$TaskName' failed with exit code $($result.ExitCode)."
        throw "Task '$TaskName' failed with exit code $($result.ExitCode)."
    }

    # Success!
    Write-Verbose "Completed task '$TaskName' with exit code $($result.ExitCode)."
    $TaskContext.CurrentTask = $null
    $global:LASTEXITCODE = 0 # reset to avoid affecting the final exit code
}

function getOrderedTasks {
    <#
    .DESCRIPTION
        Returns an ordered array of TaskDefinition objects corresponding to the specified task
        names and their dependencies (if $includeDependencies is specified). The tasks are returned
        in dependency order. For example, if taskA depends on taskB, then `getOrderedTasks taskA $true`
        will return an array with taskB first, followed by taskA.
    .OUTPUTS
        [TaskDefinition[]]
        An array of TaskDefinition objects corresponding to the specified task names and their dependencies (if $includeDependencies is specified), sorted in dependency order.
    #>
    [CmdletBinding(PositionalBinding = $false)]
    [OutputType([TaskDefinition])]
    param(
        [Parameter(Mandatory, Position = 0)]
        [string[]]$TaskNames,
        [switch]$IncludeDependencies,
        [TaskContext]$TaskContext = (Get-TaskFrameworkContext)
    )

    # get all tasks in dependency order...
    $orderedTaskMap = getAllOrderedTasks -TaskContext $TaskContext

    # get the set of all tasks to execute, including dependencies if specified.
    $execTaskNames = @{}
    $queue = [System.Collections.Generic.Queue[string]]::new($TaskNames)
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
        if ($IncludeDependencies) {
            foreach ($dep in $task.DependsOn) {
                $queue.Enqueue($dep)
            }
        }
    }

    # Return the tasks in dependency order...
    return $orderedTaskMap.Values.foreach{
        if ($execTaskNames.ContainsKey($_.Name)) {
            $_
        }
    }
}

function Invoke-TaskFramework {
    <#
    .DESCRIPTION
        Invokes one or more tasks defined in the task framework.

        Tasks will be executed in the order they were defined. If a task has dependencies,
        those will be executed first unless $SkipDependencies is specified.
    #>
    [CmdletBinding(PositionalBinding = $false)]
    param(
        # The name(s) of the task(s) to invoke.
        [Parameter(Mandatory)]
        [string[]]$TaskName,

        # Task-specific arguments. Can only be used when invoking a _single_ task.
        [ValidateNotNull()]
        [object[]]$TaskArgs = @(),

        # Indicates whether to skip invoking dependencies of specified tasks. Defaults to $false.
        [switch]$SkipDependencies,

        # The working directory in which to invoke the task(s).
        # Defaults to the BuildScriptPath's directory.
        [ValidateNotNullOrEmpty()]
        [string]$WorkingDirectory,

        # Indicates that the function should exit the script if a failure occurs. If not specified,
        # the function will throw an exception failure.
        [switch]$ExitOnError,

        # The task context hashtable. Defaults to the current task context returned by Get-TaskFrameworkContext.
        [ValidateNotNull()]
        [TaskContext]$TaskContext = (Get-TaskFrameworkContext)
    )
    & $PSScriptRoot/syncCallerPreferences.ps1 $MyInvocation
    $ErrorActionPreference = 'Stop'

    if (!$WorkingDirectory) {
        $WorkingDirectory = $TaskContext.BuildScriptPath.DirectoryName
    }
    elseif (!(Test-Path $WorkingDirectory -PathType Container)) {
        Write-Error -Exception "The specified working directory '$WorkingDirectory' does not exist."
        return
    }

    $TaskContext.WorkingDirectory = Get-Item $WorkingDirectory -Force
    $TaskContext.SkipDependencies = $SkipDependencies.IsPresent
    $TaskContext.Start = [datetime]::UtcNow

    $orig = @{
        PSModulePath = $env:PSModulePath
        Location     = Get-Location
    }

    Write-Verbose "Working directory: '$($TaskContext.WorkingDirectory)'."
    Set-Location $TaskContext.WorkingDirectory
    try {
        if ($TaskArgs.Count -gt 0 -and $TaskName.Count -gt 1) {
            throw 'Task arguments cannot be used when invoking multiple tasks.'
        }

        # Add this scripts directory to the module path so that task actions
        # can import our helper modules using the module's folder name.
        $pathSeparator = $IsWindows ? ';' : ':'
        $env:PSModulePath = "$PSScriptRoot$pathSeparator$env:PSModulePath"

        $tasksToExecute = getOrderedTasks $TaskName -IncludeDependencies:(!$TaskContext.SkipDependencies) -TaskContext $TaskContext
        $TaskContext.TasksToExecute = $tasksToExecute
        Write-Verbose "Executing tasks: $($tasksToExecute.Name -join ', ')"

        foreach ($task in $tasksToExecute) {
            Invoke-Task -Task $task -TaskArgs ($task.Name -eq $TaskName ? $TaskArgs : @()) -TaskContext $TaskContext
            Set-Location $TaskContext.WorkingDirectory
        }

        Write-Verbose "Done executing tasks."
        $TaskContext.ExitCode = $global:LASTEXITCODE
    }
    catch {
        if ($global:LASTEXITCODE -eq 0) { $global:LASTEXITCODE = -1 }
        $TaskContext.ExitCode = $global:LASTEXITCODE
        $TaskContext.Error = $_

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
        $TaskContext.Duration = [datetime]::UtcNow - $TaskContext.Start
        $env:PSModulePath = $orig.PSModulePath
        Set-Location $orig.Location
    }
}

# !Important! Remember to update the module manifest (.psd1) when adding or removing exports.
Export-ModuleMember -Function `
    Initialize-TaskFramework, `
    Get-TaskFrameworkContext, `
    Task, `
    Get-TaskFrameworkTasks, `
    Invoke-TaskFramework, `
    Get-TaskFrameworkHelp, `
    Add-TaskFrameworkDefaultTasks
