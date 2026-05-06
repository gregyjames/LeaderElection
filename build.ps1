<#
.SYNOPSIS
    A lightweight task runner for common .NET Core repository tasks.
.DESCRIPTION
    This script defines a set of common .NET Core repository tasks that can be executed from
    the command line.

    See the task definitions below for more details on each task and how to use them.

    PowerShell 7.4 or later is required to use this script. See https://aka.ms/install-powershell.
.EXAMPLE
    PS> ./build.ps1

    Executes the default 'build' task, including all of its dependencies (e.g. 'restore').
.EXAMPLE
    PS> ./build.ps1 list

    Lists all available tasks.
.EXAMPLE
    PS> ./build.ps1 clean -noDeps

    Executes the 'clean' task without executing its dependencies.
.EXAMPLE
    PS> ./build.ps1 help clean -full

    Displays the help documentation for the 'clean' task.
    Run `./build.ps1 help help -full` for more information on the help system.
.NOTES
    SPDX-License-Identifier: Unlicense
    Source: http://github.com/mrfootoyou/pstaskframework
#>
#Requires -Version 7.4
# spell:ignore dont,winget,choco,opencover,reportgenerator,reportgenerator-globaltool

[Diagnostics.CodeAnalysis.SuppressMessage('PSReviewUnusedParameter', '')]
[Diagnostics.CodeAnalysis.SuppressMessage('PSAvoidGlobalVars', 'global:LastTaskContext')]
[CmdletBinding(PositionalBinding = $false)]
param (
    # The name of the task(s) to execute.
    [Parameter(Position = 0)]
    [ValidateSet(
        'list',
        'help',
        'bootstrap',
        'version',
        'updateTools',
        'restoreTools',
        'initGit',
        'restore',
        'updatePackages',
        'updateRepo',
        'clean',
        'format',
        'build',
        'test',
        'coverage',
        'package',
        'push',
        'runExample'
    )]
    [string[]] $TaskName = @('build'),

    # The build configuration to use when executing tasks that support it (e.g. 'build', 'test').
    # Defaults to 'debug'.
    [ValidateSet('debug', 'release')]
    [string] $Configuration = 'debug',

    # The version to use when executing tasks that support it (e.g. 'build', 'package').
    [ValidateNotNullOrEmpty()]
    [string] $Version,

    # When specified, dependencies of the task(s) will not be executed.
    # Default is execute all dependencies (and their dependencies).
    [Alias("noDeps")]
    [switch] $SkipDependencies,

    # Receives task-specific arguments for the _single task_ specified in -TaskName.
    [Parameter(ValueFromRemainingArguments, DontShow)]
    [ValidateNotNull()]
    [object[]] $TaskArgs = @()
)
# Initialize some default PowerShell preferences...
$ErrorActionPreference = 'Stop'     # throw exception on any unhandled error
$InformationPreference = 'Continue' # display informational messages

# Initialize some repository variables...
$RepoRoot = $PSScriptRoot # assumes this script is located in the repo root
$ScriptsDir = Convert-Path "$RepoRoot/scripts"

# Import and initialize the PSTaskFramework...
Import-Module "$ScriptsDir/PSTaskFramework" -Verbose:$false
$TaskContext = Initialize-TaskFramework

####################################################################################
# Define shared variables and functions...
####################################################################################


####################################################################################
# Define all tasks
# - Tasks will execute in the order they are defined below, unless they have
#   dependencies, in which case the dependencies will always be executed first.
# - The task's working directory is the folder containing this script.
# - Tasks can assign values to script-scope variables using the `$script:` modifier.
####################################################################################
#region Task definitions

# Add the default list and help tasks...
Add-TaskFrameworkDefaultTasks list, help

Task bootstrap -desc 'Installs required tools' {
    <#
    .DESCRIPTION
        Bootstraps the repository by installing required tools.

        Required tools include:
        - Git (probably already installed).
        - .NET SDK
        - Docker-API compatible container runtime for integration tests.
        - PowerShell 7.4 or later (assumed to be already be installed).
    #>
    param()
    Import-Module InstallHelpers -Verbose:$false

    $appsToInstall = [ordered]@{
        'git'           = $null # well-known app
        'dotnet-sdk-10' = $null # well-known app
        'docker'        = $null # well-known app
        'powershell'    = $null # well-known app
    }
    Install-RequiredApp $appsToInstall -InstallPackageManagers
}

Task version -desc 'Display tool versions' {
    [PSCustomObject]@{
        '.NET SDK'    = Invoke-Shell -InformationAction Ignore -- dotnet --version
        'PowerShell'  = $PSVersionTable.PSVersion
        'OS Platform' = "$($PSVersionTable.OS) ($($PSVersionTable.Platform))"
        'RepoRoot'    = $RepoRoot
    } | Format-List
}

Task clean -desc 'Clean the repository' -DependsOn version {
    <#
    .DESCRIPTION
        Cleans the repository using 'git clean'. By default it will run in interactive mode,
        prompting the user to confirm which files to delete. To skip the confirmation prompt,
        use the -Force switch.

        By default this uses 'git clean -X' to remove all untracked files that are
        ignored by git (e.g. build outputs, .vs folders, etc). This is typically safer since
        it leaves behind untracked files that are _not_ ignored by git, such as new source files.

        If you want to remove all untracked files, including those not ignored by git, use
        the -Pristine switch to run 'git clean -x' instead.
    #>
    param(
        # If specified, will run 'git clean -x' instead of 'git clean -X'
        [switch]$Pristine,
        # If specified, will skip the confirmation prompt and run 'git clean' with the -force option.
        [switch]$Force
    )
    $cleanArgs = @(
        '-d' # remove untracked directories in addition to untracked files
        ($Pristine ? '-x' : '-X')
        ($Force ? '--force' : '--interactive')
        '--exclude=.env' # never delete .env files since they often contain secrets
    )
    Invoke-Shell -- git clean @cleanArgs
}

Task updateTools -desc 'Update .NET tools' -DependsOn version {
    Invoke-Shell -- dotnet tool update --all
    Invoke-Shell -- dotnet tool update --all --global
}

Task restoreTools -desc 'Restore .NET dependencies' -DependsOn version {
    Invoke-Shell -- dotnet tool restore
}

Task initGit -desc 'Initialize Git repository' -DependsOn restoreTools {
    <#
    .DESCRIPTION
        Initializes Git repository hooks using Husky.

        Husky.net should be installed as a .NET local tool:
          `dotnet tool install husky`
        See https://alirezanet.github.io/Husky.Net/
    #>
    Invoke-Shell -- dotnet husky install
}

Task restore -desc 'Restore .NET dependencies' -DependsOn restoreTools {
    Invoke-Shell -- dotnet restore
}

Task updatePackages -desc 'Update .NET packages' -DependsOn version {
    <#
    .DESCRIPTION
        Updates .NET project dependencies using the dotnet-outdated-tool.
        See https://github.com/dotnet-outdated/dotnet-outdated

        If the dotnet-outdated-tool is not already installed, it will be
        installed as a .NET global tool:
          `dotnet tool install --global dotnet-outdated-tool`
    #>
    param(
        # The update mode. Default is 'Prompt' to prompt for each package update.
        # Use 'Auto' to automatically update all packages.
        [ValidateSet('Auto', 'Prompt')]
        [string] $UpdateMode = 'Prompt',
        # A list of package IDs to ignore when updating. This is useful for packages
        # that should not be updated automatically.
        [string[]] $IgnorePackages = @('JunitXml.TestLogger')
    )
    if (!(dotnet tool list --global | Select-String 'dotnet-outdated-tool')) {
        Write-Host 'Installing dotnet-outdated-tool...'
        Invoke-Shell -- dotnet tool install --global dotnet-outdated-tool
    }
    $outdatedArgs = @(
        "--upgrade:$UpdateMode"
        $IgnorePackages.foreach{ "--exclude:$_" }
    )
    Invoke-Shell -- dotnet outdated @outdatedArgs
}

Task updateRepo -desc 'Update the repository tools and packages' -DependsOn bootstrap, updateTools, updatePackages, initGit {
    # This is a aggregate task that runs all tasks to keep the repository up to date.
}

Task format -desc 'Format the code' -DependsOn restoreTools {
    <#
    .DESCRIPTION
        Formats the code using the CSharpier .NET tool.

        CSharpier should be installed as a .NET local tool:
          `dotnet tool install csharpier`
        See https://github.com/belav/csharpier.

        By default, it will format all code files in the repository. You can specify
        a subset of files to format using the -Path parameter.

        When the -DryRun switch is specified, it will check if the code is formatted
        correctly without making any changes. This is useful for CI checks.
    #>
    param(
        # An optional array of file or directory paths to format. If not specified, all
        # C# files in the repository will be formatted.
        [string[]] $Path = @('.'),
        # When specified, will check if the code is formatted correctly without making
        # any changes. This is useful for CI checks.
        [switch] $DryRun
    )
    $csharpierArgs = @(
        ($DryRun ? 'check' : 'format')
        $Path
    )
    Invoke-Shell -- dotnet csharpier @csharpierArgs
}

Task build -desc 'Build the solution' -dependsOn restore {
    <#
    .DESCRIPTION
        Builds the solution using 'dotnet build'.

        The build configuration can be specified using the -Configuration parameter (e.g.
        'debug' or 'release'). A build version can be specified using the -Version parameter.
    .EXAMPLE
        PS> ./build.ps1 build

        Builds the debug configuration of the solution.
    .EXAMPLE
        PS> ./build.ps1 build -Configuration Release -Version 1.2.3

        Builds the release configuration assigning it version 1.2.3.
    #>
    param()
    $buildArgs = @(
        '--configuration', $Configuration
        '--no-restore'
        if ($Version) { "-p:Version=$Version" }
    )
    Invoke-Shell -- dotnet build @buildArgs
}

Task test -desc 'Run tests' -dependsOn build {
    <#
    .DESCRIPTION
        Runs tests using 'dotnet test'.

        By default, it will run all tests in the repository. You can run a subset
        of tests using the -TestFilter parameter.
    .EXAMPLE
        PS> ./build.ps1 test -TestFilter "PartialTestName"

        Runs only tests with names that contain "PartialTestName".
    .EXAMPLE
        PS> ./build.ps1 test -TestFilter "Kind=Integration"

        Runs only tests with the [Trait("Kind", "Integration")] attribute.
    .EXAMPLE
        PS> ./build.ps1 test -TestFilter "Kind!=Integration"

        Runs only tests without the [Trait("Kind", "Integration")] attribute.
    #>
    param(
        # An optional filter expression to select which tests to run. This is passed
        # directly to 'dotnet test --filter'.
        [string]$TestFilter
    )
    $testArgs = @(
        '--configuration', $Configuration
        '--no-build'
        if ($TestFilter) { '--filter', $TestFilter }
    )
    Invoke-Shell -- dotnet test @testArgs
}

Task coverage -desc 'Run tests with code coverage' -dependsOn restore {
    <#
    .DESCRIPTION
        Runs all tests to generate coverage data in OpenCover format. The reports
        will be named "coverage.opencover.xml" in each test project's source directory.

        The coverage reports are then aggregated and converted into an HTML report using the
        dotnet-reportgenerator-globaltool. By default, the HTML report is output to the
        "artifacts/coverage" directory, but you can specify a different output directory using
        the -OutputDir parameter.

        Finally, the HTML report (if created) will be automatically opened in the default
        browser unless the -DoNotOpenReport switch is specified.
    .EXAMPLE
        PS> ./build.ps1 coverage

        Generates an HTML code coverage report and opens it in the default browser.
    .EXAMPLE
        PS> ./build.ps1 coverage -OutputDir ./coverage-report -ReportType Html,lcov,opencover -DoNotOpenReport

        Generates an HTML, lcov, and OpenCover code coverage report in the "./coverage-report"
        directory and does not open the HTML report.
    #>
    param(
        # The output directory for the coverage report. Defaults to "artifacts/coverage".
        [string] $OutputDir = './artifacts/coverage',
        # If specified, the output directory will not be cleaned before generating the report.
        [switch] $DoNotCleanOutputDir,
        # The type(s) of report to generate. Defaults to 'Html'.
        # See https://github.com/danielpalme/ReportGenerator for supported report types.
        [string[]] $ReportType = @('Html'),
        # If specified, the generated Html report will not be opened in the default browser.
        [switch] $DoNotOpenReport
    )

    # Ensure the report generator tool is installed...
    if (!(dotnet tool list --global | Select-String 'dotnet-reportgenerator-globaltool')) {
        Write-Host 'Installing dotnet-reportgenerator-globaltool...'
        Invoke-Shell -- dotnet tool install --global dotnet-reportgenerator-globaltool
    }

    # Remove existing coverage reports to ensure that the report only contains
    # data from the current test run...
    $coverageReports = './tests/**/coverage.opencover.xml'
    Remove-Item $coverageReports -ErrorAction Ignore

    # Ensure the output directory for the final report exists and is empty (unless
    # -DoNotCleanOutputDir is specified).
    if (!(Test-Path $OutputDir)) {
        $null = New-Item $OutputDir -ItemType Directory -Force
    }
    if (!$DoNotCleanOutputDir) {
        Remove-Item $OutputDir/* -Recurse -Force -ErrorAction Ignore
    }

    # Run tests with to generate OpenCover data. We use OpenCover because
    # ReportGenerator tool can convert it into various quality reports.
    #
    # Note: This implementation assumes the test projects are using
    # `coverlet.msbuild` package for collecting coverage data.
    $coverageArgs = @(
        '--configuration', $Configuration
        '--no-restore'
        '-p:CollectCoverage=true'
        '-p:CoverletOutputFormat=opencover'
    )
    Invoke-Shell -- dotnet test @coverageArgs

    # Generate coverage reports...
    if ($ReportType -contains 'opencover') {
        # The free version of ReportGenerator doesn't support merging multiple OpenCover
        # reports, so we'll just copy them to the output directory with unique names.
        foreach ($coverageReport in Get-Item $coverageReports) {
            Copy-Item $coverageReport "$OutputDir/$($coverageReport.Directory.Name).opencover.xml"
        }
        $ReportType = $ReportType.where{ $_ -ne 'opencover' }
    }
    if ($ReportType) {
        $reportArgs = @(
            "-reports:$coverageReports"
            "-reportTypes:$($ReportType -join ',')"
            "-targetDir:$OutputDir"
        )
        Invoke-Shell -- reportgenerator @reportArgs
    }

    if ($ReportType -contains 'Html' -and !$DoNotOpenReport) {
        Start-Process "$OutputDir/index.html" # open in browser
    }
}

Task package -desc 'Package the solution' -dependsOn build {
    <#
    .DESCRIPTION
        Packages the solution into NuGet packages using 'dotnet pack'.

        By default, it will package all packable projects in the solution. You can specify
        a specific project using the -TargetProject parameter.

        The output packages will be placed in the "artifacts/package/$configuration"
        directory by default, but you can specify a different output directory using
        the -OutputDir parameter.

        When packaging release builds, the version must be specified using the -Version
        parameter. For non-release builds, the version is optional and will default to
        whatever version is specified in the .csproj file(s), usually 1.0.0.
    .EXAMPLE
        PS> ./build.ps1 package

        Build and package all packable projects in the solution using the Debug
        configuration and default version.
    .EXAMPLE
        PS> ./build.ps1 package -Version 1.2.3 -TargetProject ./src/MyProject/

        Build and package the specified project as version 1.2.3.
    #>
    param(
        # Optional path to the project to package. If not specified, all packable projects
        # in the solution will be packaged.
        [string]$TargetProject,
        # The output directory for the package(s). Defaults to "artifacts/package/$configuration".
        [string]$OutputDir = "artifacts/package/$($Configuration.ToLower())"
    )
    if ($Configuration -eq 'release' -and -not $Version) {
        throw 'Version must be specified when packing release builds.'
    }
    if ($Configuration -ne 'release') {
        Write-Warning "Packaging $Configuration build! It's recommended to only package Release builds."
    }

    $packArgs = @(
        if ($TargetProject) { $TargetProject }
        '--configuration', $Configuration
        '--no-build'
        if ($Version) { "-p:Version=$Version" }
        if ($OutputDir) { '--output', $OutputDir }
    )
    Invoke-Shell -- dotnet pack @packArgs
}

Task push -desc 'Push NuGet packages' -dependsOn version {
    <#
    .DESCRIPTION
        Pushes the specified NuGet packages to the configured NuGet source.

        Note: This task requires the packages to have already been created (see
        the 'package' task).
    .EXAMPLE
        PS> ./build.ps1 push ./artifacts/package/release

        Push all packages in the specified folder to the default NuGet source,
        prompting for the API key if not set via the NUGET_API_KEY environment variable.
    .EXAMPLE
        PS> ./build.ps1 push ./artifacts/package/release/MyPackage.1.2.3.nupkg -ApiKey $secretKey

        Push the specified package using the specified API key.
    #>
    [CmdletBinding(PositionalBinding = $false)]
    param(
        # The path(s) to the package(s) to push. Can be a single path (file or directory)
        # or an array of paths.
        [Parameter(Mandatory, Position = 0)]
        [string[]]$PackagePath,
        # The API key to use when pushing packages. Defaults to the NUGET_API_KEY
        # environment variable. If not set, the user will be prompted for the API key.
        [string]$ApiKey = $env:NUGET_API_KEY,
        # Url or source name of the target NuGet registry. Defaults to the configured
        # `DefaultPushSource` if any, otherwise NuGet.org.
        [string]$NugetSource = $null
    )

    $PackagePath = $PackagePath.foreach{
        $package = $_
        if (Test-Path $package -PathType Container) {
            $package = Join-Path $package '*.nupkg'
        }
        if (!(Test-Path $package -PathType Leaf)) {
            throw "Package not found: '$package'."
        }
        $package
    }

    $NugetSourceName = $null
    if (!$NugetSource) {
        # is there a default push source configured...
        $dps = Invoke-Shell -InformationAction Ignore -ErrorAction Ignore -- dotnet nuget config get DefaultPushSource 2>&1
        if ($global:LASTEXITCODE -eq 0) {
            $NugetSource = $dps.Trim()
        }
        else {
            Write-Host 'No default NuGet push source configured. Defaulting to NuGet.org.' -ForegroundColor Yellow
            $NugetSource = 'https://api.nuget.org/v3/index.json'
            $NugetSourceName = 'NuGet.org'
        }
    }
    $NugetSourceName ??= (
        [uri]::IsWellFormedUriString($NugetSource, [uriKind]::Absolute) ?
        ([uri]$NugetSource).Host :
        $NugetSource
    )

    if (-not $ApiKey) {
        $ApiKey = Read-Secret "Enter API key for pushing packages to $NugetSourceName"
    }
    Push-Secret $ApiKey
    try {
        foreach ($package in $PackagePath) {
            $package = [System.IO.Path]::GetFullPath($package) # retains wildcards
            $pushArgs = @(
                $package
                if ($NugetSource) { '--source', $NugetSource }
                '--api-key', $ApiKey
                '--skip-duplicate'
            )
            Invoke-Shell -- dotnet nuget push @pushArgs
        }
    }
    finally {
        Pop-Secret $ApiKey
    }
}

Task runExample -desc 'Run the example application' -dependsOn build {
    <#
    .DESCRIPTION
        Runs the example application located in 'examples/LeaderElectionTester' folder.

        By default it uses Aspire to orchestrate the infrastructure and the example
        application instances. Click the "dashboard" link in the console output to view
        the Aspire dashboard.

        To run without Aspire, use the -NoAspire switch. In this mode, you are responsible
        for ensuring that the necessary infrastructure (e.g. docker containers) is
        properly configured and running *before* starting the example application.
    .EXAMPLE
        PS> ./build.ps1 runExample

        Uses Aspire to orchestrate a Redis container and two instances of the example
        application running Redis leader election.
    .EXAMPLE
        PS> ./build.ps1 runExample -- blob -count 5

        Uses Aspire to orchestrate a Blob Storage container and five instances of the
        example application running BlobStorage leader election.
    .EXAMPLE
        PS> ./build.ps1 runExample -- postgres -NoAspire

        Starts one instance of the example application running Postgres leader election.
        The caller is responsible for managing a compatible Postgres database.
        Run the command again (in another terminal window) to create a second instance.
    #>
    [CmdletBinding(PositionalBinding = $false)]
    param(
        # The type of LeaderElection to run. Valid values are:
        #  - redis
        #  - blob or blobStorage
        #  - s3
        #  - postgres
        #  - dc or distributedCache
        #  - fc or fusionCache
        # Default is redis.
        [Parameter(Position = 0)]
        [string] $Type = 'redis',

        # The number of example application instances Aspire should create.
        # Defaults to 2.
        [ValidateRange(1, 100)]
        [int] $Count = 2,

        # When specified, a single instance of the example application will be started without
        # using Aspire. It is the caller's responsibility to ensure that the necessary
        # infrastructure (e.g. docker containers) is properly configured.
        [switch] $NoAspire
    )

    $runArgs = @(
        '--no-build'
        '--launch-profile', 'https'
        '--'
        "LeaderElectionType=$Type"
    )

    if ($NoAspire) {
        Set-Location './examples/LeaderElectionTester'
    }
    else {
        Set-Location './examples/LeaderElectionTester.AppHost'
        $runArgs += "TesterCount=$Count"
    }

    Invoke-Shell -- dotnet run @runArgs
}

#endregion Task definitions

####################################################################################
# Execute the specified task(s)...
####################################################################################
Invoke-TaskFramework `
    -TaskName $TaskName `
    -TaskArgs $TaskArgs `
    -SkipDependencies:$SkipDependencies `
    -ExitOnError

# Save TaskContext in a global variable so that it can be inspected
$global:LastTaskContext = $TaskContext
