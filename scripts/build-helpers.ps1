# SPDX-License-Identifier: Unlicense
# Source: http://github.com/mrfootoyou/pstaskframework
# spell:ignore psargs
#Requires -Version 7.4

[Diagnostics.CodeAnalysis.SuppressMessage('PSAvoidUsingPositionalParameters', 'Invoke-Shell', Justification = 'Invoke-Shell is intended to be used with positional parameters.')]
param()

Import-Module "$PSScriptRoot/secrets.psm1" -Verbose:$false
Import-Module "$PSScriptRoot/psargs.psm1" -Verbose:$false

$RepoRoot ??= Split-Path $PSScriptRoot -Parent

function Test-Administrator {
    if ($IsWindows) {
        # test for administrator on Windows
        return [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    else {
        # test for root user on Linux/macOS
        return (id -u) -eq 0
    }
}

function Assert-AppExists {
    <#
    .DESCRIPTION
        Check if the specified application exists.
    .NOTES
        The ErrorAction parameter (-ea) defaults to 'Stop'. Specify an explicit value to override.
    .OUTPUTS
        None
        By default, this cmdlet returns no output.

        [System.String]
        If you specify the PassThru parameter, the cmdlet returns the full path to the application.
    #>
    [CmdletBinding(PositionalBinding = $false)]
    [OutputType([string])]
    [Diagnostics.CodeAnalysis.SuppressMessage('PSUseSingularNouns', '', Justification = 'Exists is 3rd person present verb.')]
    param(
        # The name or path to the application to check.
        # For maximum compatibility on non-Windows platforms, use the app name without the
        # file extension (e.g. "git" instead of "git.exe").
        [Parameter(Mandatory, Position = 0)]
        [string] $AppPath,
        # An optional friendly name to use in error messages. For example, "Azure CLI".
        [string] $AppTitle,
        # If specified, the cmdlet will return the full path to the application if it exists.
        [switch] $PassThru
    )
    # Set the ErrorActionPreference to 'Stop' if not explicitly specified.
    if (!$PSBoundParameters.ContainsKey('ErrorAction')) {
        $ErrorActionPreference = 'Stop'
    }

    # When multiple commands with the same name are found, Get-Command returns
    # them in execution precedence order. So take the first one
    $cmd = Get-Command $AppPath -CommandType Application -ea Ignore | Select-Object -First 1
    if (!$cmd) {
        if ($ErrorActionPreference -ne 'Ignore') {
            $appName = $AppTitle ? "$AppTitle ($AppPath)" : $AppPath
            Write-Error -Exception "$appName not found. Please bootstrap first using '.\build.ps1 bootstrap'." `
                -CategoryActivity 'Assert-AppExists' -CategoryReason 'App not found' -CategoryTargetName $AppPath
        }
        return
    }
    if ($PassThru) {
        return $cmd.Source
    }
}

function Invoke-Shell {
    <#
    .SYNOPSIS
        Invokes a shell application.
    .DESCRIPTION
        Invokes a shell application with arguments. The full command is echoed
        to the console, unless the -NoEcho switch is specified.

        If the command completes with a non-zero exit code, it is considered to have
        failed and an error stating as much is reported/thrown according to the
        -ErrorAction parameter. The $global:LASTEXITCODE variable will always contain
        the command's exit code.

        Best practice is to separate any PowerShell arguments from command arguments
        using `-- `.
        Arguments after the `-- ` separator are guaranteed to be passed verbatim to
        the invoked command, while arguments before the separator _may_ be interpreted
        as PowerShell arguments.

        For example:
            Invoke-Shell -- dotnet build -v quiet

        Without the `-- ` separator, PowerShell would interpret the '-v' as a PowerShell
        argument (-Verbose).
    #>
    [CmdletBinding()]
    param(
        # The command to execute. This can be a simple command name (e.g. "git") or a path
        # to an executable. The command must be an application which completes with an
        # exit code indicating success (0) or failure (non-zero).
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Command,

        # The arguments to pass to the command.
        # You typically do not use this parameter directly since PowerShell will automatically
        # add any unrecognized arguments to this parameter.
        [Parameter(ValueFromRemainingArguments)]
        [string[]] $CommandArgs,

        # When specified, the full command will NOT be echoed to the console before execution.
        [switch] $NoEcho,

        [int[]] $AllowedExitCodes = @(0)
    )

    $cmdPath = Assert-AppExists $Command -PassThru
    $cmdText = Protect-Secret "$(ConvertTo-PSString $cmdPath) $(ConvertTo-CommandArgs $CommandArgs)"
    if (!$NoEcho) { Write-Host "$($PSStyle.Dim)>> $cmdText" }

    $global:LASTEXITCODE = 0
    $PSNativeCommandUseErrorActionPreference = $false # we'll handle errors ourselves
    & $cmdPath @CommandArgs

    if ($global:LASTEXITCODE -notin $AllowedExitCodes) {
        if ($ErrorActionPreference -ne 'Ignore') {
            Write-Error -Exception "Command failed with exit code $global:LASTEXITCODE ($cmdText)." `
                -CategoryActivity 'Invoke-Shell' -CategoryReason 'Non-zero exit code' -CategoryTargetName $Command
        }
    }
}

