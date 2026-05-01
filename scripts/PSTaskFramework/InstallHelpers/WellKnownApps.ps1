<#
.DESCRIPTION
    Well-known applications metadata for installation helpers.
.NOTES
    SPDX-License-Identifier: Unlicense
    Source: http://github.com/mrfootoyou/pstaskframework
#>
#Requires -Version 7.4
# spell:ignore psargs,winget,choco,8wekyb3d8bbwe,assumeyes,myapp

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseDeclaredVarsMoreThanAssignments', 'WellKnownApps')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'appName')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'appInfo')]
param()

Import-Module "$PSScriptRoot/../BuildHelpers" -Verbose:$false

# Canonical install metadata for common tools.
# See `Install-RequiredApp` in `InstallHelpers.psm1` for details on the expected
# format of this data and how it is used.
$WellKnownApps = [ordered]@{
    'powershell'    = [ordered]@{
        website    = 'https://aka.ms/install-powershell'
        executable = 'pwsh'
        version    = { ((pwsh --version) -split ' ')[-1] }
        isUpToDate = { isPowerShellUpToDate @args }
        script     = { installPowerShell @args }
    }
    'git'           = [ordered]@{
        website = 'https://git-scm.com/downloads'
        version = { ((git --version) -split ' ')[-1] }
        winget  = 'Git.Git'
        choco   = 'git'
        apt     = 'git'
        dnf     = 'git'
        brew    = 'git'
    }
    'dotnet-sdk-10' = [ordered]@{
        website    = 'https://aka.ms/dotnet-download'
        executable = 'dotnet'
        version    = { dotnet --version }
        isUpToDate = { (dotnet --version) -like '10.*' }
        winget     = 'Microsoft.DotNet.SDK.10'
        choco      = 'dotnet-10.0-sdk'
        apt        = 'dotnet-sdk-10.0'
        dnf        = 'dotnet-sdk-10.0'
        brew       = 'dotnet-sdk' # dotnet-sdk@10 not yet available
    }
    'docker'        = [ordered]@{
        website        = 'https://docs.docker.com/get-docker/'
        version        = { docker version --format '{{.Server.Version}}' }
        winget         = 'Docker.DockerDesktop'
        choco          = 'docker-desktop'
        'brew:macos'   = '--cask', 'docker-desktop'
        'script:linux' = { installDockerLinux @args }
    }
}

function script:isPowerShellUpToDate {
    # This function checks if the installed PowerShell version meets or exceeds the latest
    # stable release available on GitHub. The version id is cached for 6 hours to minimize
    # network calls, with error handling to retry sooner if the check fails.
    param($appName, $appInfo)
    $data = ($appInfo.data ??= @{})

    # Cache latest release checks to avoid frequent network calls during repeated tasks.
    if (!$data.LatestVersion -or [DateTime]::Now -ge $data.NextVersionCheck) {
        # get latest version from GitHub by inspecting the redirect from the "latest" release URL
        $resp = Invoke-WebRequest 'https://github.com/PowerShell/PowerShell/releases/latest' -MaximumRedirection 0 -SkipHttpErrorCheck -ErrorAction SilentlyContinue -ErrorVariable err -Verbose:$false
        if ($resp -and $resp.StatusCode -eq 302) {
            # Location: https://github.com/PowerShell/PowerShell/releases/tag/v7.6.0
            $latestVer = ([uri]$resp.Headers['Location'][0]).Segments[-1].TrimStart('v')
            $data.LatestVersion = $latestVer
            $data.NextVersionCheck = [DateTime]::Now.AddHours(6) # check every 6 hours
        }
        else {
            if ($err) { Write-Warning "Unexpected error when checking latest PowerShell version: $err" }
            else { Write-Warning "Unexpected response when checking latest PowerShell version: Expected 302 but got $($resp.StatusCode) - $($resp.StatusDescription)" }
            $data.NextVersionCheck = [DateTime]::Now.AddMinutes(10) # try again in 10 minutes
            return $true
        }
    }
    else {
        Write-Verbose "Using cached latest PowerShell version: $($data.LatestVersion). Next check at $($data.NextVersionCheck)."
    }
    return $data.LatestVersion -and $PSVersionTable.PSVersion -ge $data.LatestVersion
}

function script:installPowerShell {
    # This is a simple wrapper that directs users to the official installation instructions,
    # since PowerShell's cross-platform installer is a bit more complex than a single command
    # and varies by platform.
    param($appName, $appInfo)

    Write-Host "Install PowerShell $($appInfo.data.LatestVersion) from $($appInfo.website)." -ForegroundColor Magenta
}

function script:installDockerLinux {
    # This is a simple wrapper around the official convenience script at https://get.docker.com.
    param($appName, $appInfo)

    $script = Join-Path ([System.IO.Path]::GetTempPath()) 'install-docker.sh'
    try {
        Invoke-WebRequest 'https://get.docker.com' -OutFile $script -Verbose:$false
        Invoke-Shell -- sudo sh $script
    }
    finally {
        if (Test-Path $script) { Remove-Item $script -ErrorAction SilentlyContinue }
    }
}
