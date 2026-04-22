<#
.DESCRIPTION
    Installation helpers for various tools and applications.
.NOTES
    SPDX-License-Identifier: Unlicense
    Source: http://github.com/mrfootoyou/pstaskframework
#>
#Requires -Version 7.4
# spell:ignore psargs,winget,choco,8wekyb3d8bbwe,assumeyes,myapp

param()

Import-Module "$PSScriptRoot/../PSArgs" -Verbose:$false
Import-Module "$PSScriptRoot/../Secrets" -Verbose:$false
Import-Module "$PSScriptRoot/../BuildHelpers" -Verbose:$false

. "$PSScriptRoot/WellKnownApps.ps1"

function Get-WellKnownAppInfo {
    <#
    .DESCRIPTION
        Gets the metadata dictionary for a well-known app by name.
    .OUTPUTS
        [PSObject] with properties:
        - Name: the app name that was looked up
        - Info: the metadata dictionary for the app
    #>
    [CmdletBinding(PositionalBinding = $false)]
    [OutputType([PSObject])]
    param(
        # The name of the well-known app to get info for. Wildcards supported.
        # Defaults to '*'.
        [Parameter(Position = 0, ValueFromPipeline, ValueFromPipelineByPropertyName)]
        [SupportsWildcards()]
        [string[]] $Name = @('*')
    )
    process {
        switch ($Name) {
            { $_.IndexOfAny([char[]]'*?[') -ge 0 } {
                $n = $_
                $WellKnownApps.GetEnumerator() |
                Where-Object { $_.Key -like $n } |
                ForEach-Object {
                    [PSCustomObject]@{
                        Name = $_.Key
                        Info = $_.Value
                    }
                }
            }
            { $WellKnownApps.Contains($_) } {
                [PSCustomObject]@{
                    Name = $_
                    Info = $WellKnownApps[$_]
                }
            }
            default {
                Write-Error -Exception "App '$_' is not a well-known app." -CategoryActivity 'Get-WellKnownAppInfo'
            }
        }
    }
}

function refreshEnvironment {
    # only supported on Windows
    if (!$IsWindows) {
        return
    }

    # Refresh the PATH environment variable for the current session.
    $rawPath = (
        [System.Environment]::GetEnvironmentVariable('PATH', [System.EnvironmentVariableTarget]::Machine) + ';' +
        [System.Environment]::GetEnvironmentVariable('PATH', [System.EnvironmentVariableTarget]::User))

    $env:PATH = (
        $rawPath.Split(';') |
        Where-Object { $_ } |
        ForEach-Object { [System.Environment]::ExpandEnvironmentVariables($_.Trim()) } |
        Select-Object -Unique
    ) -join ';'
}

function installWinget {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', 'Get-AppxPackage')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', 'Add-AppxPackage')]
    param()
    $ErrorActionPreference = 'Stop'

    if (!$IsWindows) {
        throw 'Winget is not supported on this OS platform.'
    }

    if (Get-Command 'winget' -ErrorAction Ignore) {
        Write-Information "$($PSStyle.Foreground.Green)Winget is already installed.$($PSStyle.Reset)"
        return
    }

    # Winget (App Installer) is included by default in modern versions of Windows 10 and
    # later, but the App Installer package can sometimes become corrupted. Re-registering
    # the package can often fix issues with Winget without requiring a full reinstall.
    $packageName = 'Microsoft.DesktopAppInstaller_8wekyb3d8bbwe'
    $storeUrl = 'https://apps.microsoft.com/detail/9nblggh4nns1'

    if (!(Get-Command 'Get-AppxPackage' -ErrorAction Ignore)) {
        throw "Your version of Windows may not support Winget. Try installing 'App Installer' from the Microsoft Store: $storeUrl."
    }

    if (!(Get-AppxPackage | Where-Object PackageFamilyName -eq $packageName -ErrorAction Ignore)) {
        throw "Winget not found. Install 'App Installer' from the Microsoft Store: $storeUrl."
    }

    Write-Information "$($PSStyle.Foreground.Yellow)Winget not found, but 'App Installer' is present. Attempting to re-register the package to restore Winget functionality...$($PSStyle.Reset)"
    try {
        Add-AppxPackage -RegisterByFamilyName -MainPackage $packageName

        if (!(Get-Command 'winget' -ErrorAction Ignore)) {
            refreshEnvironment
            if (!(Get-Command 'winget' -ErrorAction Ignore)) {
                throw "Re-registered 'App Installer' package, but Winget is still not found. Maybe try opening a new terminal or restarting your computer?"
            }
        }

        Write-Information "$($PSStyle.Foreground.Green)Winget was successfully installed.$($PSStyle.Reset)"
    }
    catch {
        throw "Winget not found and failed to re-register 'App Installer' package: $_"
    }
}

function installChocolatey {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingInvokeExpression', '', Justification = 'Needed for chocolatey installation script.')]
    param()
    $ErrorActionPreference = 'Stop'

    if (!$IsWindows) {
        throw 'Chocolatey is not supported on this OS platform.'
    }

    if (Get-Command 'choco' -ErrorAction Ignore) {
        Write-Information "$($PSStyle.Foreground.Green)Chocolatey is already installed.$($PSStyle.Reset)"
        return
    }

    Write-Information "$($PSStyle.Foreground.Yellow)Attempting to install Chocolatey package manager...$($PSStyle.Reset)"
    try {
        # The ExecutionPolicy needs to be at least RemoteSigned
        if ((Get-ExecutionPolicy) -notin 'RemoteSigned', 'AllSigned', 'Bypass', 'Unrestricted') {
            # Try to set the ExecutionPolicy for the current process, but don't
            # fail if we can't.
            Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned -Force -ea Ignore
        }

        $ProgressPreference = 'SilentlyContinue'
        $resp = Invoke-WebRequest 'https://community.chocolatey.org/install.ps1' -Verbose:$false
        Invoke-Expression $resp.Content

        if (!(Get-Command 'choco' -ErrorAction Ignore)) {
            refreshEnvironment
            if (!(Get-Command 'choco' -ErrorAction Ignore)) {
                throw 'Chocolatey installation script ran, but Chocolatey (choco) is still not found. Maybe try opening a new terminal or restarting your computer?'
            }
        }

        Write-Information "$($PSStyle.Foreground.Green)Chocolatey was successfully installed.$($PSStyle.Reset)"
    }
    catch {
        throw "Failed to install Chocolatey: $_"
    }
}

function installAPT {
    param()
    $ErrorActionPreference = 'Stop'

    if (!$IsMacOS -and !$IsLinux) {
        throw 'APT (Advanced Package Tool) is not supported on this OS platform.'
        return
    }

    if (Get-Command 'apt-get' -ErrorAction Ignore) {
        Write-Information "$($PSStyle.Foreground.Green)APT (Advanced Package Tool) is already installed.$($PSStyle.Reset)"
        return
    }

    # Not gonna try it!
    throw 'APT (Advanced Package Tool) not found. Manually install APT and try again.'
}

function installDNF {
    param()
    $ErrorActionPreference = 'Stop'

    if (!$IsMacOS -and !$IsLinux) {
        throw 'DNF (Dandified Yum) is not supported on this OS platform.'
        return
    }

    if (Get-Command 'dnf' -ErrorAction Ignore) {
        Write-Information "$($PSStyle.Foreground.Green)DNF (Dandified Yum) is already installed.$($PSStyle.Reset)"
        return
    }

    # Not gonna try it!
    throw 'DNF (Dandified Yum) not found. Manually install DNF and try again.'
}

function installBrew {
    param()

    if (!$IsMacOS -and !$IsLinux) {
        throw 'Homebrew is not supported on this OS platform.'
    }

    if (Get-Command 'brew' -ErrorAction Ignore) {
        Write-Information "$($PSStyle.Foreground.Green)Homebrew is already installed.$($PSStyle.Reset)"
        return
    }

    # Not gonna attempt automated installation! Installation itself is easy enough, but it
    # requires some post-setup manual steps to integrate with the shell that I am not
    # comfortable automating.
    throw 'Homebrew (brew) not found. Manually install Homebrew from https://brew.sh/ and try again.'
}

function installWithPackageManager {
    param(
        # A list of apps to install using the specified package manager. Each has the app name
        # and the app metadata dictionary (Info) describing the PM installation details.
        [PSObject[]] $AppsToInstall,

        # The name of the installation method to use, e.g. 'winget', 'choco', 'apt', 'dnf',
        # 'brew', 'brew:linux', etc. This must correspond to a field in the app metadata
        # dictionary that describes how to install the app using the specified package manager.
        [string] $MethodName,

        # A user-friendly name for the package manager, used in logging messages.
        [string] $PackageManagerName,

        # A scriptblock that executes a package manager command with the given arguments.
        # Should support an optional switch parameter `-DoNotAppendArgs` to disable
        # automatic "silent" argument appending, and an optional parameter
        # `-AllowedExitCodes` to specify additional exit codes that should be treated as
        # success.
        [scriptblock] $Execute,

        # A scriptblock used to install a list of packages (the typical installation method
        # for most package managers).
        [scriptblock] $InstallPackages
    )
    $ErrorActionPreference = 'Stop'

    # String values are collected and installed in one batch call. Array and script
    # values are executed per-app to support custom command lines and logic.
    $packageIds = @()
    foreach ($app in $AppsToInstall) {
        $appName = $app.Name
        $appInfo = $app.Info
        $method = $appInfo[$MethodName]

        if ($method -is [string] -and $method) {
            $packageIds += $method
        }
        elseif ($method -is [string[]]) {
            Write-Verbose "Using $PackageManagerName with custom args to install '$appName'."
            & $Execute -- @method
        }
        elseif ($method -is [System.Collections.IDictionary]) {
            Write-Verbose "Using $PackageManagerName with dictionary args to install '$appName'."
            & $Execute @method
        }
        elseif ($method -is [scriptblock]) {
            Write-Verbose "Using $PackageManagerName with custom script to install '$appName'."
            & {
                $ErrorActionPreference = 'Stop'
                $ProgressPreference = 'SilentlyContinue'
                & $method $appName $appInfo $Execute
            }
        }
        else {
            throw "Unexpected installation type for '$appName'['$MethodName']: [$($null -eq $method ? '$null' : $method.GetType().FullName)]."
        }
    }
    if ($packageIds) {
        Write-Verbose "Using $PackageManagerName to install '$($packageIds -join ''', ''')'."
        & $InstallPackages -- @packageIds
    }
}

function installWithWinget {
    param(
        [PSObject[]] $AppsToInstall,
        [string] $MethodName = 'winget'
    )

    $wingetExec = {
        [CmdletBinding(PositionalBinding)]
        param(
            [Parameter(Mandatory, Position = 0, ValueFromRemainingArguments = $true)]
            [string[]] $PMArgs,
            [switch]$DoNotAppendArgs,
            [int[]]$AllowedExitCodes = @(
                0,
                0x8A15002B # No applicable update found
            )
        )
        if (!$DoNotAppendArgs) {
            if (!$PMArgs.where{ $_ -in '-h', '--silent' }) {
                $PMArgs += '--silent'
            }
            if ($PMArgs -notcontains '--force') {
                $PMArgs += '--force'
            }
            if ($PMArgs -notcontains '--accept-package-agreements') {
                $PMArgs += '--accept-package-agreements'
            }
            if ($PMArgs -notcontains '--accept-source-agreements') {
                $PMArgs += '--accept-source-agreements'
            }
        }

        Invoke-Shell -AllowedExitCodes $allowedExitCodes -- winget @PMArgs
        # Normalize to success so callers treat "no update" as non-fatal.
        $global:LASTEXITCODE = 0
    }

    $pmArgs = @{
        AppsToInstall      = $AppsToInstall
        MethodName         = $MethodName
        PackageManagerName = 'Winget'
        Execute            = $wingetExec
        InstallPackages    = { & $wingetExec -- install @args --exact --source winget }
    }
    installWithPackageManager @pmArgs
}

function installWithChocolatey {
    param(
        [PSObject[]] $AppsToInstall,
        [string] $MethodName = 'choco'
    )

    $chocoExec = {
        [CmdletBinding(PositionalBinding)]
        param(
            [Parameter(Mandatory, Position = 0, ValueFromRemainingArguments = $true)]
            [string[]] $PMArgs,
            [switch]$DoNotAppendArgs,
            [int[]]$AllowedExitCodes = @(
                0,
                2 # nothing to do, no packages outdated
            )
        )
        if (!$DoNotAppendArgs) {
            if (!$PMArgs.where{ $_ -in '-y', '--yes', '--confirm' }) {
                $PMArgs += '--yes'
            }
        }

        # Chocolatey requires elevated permissions
        if (Test-Administrator) {
            Invoke-Shell -AllowedExitCodes $allowedExitCodes -- choco @PMArgs
            $global:LASTEXITCODE = 0
        }
        else {
            # Use Start-Process to run Chocolatey as administrator.
            # We must escape the arguments using Win32 command line parsing
            # rules. See https://learn.microsoft.com/en-us/cpp/c-language/parsing-c-command-line-arguments
            $cmdPath = Assert-AppExists 'choco' -PassThru
            $cmdArgs = switch ($PMArgs) {
                { !$_ } { '""' }
                { $_ -match '[ \t"]' } {
                    # 1. Escape literal backslashes immediately preceding a literal quote
                    # 2. Escape literal quotes
                    # 3. Escape trailing backslashes before wrapping in quotes
                    # 4. Wrap in quotes
                    '"{0}"' -f ($_ `
                            -replace '(\\+)(?=")', '$1$1' `
                            -replace '"', '\"' `
                            -replace '(\\+)$', '$1$1')
                }
                default { $_ }
            }

            Write-Information "$($PSStyle.Foreground.Yellow)Running Chocolatey as administrator. Expect a prompt.$($PSStyle.Reset)"

            # simulate Invoke-Shell behavior
            $cmdText = Protect-Secret "$(ConvertTo-PSString $cmdPath) $($cmdArgs -join ' ')"
            Write-Information "$($PSStyle.Dim)>> $cmdText$($PSStyle.Reset)"

            $ps = Start-Process $cmdPath -ArgumentList $cmdArgs -Verb RunAs -Wait -PassThru
            if ($ps.ExitCode -notin $allowedExitCodes) {
                $global:LASTEXITCODE = $ps.ExitCode
                throw "Command failed with exit code $global:LASTEXITCODE ($cmdText)."
            }
        }
    }

    $pmArgs = @{
        AppsToInstall      = $AppsToInstall
        MethodName         = $MethodName
        PackageManagerName = 'Chocolatey'
        Execute            = $chocoExec
        InstallPackages    = { & $chocoExec -- upgrade @args }
    }
    installWithPackageManager @pmArgs
}

function installWithAPT {
    param(
        [PSObject[]] $AppsToInstall,
        [string] $MethodName = 'apt'
    )
    $aptUpdated = $false

    $aptExec = {
        [CmdletBinding(PositionalBinding)]
        param(
            [Parameter(Mandatory, Position = 0, ValueFromRemainingArguments = $true)]
            [string[]] $PMArgs,
            [switch]$DoNotAppendArgs,
            [int[]]$AllowedExitCodes = @(0)
        )
        if (!$DoNotAppendArgs) {
            if (!$PMArgs.where{ $_ -in '-y', '--yes', '--assume-yes' }) {
                $PMArgs += '--yes'
            }
        }

        if (!$aptUpdated) {
            Invoke-Shell -- sudo apt-get update -y
            # Scriptblock closures in PowerShell require explicit variable mutation.
            (Get-Variable -Name aptUpdated).Value = $true
        }

        Invoke-Shell -AllowedExitCodes $AllowedExitCodes -- sudo apt-get @PMArgs
    }

    $pmArgs = @{
        AppsToInstall      = $AppsToInstall
        MethodName         = $MethodName
        PackageManagerName = 'APT'
        Execute            = $aptExec
        InstallPackages    = { & $aptExec -- install @args }
    }
    installWithPackageManager @pmArgs
}

function installWithDNF {
    param(
        [PSObject[]] $AppsToInstall,
        [string] $MethodName = 'dnf'
    )
    $ErrorActionPreference = 'Stop'

    $dnfExec = {
        [CmdletBinding(PositionalBinding)]
        param(
            [Parameter(Mandatory, Position = 0, ValueFromRemainingArguments = $true)]
            [string[]] $PMArgs,
            [switch]$DoNotAppendArgs,
            [int[]]$AllowedExitCodes = @(0)
        )
        if (!$DoNotAppendArgs) {
            if (!$PMArgs.where{ $_ -in '-y', '--assumeyes' }) {
                $PMArgs += '-y'
            }
        }

        Invoke-Shell -AllowedExitCodes $AllowedExitCodes -- sudo dnf @PMArgs
    }

    $pmArgs = @{
        AppsToInstall      = $AppsToInstall
        MethodName         = $MethodName
        PackageManagerName = 'DNF'
        Execute            = $dnfExec
        InstallPackages    = { & $dnfExec -- install @args }
    }
    installWithPackageManager @pmArgs
}

function installWithBrew {
    param(
        [PSObject[]] $AppsToInstall,
        [string] $MethodName = 'brew'
    )
    $ErrorActionPreference = 'Stop'
    $brewUpdated = $false

    $brewExec = {
        [CmdletBinding(PositionalBinding)]
        param(
            [Parameter(Mandatory, Position = 0, ValueFromRemainingArguments = $true)]
            [string[]] $PMArgs,
            [switch]$DoNotAppendArgs,
            [int[]]$AllowedExitCodes = @(0)
        )
        if (!$DoNotAppendArgs) {
            if (!$PMArgs.where{ $_ -in '-q', '--quiet' }) {
                $PMArgs += '--quiet'
            }
        }

        if (!$brewUpdated) {
            Invoke-Shell -- brew update
            # Scriptblock closures in PowerShell require explicit variable mutation.
            (Get-Variable -Name brewUpdated).Value = $true
        }

        Invoke-Shell -AllowedExitCodes $AllowedExitCodes -- brew @PMArgs
    }

    $brewInstallPackages = {
        # Homebrew doesn't have an "upgrade or install" command, so we have to
        # check if each package is already installed and choose the command accordingly.
        foreach ($packageId in $args) {
            $null = Invoke-Shell -InformationAction Ignore -ErrorAction Ignore -- brew list $packageId 2>&1
            $action = $global:LASTEXITCODE -eq 0 ? 'upgrade' : 'install'

            & $brewExec -- $action $packageId

            # Reset exit code logic if the error was just "already up to date"
            if ($global:LASTEXITCODE -ne 0) {
                # Check if it was a real failure or just Homebrew being pedantic
                $saved = $global:LASTEXITCODE
                $null = Invoke-Shell -InformationAction Ignore -ErrorAction Ignore -- brew list $packageId 2>&1
                if ($global:LASTEXITCODE -ne 0) {
                    $global:LASTEXITCODE = $saved # real failure, keep the error code
                }
            }
        }
    }

    $pmArgs = @{
        AppsToInstall      = $AppsToInstall
        MethodName         = $MethodName
        PackageManagerName = 'Homebrew'
        Execute            = $brewExec
        InstallPackages    = $brewInstallPackages
    }
    installWithPackageManager @pmArgs
}

function installWithScript {
    param(
        [PSObject[]] $AppsToInstall,
        [string] $MethodName = 'script'
    )

    # get platform from the method. E.g. "script:linux" -> "linux", "script" -> "all platforms", etc.
    $platform = ($MethodName -split ':', 2)[1]
    $platform = $platform ? "$platform-specific" : 'platform-agnostic'

    foreach ($app in $AppsToInstall) {
        $appName = $app.Name
        $appInfo = $app.Info
        $method = $appInfo[$MethodName]
        if ($method -is [scriptblock]) {
            Write-Verbose "Using $platform install script for '$appName'."
            & {
                $ErrorActionPreference = 'Stop'
                $ProgressPreference = 'SilentlyContinue'
                & $method $appName $appInfo
            }
        }
        else {
            throw "Unexpected installation type for '$appName'['$MethodName']: [$($null -eq $method ? '$null' : $method.GetType().FullName)]."
        }
    }
}

function Get-PackageManager {
    <#
    .DESCRIPTION
        Detects the supported package managers installed on the system
        and returns their names (e.g. winget, choco, apt, dnf, brew,
        brew:linux, brew:macos).
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        # If specified, returns all supported package managers, not just the ones currently
        # installed on the system.
        [switch] $AllSupported
    )

    $supportedPackageManagers = [string[]] @(
        if ($IsWindows) { 'winget', 'choco' }
        if ($IsLinux) { 'apt', 'dnf', 'brew', 'brew:linux' }
        if ($IsMacOS) { 'brew', 'brew:macos' }
    )

    if ($AllSupported) {
        Write-Output $supportedPackageManagers
        return
    }

    foreach ($pm in $supportedPackageManagers) {
        if (Get-Command $pm -ErrorAction Ignore) {
            $pm
            if ($pm -eq 'brew' -and $IsMacOS) { 'brew:macos' }
            if ($pm -eq 'brew' -and $IsLinux) { 'brew:linux' }
        }
    }
}

function Install-PackageManager {
    <#
    .DESCRIPTION
        Attempts to install a supported package manager on the system.

        See 'Get-PackageManager -AllSupported' for the list of supported package managers.
    .OUTPUTS
        The names of the package managers that were [already] installed.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        # The package manager(s) to install. Use 'any' to install any supported
        # package manager.
        [Parameter(Mandatory, Position = 0)]
        [ValidateSet('any', 'winget', 'choco', 'apt', 'dnf', 'brew', 'brew:linux', 'brew:macos')]
        [string[]] $PackageManager
    )

    $PackageManager = @(
        $PackageManager |
        ForEach-Object {
            if ($_ -eq 'brew:macos' -and $IsMacOS) { 'brew' }
            elseif ($_ -eq 'brew:linux' -and $IsLinux) { 'brew' }
            else { $_ }
        } |
        Select-Object -Unique
    )

    $installed = @{}

    function install([string]$pm) {
        Write-Information "Installing $pm if needed..."
        switch ($pm) {
            'winget' { installWinget }
            'choco' { installChocolatey }
            'apt' { installAPT }
            'dnf' { installDNF }
            'brew' { installBrew }
            default { throw "Unsupported package manager: $_." }
        }

        # success
        $installed[$pm] = $true
        if ($pm -eq 'brew' -and $IsMacOS) { $installed['brew:macos'] = $true }
        if ($pm -eq 'brew' -and $IsLinux) { $installed['brew:linux'] = $true }
    }

    foreach ($pm in $PackageManager) {
        try {
            if ($pm -eq 'any') { continue } # see below
            install $pm
        }
        catch {
            # Keep trying remaining candidates; this cmdlet reports partial failures.
            Write-Error -ErrorRecord $_ -CategoryActivity 'Install-PackageManager'
        }
    }

    if (!$installed.Count -and $PackageManager -contains 'any') {
        # just the ones not already tried...
        $otherSupportedPMs = Get-PackageManager -AllSupported | Where-Object { $_ -notin $PackageManager }
        foreach ($pm in $otherSupportedPMs) {
            try {
                install $pm
                break
            }
            catch {
                Write-Warning $_.Exception.Message
            }
        }
        if (!$installed.Count) {
            Write-Error -Exception "Failed to install any supported package manager (tried $($otherSupportedPMs -join ', '))." -CategoryActivity 'Install-PackageManager'
        }
    }

    Write-Output $installed.Keys
}

function Install-RequiredApp {
    <#
    .DESCRIPTION
        Installs required applications using the best available package manager or
        installation method for each application.

        The -AppsToInstall parameter is a dictionary mapping application names to
        "installation information". The installation information describes various
        aspects of the app, including various installation methods, a command to test
        if the app is already installed, and any other relevant data. The specific structure
        of the installation information is flexible, but it must include at least one
        supported installation method (either a package manager or a script block).

        If an app's installation information is $null, it indicates that the app is in the
        $WellKnownApps collection and you want to use the default installation methods from
        there rather than specifying custom or duplicate installation information.

        Installation Information:
        ------------------------
        The installation information for each app can include any of the following properties:

        - `executable`: The name or path of the installed executable. Defaults to the app name.
          Used by the installation logic to test if the app is already installed. Use an empty
          string to indicate that no such executable is available (perhaps the app is not in the
          PATH or the path is unpredictable).
        - `isUpToDate`: An optional script block to test if the app is installed and up-to-date.
          The script block should return $true if the app is up-to-date and $false otherwise.
          If not specified, it is assumed to _not_ be up-to-date.
          This script block will only be called if the `executable` (see above) exists,
          or is an empty string.
        - `version`: An optional scriptblock that returns the installed version when invoked.
        - One or more installation methods where the property name is the installation method
          and the property value describes the details for that installation method.
          Supported installation methods:
          - Package manager (winget, chocolatey, apt, dnf, brew, brew:linux, brew:macos).
            See below for package manager installation details.
          - 'script:<platform>' where platform is 'windows', 'linux', or 'macos'. A script block
            that performs the installation for the specified platform. The script block will be
            passed two arguments: the app name and the app info dictionary.
          - 'script' A script block that performs the installation on any platform. The script
            block will be passed two arguments: the app name and the app info dictionary.


        Package Manager Installation:
        ----------------------------
        For package manager installation methods, the value can be one of the following:

        - A single string value representing the package manager-specific package/app id.
        - An array of strings specifying the package manager-specific arguments to use.
          These will be passed to the package manager without modification. "Silent"
          arguments will be automatically appended.
        - A dictionary with the following properties:
          - `PMArgs` - an array of strings specifying the package manager-specific arguments
            to use (similar to the array of strings above).
          - `DoNotAppendArgs` - a boolean value that can be set to prevent automatic
            inclusion of "silent" arguments.
          - `AllowedExitCodes` - an array of integers specifying which exit codes should be
            treated as successful.
        - A scriptblock to perform the installation. This scriptblock will be passed three
          arguments: the app name, the app info dictionary, and a script block used to
          invoke the package manager command. For example, a `winget` scriptblock will
          receive a third argument ($invokeWinget) that can be used like
          `& $invokeWinget -- ...` to run the equivalent `winget ...` command with the
          benefits of error handling and silent installation. This scriptblock argument
          accepts an optional `-AllowedExitCodes` parameter that can be used to specify
          which exit codes should be treated as successful. You can also use the
          `-DoNotAppendArgs` switch to prevent "silent" arguments from being automatically
          added to the package manager command.

        For apps with multiple package managers defined on systems with multiple package
        managers available, the specific package manager used to install the app is based
        on one of two factors:
        1. If the app info is an ordered dictionary ([ordered]@{ }), then the PMs are
           prioritized in the order specified in the app info, allowing the caller to
           specify their preferred PM for a given app.
        2. Otherwise the installation logic prioritizes package managers in the order returned
           by Get-PackageManager. For example, on Windows systems, Winget will be preferred
           when both Winget and Chocolatey are available.

    .EXAMPLE
        $apps = @{
            'git' = $null  # will use install info from the well-known apps list
            'myapp' = [ordered]@{
                executable     = 'my-app'
                isUpToDate     = { (my-app --version) -like '2.*' }
                version        = { my-app --version }
                winget         = 'Company.MyApp'
                choco          = @('upgrade', 'myapp', '--params="/arg1 /arg2"')
                dnf            = @{
                                    PMArgs = @('install', 'myapp@2.0.0', '--not-quiet')
                                    DoNotAppendArgs = $true
                                    AllowedExitCodes = @(0, 1)
                                 }
                'script:linux' = {
                        param($appName, $appInfo)
                        # ...custom install script...
                    }
                'brew:macos'   = {
                        param($appName, $appInfo, $invokeBrew)
                        & $invokeBrew -AllowedExitCodes 0,1,2 -- install myapp ...
                    }
                data           = @{...} # custom data for use in scriptblock(s) via `$appInfo.data`.
            }
        }
        Install-RequiredApp -AppsToInstall $apps

        This example demonstrates the various installation methods. For "git", it will look
        up the installation information in the well-known apps list. For "myapp", it will use
        the provided installation information: If Winget is available, it will run `winget
        install Company.MyApp --exact --source winget`. If Chocolatey is available but Winget
        is not, it will run `choco upgrade myapp --params="/arg1 /arg2" --yes`. On Linux
        systems, if DNF is available, it will run `dnf install myapp@2.0.0 --extra-args -y`.
        If DNF is not available, it will execute the custom script block ("script:linux").
        On macOS systems with Homebrew available, it will execute the custom Homebrew script
        block. Otherwise it will fail since there is no supported installation method available.
    #>
    [CmdletBinding(PositionalBinding = $false)]
    param(
        # A dictionary mapping app names to installation information. See description for details.
        [Parameter(Mandatory, Position = 0)]
        [System.Collections.IDictionary] $AppsToInstall,

        # If specified, the function will attempt to install required package managers.
        [switch] $InstallPackageManagers
    )

    # Validate that all apps have installation information, either provided
    # directly or via the well-known apps list...
    $copiedAppsToInstall = [ordered]@{}
    foreach ($appName in $AppsToInstall.Keys) {
        $appInfo = $AppsToInstall[$appName] ?? $WellKnownApps[$appName]
        if ($null -eq $appInfo) {
            throw "No install information found for '$appName'. Please provide installation details or ensure it's defined in the well-known apps list."
        }
        if ($appInfo -isnot [System.Collections.IDictionary]) {
            throw "Invalid install information for '$appName'. Expected a dictionary."
        }

        # check if the app needs to be installed...
        $executable = $appInfo['executable'] ?? $appName
        if ($executable -isnot [string]) { throw "Invalid 'executable' value for '$appName'. Expected a string." }

        $appExists = $executable -and (Get-Command $executable -CommandType Application -ErrorAction Ignore)
        $isUpToDateTest = $appInfo['isUpToDate'] ?? { $appExists }
        if ($isUpToDateTest -isnot [scriptblock]) { throw "Invalid 'isUpToDate' value for '$appName'. Expected script block." }

        if ($executable -eq '' -or $appExists) {
            if (& $isUpToDateTest $appName $appInfo) {
                $version = $appInfo['version'] -is [scriptblock] `
                    ? (& $appInfo['version'] $appName $appInfo) `
                    : $appInfo['version']
                Write-Information "$($PSStyle.Foreground.Green)$appName $version is installed.$($PSStyle.Reset)"
                continue
            }
        }

        $copiedAppsToInstall[$appName] = $appInfo
    }
    $AppsToInstall = $copiedAppsToInstall

    # Get all available package managers.
    # If we're allowed to install package managers, then we'll assume all supported
    # package managers are available, since we'll try to install any that are missing.
    # Otherwise, we can only choose from the list of available package managers.
    $packageManagers = @(Get-PackageManager)
    if ($InstallPackageManagers) {
        # include all supported PMs, but prioritize installed PMs...
        $packageManagers += @(Get-PackageManager -AllSupported)
        $packageManagers = $packageManagers | Select-Object -Unique
    }

    # Get all supported installation methods...
    $installationMethods = $packageManagers
    if ($IsWindows) { $installationMethods += 'script:windows' }
    if ($IsLinux) { $installationMethods += 'script:linux' }
    if ($IsMacOS) { $installationMethods += 'script:macos' }
    $installationMethods += 'script'

    # Choose an installation method for every selected app...
    $installMethodToApps = @{}
    $allResolved = $false
    while (!$allResolved) {
        # Re-resolve from scratch each iteration so failed package managers can be
        # removed and alternate methods selected deterministically.
        $installMethodToApps.Clear()
        $unresolvedApps = @()

        foreach ($appName in $AppsToInstall.Keys) {
            $appInfo = $AppsToInstall[$appName]

            # if the app info is an ordered dictionary, preserve that order when checking
            # for installation methods. Otherwise, use the $installationMethods order...
            if ($appInfo -is [System.Collections.Specialized.IOrderedDictionary]) {
                $preferredMethod = $appInfo.Keys.where({ $_ -in $installationMethods }, 'First')
            }
            else {
                $preferredMethod = $installationMethods.where({ $_ -in $appInfo.Keys }, 'First')
            }

            if ($preferredMethod) {
                $data = [PSCustomObject]@{
                    Name = $appName
                    Info = $appInfo
                }
                $installMethodToApps[$preferredMethod.ToLower()] += , $data
            }
            else {
                $unresolvedApps += $appName
            }
        }

        if ($unresolvedApps) {
            Write-Error -Exception "No supported installation method for $($unresolvedApps -join ', '). Tried: $($packageManagers -join ', ')." -CategoryActivity 'Install-RequiredApp'
            break
        }

        $allResolved = $true

        # Install required PMs. If any fail, remove that PM from the available
        # installation methods and re-resolve...
        $installedPMs = @(Get-PackageManager)
        $missingPMs = $installMethodToApps.Keys.where{
            $_ -in $packageManagers -and $_ -notin $installedPMs
        }
        foreach ($pm in $missingPMs) {
            if (!(Install-PackageManager -PackageManager $pm -ea Continue)) {
                Write-Host 'Attempting alternative installation method...' -ForegroundColor Yellow
                # Drop only the failing method, then restart resolution loop.
                $installationMethods = $installationMethods.where{ $_ -ne $pm }
                $allResolved = $false
                break
            }
        }
    }

    # Install apps...
    foreach ($installationMethod in $installMethodToApps.Keys) {
        $apps = $installMethodToApps[$installationMethod]
        switch -regex ($installationMethod) {
            '^winget$' { installWithWinget $apps $_ }
            '^choco$' { installWithChocolatey $apps $_ }
            '^apt$' { installWithAPT $apps $_ }
            '^dnf$' { installWithDNF $apps $_ }
            '^brew(:.*)?$' { installWithBrew $apps $_ }
            '^script(:.*)?$' { installWithScript $apps $_ }
            default {
                Write-Error -Exception "Unsupported installation method: $_." -CategoryActivity 'Install-RequiredApp'
            }
        }
    }
}

function Install-PowerShellModule {
    <#
    .DESCRIPTION
        Checks if the specified PowerShell module(s) are installed with at least
        the given minimum version. If any module is missing or does not meet the version
        requirement, it will be installed from the PowerShell Gallery.
    .EXAMPLE
        $modules = @{
            'Pester' = '5.1.0'
            'PSScriptAnalyzer' = '1.25.0'
        }
        Install-PowerShellModule -ModuleVersions $modules

        This example checks if Pester v5.1.0 or higher and PSScriptAnalyzer v1.25.0
        or higher are installed, and installs them if not.
    .NOTES
        Use the -InformationAction parameter to see status messages.
    #>
    [CmdletBinding()]
    param(
        # A dictionary where the keys are module names and values are the minimum required
        # versions (as [version] compatible values).
        [Parameter(Mandatory)]
        [System.Collections.IDictionary] $ModuleVersions,

        # The scope for installing modules. Defaults to 'CurrentUser' to avoid requiring
        # administrator privileges.
        [ValidateSet('CurrentUser', 'AllUsers')]
        [string]$Scope = 'CurrentUser'
    )

    function tryGetModule([string]$Name, [version]$MinimumVersion) {
        Get-Module -Name $Name -ListAvailable -ea Ignore |
        Where-Object Version -ge $MinimumVersion
    }

    function installIfNeeded([string]$Name, [version]$MinimumVersion, $Scope) {
        if (!($installed = tryGetModule @PSBoundParameters)) {
            Write-Information "$($PSStyle.Foreground.Yellow)Installing $Name $MinimumVersion (or greater) in $Scope scope...$($PSStyle.Reset)"
            Install-Module @PSBoundParameters -Force

            # Re-query after install to verify the expected minimum version is visible.
            if (!($installed = tryGetModule @PSBoundParameters)) {
                throw "Failed to install $Name $MinimumVersion (or greater) in $Scope scope."
            }
        }
        Write-Information "$($PSStyle.Foreground.Green)$Name $($installed.Version) is installed.$($PSStyle.Reset)"
    }

    try {
        foreach ($module in $ModuleVersions.Keys) {
            installIfNeeded -Name $module -MinimumVersion $ModuleVersions[$module] -Scope $Scope
        }
    }
    catch {
        Write-Error -ErrorRecord $_ -CategoryActivity 'Install-PowerShellModule'
    }
}

$exportModuleMemberParams = @{
    Function = @(
        'Install-PowerShellModule'
        'Get-WellKnownAppInfo'
        'Install-PackageManager'
        'Get-PackageManager'
        'Install-RequiredApp'
    )
}

Export-ModuleMember @exportModuleMemberParams
