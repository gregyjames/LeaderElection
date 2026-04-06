# SPDX-License-Identifier: Unlicense
# Source: http://github.com/mrfootoyou/pstaskframework
# spell:ignore bstr
#Requires -Version 7.4

$secrets = [PSCustomObject]@{
    values = @{}
    regex  = $null
}

function Push-Secret {
    <#
    .DESCRIPTION
        Registers a secret value to be masked in the output of Protect-Secret.

        The secret is reference counted, thus every call to `Push-Secret X` must have
        a corresponding `Pop-Secret X`.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessage('PSPossibleIncorrectUsageOfAssignmentOperator', '', Justification = 'Intended to be used this way.')]
    param (
        [Parameter(Mandatory = $true, ValueFromPipeline)]
        [string]$s
    )
    process {
        if ($s -AND ($secrets.values[$s] += 1) -eq 1) {
            $secrets.regex = $null
        }
    }
}

function Pop-Secret {
    <#
    .DESCRIPTION
        Unregisters a secret value previously registered with Push-Secret.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessage('PSPossibleIncorrectUsageOfAssignmentOperator', '', Justification = 'Intended to be used this way.')]
    param (
        [Parameter(Mandatory = $true, ValueFromPipeline)]
        [string]$s
    )
    process {
        if ($s -AND $secrets.values.ContainsKey($s) -AND ($secrets.values[$s] -= 1) -eq 0) {
            $null = $secrets.values.Remove($s)
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
    [Diagnostics.CodeAnalysis.SuppressMessage('PSReviewUnusedParameter', 'Mask', Justification = 'Not unused.')]
    param (
        [Parameter(Mandatory = $true, Position = 0, ValueFromPipeline)]
        [AllowEmptyString()]
        [string]$Message,
        [AllowEmptyString()]
        [string]$Mask = '****'
    )
    process {
        if (!$secrets.regex -and $secrets.values.Count) {
            $secrets.regex = [regex]::new($secrets.values.Keys.foreach{ [regex]::Escape($_) } -join '|')
        }
        if ($secrets.regex) {
            # Use a match-evaluator overload to prevent '$0' from reintroducing the secret value
            $secrets.regex.Replace($Message, { $Mask })
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
    if ($env:CI) {
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

Export-ModuleMember -Function Read-Secret, Push-Secret, Pop-Secret, Protect-Secret
