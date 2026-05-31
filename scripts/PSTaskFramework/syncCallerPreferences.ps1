<#
.SYNOPSIS
    Syncs the calling module's preference variables into the caller's scope.
.DESCRIPTION
    Annoyingly, advanced functions defined in a script-module do not inherit the preferences variables
    from the caller's scope. This function manually imports the preferences variables from the calling
    module into the scope of the caller.

    See https://github.com/PowerShell/PowerShell/issues/4568 for more information about this issue.
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    # The Invocation trying to sync preferences.
    # Defaults to the invocation of the caller of this function. Pass $MyInvocation for a slight
    # performance boost, since it will avoid a call to Get-PSCallStack.
    [Parameter(Position = 0)]
    [System.Management.Automation.InvocationInfo] $Invocation,

    # The scope of the Invocation, relative to the caller. Defaults to 0, which is the caller's scope.
    [int] $Scope = 0,

    # The preferences to sync.
    [string[]] $PreferencesToSync = @(
        'ErrorAction'
        'WarningAction'
        'InformationAction'
        'Verbose'
        'Debug'
        'Confirm'
        'WhatIf'
    ),

    # The preferences to ignore.
    [ValidateSet('ErrorAction', 'WarningAction', 'InformationAction', 'Verbose', 'Debug', 'Confirm', 'WhatIf')]
    [string[]] $PreferencesToIgnore = @(),

    # Additional variables to sync.
    [string[]] $VariablesToSync = @()
)

# Make scope relative to this function instead of the caller
$Scope += 1

if (!$Invocation) {
    $callstack = Get-PSCallStack
    $Invocation = $callstack[$Scope].InvocationInfo
}

$boundParameters = $Invocation.BoundParameters
$callerModule = $Invocation.MyCommand.Module
if ($null -eq $callerModule) {
    throw 'Invocation must be from a script module.'
}

@(
    $VariablesToSync
    switch ($PreferencesToSync | Where-Object { $_ -notin $PreferencesToIgnore }) {
        'ErrorAction' { if (!$boundParameters.ContainsKey($_)) { 'ErrorActionPreference' } }
        'WarningAction' { if (!$boundParameters.ContainsKey($_)) { 'WarningPreference' } }
        'InformationAction' { if (!$boundParameters.ContainsKey($_)) { 'InformationPreference' } }
        'Verbose' { if (!$boundParameters.ContainsKey($_)) { 'VerbosePreference' } }
        'Debug' { if (!$boundParameters.ContainsKey($_)) { 'DebugPreference' } }
        'Confirm' { if (!$boundParameters.ContainsKey($_)) { 'ConfirmPreference' } }
        'WhatIf' { if (!$boundParameters.ContainsKey($_)) { 'WhatIfPreference' } }
    }
) |
Select-Object -Unique |
ForEach-Object {
    $v = $_
    try {
        $var = $callerModule.GetVariableFromCallersModule($v)
        if ($var) {
            Set-Variable -Name $var.Name -Value $var.Value -Scope $Scope -Force -ErrorAction Stop -Verbose:$false -WhatIf:$false -Confirm:$false
        }
        else {
            Write-Warning "Failed to sync variable '$v': Variable not found in caller's module."
        }
    }
    catch {
        Write-Warning "Failed to sync variable '$v': $_"
    }
}
