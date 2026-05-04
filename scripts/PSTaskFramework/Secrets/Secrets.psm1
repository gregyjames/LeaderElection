<#
.DESCRIPTION
    Secret management helpers for PowerShell.
.NOTES
    SPDX-License-Identifier: Unlicense
    Source: http://github.com/mrfootoyou/pstaskframework
#>
#Requires -Version 7.4
# spell:ignore bstr

[Diagnostics.CodeAnalysis.SuppressMessage('PSAvoidGlobalVars', 'global:__PSTaskFramework_Secrets', Justification = 'Intended to be used this way.')]
param(
    # The scope of the secret storage. Can be 'Local' or 'Global'. Defaults to 'Global'.
    [ValidateSet('Local', 'Global')]
    [string] $SecretScope = 'Global'
)

if ($SecretScope -eq 'Local') {
    $script:secrets = [PSCustomObject]@{
        values = [System.Collections.Generic.Dictionary[string, int]]::new([System.StringComparer]::Ordinal)
        regex  = $null
    }
}
else {
    $global:__PSTaskFramework_Secrets ??= [PSCustomObject]@{
        values = [System.Collections.Generic.Dictionary[string, int]]::new([System.StringComparer]::Ordinal)
        regex  = $null
    }
    $script:secrets = $global:__PSTaskFramework_Secrets

    if ($ExecutionContext.SessionState.Module) {
        $ExecutionContext.SessionState.Module.OnRemove = {
            Get-Variable -Scope Global -Name __PSTaskFramework_Secrets -ErrorAction Ignore |
            Remove-Variable -Scope Global -Force -ErrorAction Ignore
        }
    }
}

# Mockable functions for testing purposes. These are not for external use.
function getState {
    return $script:secrets
}

function isContinuousIntegration {
    return $env:CI -in @('1', 'true')
}

function Clear-SecretStore {
    <#
    .DESCRIPTION
        Clears all secrets from the secret store.
    #>
    [CmdletBinding()]
    param()
    $secrets = getState
    $secrets.values.Clear()
    $secrets.regex = $null
}

function Push-Secret {
    <#
    .DESCRIPTION
        Registers a secret value to be masked in the output of Protect-Secret.

        Secret values are case-sensitive and are compared using ordinal string
        comparison.

        Note that secrets are reference counted, thus if a secret value is pushed
        multiple times, it must be popped the same number of times to be fully
        unregistered.
    #>
    [CmdletBinding()]
    param (
        [Parameter(Mandatory, ValueFromPipeline)]
        [ValidateNotNullOrEmpty()]
        [string]$Value
    )
    process {
        $secrets = getState
        $n = ($secrets.values[$Value] += 1)
        if ($n -eq 1) {
            $secrets.regex = $null
        }
    }
}

function Pop-Secret {
    <#
    .DESCRIPTION
        Unregisters a secret value previously registered with Push-Secret.

        Secret values are case-sensitive and are compared using ordinal string
        comparison.

        Note that secrets are reference counted, thus if a secret value is pushed
        multiple times, it must be popped the same number of times to be fully
        unregistered.

        An error is reported if the secret value was not previously registered or
        if it has already been popped the same number of times it was pushed.
        Use `-ErrorAction SilentlyContinue` or `-ErrorAction Ignore` to suppress
        such errors.
    #>
    [CmdletBinding()]
    param (
        # The secret value which was previously registered with Push-Secret.
        [Parameter(Mandatory, ValueFromPipeline)]
        [ValidateNotNullOrEmpty()]
        [string]$Value
    )
    begin {
        & $PSScriptRoot/../syncCallerPreferences.ps1 $MyInvocation -PreferencesToSync ErrorAction
    }
    process {
        $secrets = getState
        if (!$secrets.values.ContainsKey($Value)) {
            Write-Error -Exception 'Secret not found.' -CategoryActivity 'Pop-Secret' -Category 'ObjectNotFound' -ErrorId 'SecretNotFound' -TargetObject $Value
            return
        }
        $n = ($secrets.values[$Value] -= 1)
        if ($n -eq 0) {
            $null = $secrets.values.Remove($Value)
            $secrets.regex = $null
        }
    }
}

function Protect-Secret {
    <#
    .DESCRIPTION
        Replaces all registered secret values in the given string by replacing them with
        the specified mask value (default is '****').
    .OUTPUTS
        [System.String]
        The input string with all registered secrets replaced by the mask.
    #>
    [CmdletBinding(PositionalBinding = $false)]
    [OutputType([string])]
    param (
        [Parameter(Mandatory, Position = 0, ValueFromPipeline)]
        [AllowEmptyString()]
        [string]$Message,
        [AllowEmptyString()]
        [string]$Mask = '****'
    )
    process {
        $secrets = getState
        if (!$secrets.regex -and $secrets.values.Count) {
            $pattern = ($secrets.values.Keys |
                Sort-Object Length -Descending |
                ForEach-Object { [regex]::Escape($_) }) -join '|'
            $secrets.regex = [regex]::new($pattern)
        }
        if ($secrets.regex) {
            # Use a match-evaluator overload to prevent '$0' from reintroducing the secret value
            $secrets.regex.Replace($Message, { $Mask })
            $null = $Mask # Avoid incorrect "unused parameter" warning
        }
        else {
            $Message
        }
    }
}

function Read-Secret {
    <#
    .DESCRIPTION
        Reads a secret value from the console without echoing it to the screen.
        The secret is returned as a plain string.
    .OUTPUTS
        [System.String]
        The secret value read from the console.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        # The prompt to display to the user
        [Parameter(Mandatory)]
        [string] $Prompt,
        [switch] $AllowEmpty
    )
    & $PSScriptRoot/../syncCallerPreferences.ps1 $MyInvocation -PreferencesToSync ErrorAction, WarningAction

    if (isContinuousIntegration) {
        if ($AllowEmpty) {
            Write-Warning "CI environment detected. Returning empty value for prompt '$Prompt'."
            return ''
        }
        Write-Error -Exception 'Cannot read input in CI environment.' -CategoryActivity 'Read-Secret'
        return
    }
    $value = Read-Host $Prompt -AsSecureString
    if ($value) {
        $bstr = [System.IntPtr]::Zero
        try {
            $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($value)
            $value = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
        }
        finally {
            if ($bstr -ne [System.IntPtr]::Zero) {
                [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
            }
        }
    }
    if (!$value -and !$AllowEmpty) {
        Write-Error -Exception 'No value provided.' -CategoryActivity 'Read-Secret'
        return
    }
    return $value
}

$exportModuleMemberParams = @{
    Function = @(
        'Read-Secret'
        'Push-Secret'
        'Pop-Secret'
        'Protect-Secret'
        'Clear-SecretStore'
    )
}

Export-ModuleMember @exportModuleMemberParams
