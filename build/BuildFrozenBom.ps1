param(
    [Parameter(Mandatory = $true)]
    [string] $Root
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$rootPath = [System.IO.Path]::GetFullPath($Root)
$sourcesPath = Join-Path $rootPath "sources"
$feedPath = Join-Path $rootPath "feed"
$packagesPath = Join-Path $rootPath "packages"
$bundlePath = Join-Path $rootPath "bundle"
$artifactsPath = Join-Path $rootPath "artifacts"
$logsPath = Join-Path $rootPath "logs"
$tempPath = Join-Path $rootPath "temp"
$nuGetConfigPath = Join-Path $rootPath "NuGet.config"
$packageVersion = "0.5.0-dev.20260904.5"

New-Item -ItemType Directory -Force -Path @(
    $rootPath,
    $sourcesPath,
    $feedPath,
    $packagesPath,
    $artifactsPath,
    $logsPath,
    $tempPath
) | Out-Null

if (Get-ChildItem -LiteralPath $feedPath -Filter "*.nupkg" -File -ErrorAction SilentlyContinue)
{
    throw "The frozen feed must be empty before package creation."
}

$env:TEMP = $tempPath
$env:TMP = $tempPath
$env:NUGET_PACKAGES = $packagesPath
$env:NUGET_HTTP_CACHE_PATH = Join-Path $rootPath "http-cache"
$env:DOTNET_CLI_HOME = Join-Path $rootPath "dotnet-home"

function Invoke-BoundedProcess
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $Label,

        [Parameter(Mandatory = $true)]
        [string] $FilePath,

        [Parameter(Mandatory = $true)]
        [string[]] $Arguments,

        [Parameter(Mandatory = $true)]
        [string] $WorkingDirectory,

        [int] $TimeoutSeconds = 1800
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    foreach ($argument in $Arguments)
    {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start())
    {
        throw "Process start failed for $Label."
    }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()

    if (-not $process.WaitForExit($TimeoutSeconds * 1000))
    {
        $process.Kill($true)
        $process.WaitForExit()
        throw "$Label exceeded its $TimeoutSeconds second limit."
    }

    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $safeLabel = $Label -replace '[^A-Za-z0-9_.-]', '-'
    Set-Content -LiteralPath (Join-Path $logsPath "$safeLabel.stdout.log") -Value $stdout -NoNewline
    Set-Content -LiteralPath (Join-Path $logsPath "$safeLabel.stderr.log") -Value $stderr -NoNewline
    Set-Content -LiteralPath (Join-Path $logsPath "$safeLabel.exit.txt") -Value $process.ExitCode -NoNewline

    if ($process.ExitCode -ne 0)
    {
        if (-not [string]::IsNullOrWhiteSpace($stdout))
        {
            Write-Host $stdout
        }

        if (-not [string]::IsNullOrWhiteSpace($stderr))
        {
            Write-Host $stderr
        }

        throw "$Label failed with exit code $($process.ExitCode)."
    }

    Write-Host "$Label completed."

    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        StdOut = $stdout
        StdErr = $stderr
    }
}

$repositories = @(
    [pscustomobject]@{
        Name = "contracts"
        Repository = "https://github.com/SharpClaw-NET/SharpClaw.Contracts.git"
        Commit = "4cf302ddff93271dfa91688f61f8b23c201a965c"
    },
    [pscustomobject]@{
        Name = "core"
        Repository = "https://github.com/SharpClaw-NET/SharpClaw.Core.git"
        Commit = "618fe598fe0cdb6fb3914abd5bf7ed3b25ed6643"
    },
    [pscustomobject]@{
        Name = "module-sdk"
        Repository = "https://github.com/SharpClaw-NET/SharpClaw.ModuleSDK.git"
        Commit = "62b2851ae4e33c9e9c98aeb4969b83718738b3c2"
    },
    [pscustomobject]@{
        Name = "agent-modules"
        Repository = "https://github.com/SharpClaw-NET/SharpClaw.AgentOrchestration.git"
        Commit = "e474cc56ef3290947006d93188c37f15d8da7c88"
    },
    [pscustomobject]@{
        Name = "editor-integrations"
        Repository = "https://github.com/SharpClaw-NET/SharpClaw.EditorIntegrations.git"
        Commit = "0165a6f420d9ffe857aead0567e3bfd8ce458a1e"
    },
    [pscustomobject]@{
        Name = "metrics"
        Repository = "https://github.com/SharpClaw-NET/SharpClaw.Metrics.git"
        Commit = "3e4f2a28d05be731aad3ed2f0af56fbfd31601e7"
    },
    [pscustomobject]@{
        Name = "provider-integrations"
        Repository = "https://github.com/SharpClaw-NET/SharpClaw.ProviderIntegrations.git"
        Commit = "83ed56f12ba65a9adb4865dc0a3a7e7ea445ed3f"
    },
    [pscustomobject]@{
        Name = "module-dev"
        Repository = "https://github.com/SharpClaw-NET/SharpClaw.ModuleDevKit.git"
        Commit = "1f4ccfb8134b9a1c79adfb933d8004d56a8f76eb"
    }
)

foreach ($repository in $repositories)
{
    $destination = Join-Path $sourcesPath $repository.Name
    Invoke-BoundedProcess `
        -Label "clone-$($repository.Name)" `
        -FilePath "git" `
        -Arguments @("clone", "--filter=blob:none", "--no-checkout", $repository.Repository, $destination) `
        -WorkingDirectory $sourcesPath `
        -TimeoutSeconds 600 | Out-Null

    Invoke-BoundedProcess `
        -Label "fetch-$($repository.Name)" `
        -FilePath "git" `
        -Arguments @("-C", $destination, "fetch", "--depth", "1", "origin", $repository.Commit) `
        -WorkingDirectory $sourcesPath `
        -TimeoutSeconds 600 | Out-Null

    Invoke-BoundedProcess `
        -Label "checkout-$($repository.Name)" `
        -FilePath "git" `
        -Arguments @("-c", "advice.detachedHead=false", "-C", $destination, "checkout", "--detach", $repository.Commit) `
        -WorkingDirectory $sourcesPath `
        -TimeoutSeconds 300 | Out-Null

    $head = Invoke-BoundedProcess `
        -Label "verify-$($repository.Name)" `
        -FilePath "git" `
        -Arguments @("-C", $destination, "rev-parse", "HEAD") `
        -WorkingDirectory $sourcesPath `
        -TimeoutSeconds 60

    if ($head.StdOut.Trim() -ne $repository.Commit)
    {
        throw "The $($repository.Name) checkout does not match its required commit."
    }
}

$escapedFeedPath = [System.Security.SecurityElement]::Escape($feedPath)
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="sharpclaw-local" value="$escapedFeedPath" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="sharpclaw-local">
      <package pattern="SharpClaw.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath $nuGetConfigPath -Encoding utf8

function Restore-Target
{
    param(
        [string] $Label,
        [string] $Target,
        [string] $ArtifactGroup
    )

    $targetArtifacts = Join-Path $artifactsPath $ArtifactGroup
    Invoke-BoundedProcess `
        -Label "restore-$Label" `
        -FilePath "dotnet" `
        -Arguments @(
            "restore",
            $Target,
            "--configfile",
            $nuGetConfigPath,
            "--packages",
            $packagesPath,
            "-p:ArtifactsPath=$targetArtifacts",
            "-p:UseArtifactsOutput=true",
            "-p:SharpClawArtifactsRoot=$targetArtifacts"
        ) `
        -WorkingDirectory (Split-Path -Parent $Target) | Out-Null
}

function Pack-Target
{
    param(
        [string] $Label,
        [string] $Target,
        [string] $ArtifactGroup,
        [string[]] $ExtraArguments = @()
    )

    $targetArtifacts = Join-Path $artifactsPath $ArtifactGroup
    $arguments = @(
        "pack",
        $Target,
        "--no-restore",
        "--configuration",
        "Release",
        "-p:ArtifactsPath=$targetArtifacts",
        "-p:UseArtifactsOutput=true",
        "-p:SharpClawArtifactsRoot=$targetArtifacts",
        "-p:PackageOutputPath=$feedPath",
        "-p:PackageVersion=$packageVersion",
        "-p:ContinuousIntegrationBuild=true"
    ) + $ExtraArguments
    Invoke-BoundedProcess `
        -Label "pack-$Label" `
        -FilePath "dotnet" `
        -Arguments $arguments `
        -WorkingDirectory (Split-Path -Parent $Target) | Out-Null
}

function Restore-And-Pack
{
    param(
        [string] $Label,
        [string] $Target,
        [string] $ArtifactGroup
    )

    Restore-Target -Label $Label -Target $Target -ArtifactGroup $ArtifactGroup
    Pack-Target -Label $Label -Target $Target -ArtifactGroup $ArtifactGroup
}

$contractsProject = Join-Path $sourcesPath "contracts\SharpClaw.Contracts\SharpClaw.Contracts.csproj"
$gatewayProject = Join-Path $sourcesPath "contracts\SharpClaw.Gateway.Contracts\SharpClaw.Gateway.Contracts.csproj"
$coreProject = Join-Path $sourcesPath "core\SharpClaw.Core\SharpClaw.Core.csproj"
$moduleSdkProject = Join-Path $sourcesPath "module-sdk\SharpClaw.ModuleSDK\SharpClaw.ModuleSDK.csproj"
$moduleHostsSolution = Join-Path $sourcesPath "module-sdk\SharpClaw.ModuleSDK.slnx"
$moduleInProcessProject = Join-Path $sourcesPath "module-sdk\SharpClaw.SidecarHost.InProcess\SharpClaw.SidecarHost.InProcess.csproj"
$moduleOutOfProcessProject = Join-Path $sourcesPath "module-sdk\SharpClaw.SidecarHost.OutOfProcess\SharpClaw.SidecarHost.OutOfProcess.csproj"
$moduleTestingProject = Join-Path $sourcesPath "module-sdk\SharpClaw.ModuleSDK.Testing\SharpClaw.ModuleSDK.Testing.csproj"
$moduleHostOperationsProject = Join-Path $sourcesPath "module-sdk\SharpClaw.ModuleSDK.HostOperations\SharpClaw.ModuleSDK.HostOperations.csproj"
$agentContractsProject = Join-Path $sourcesPath "agent-modules\SharpClaw.AgentOrchestration.Contracts\SharpClaw.AgentOrchestration.Contracts.csproj"
$agentSolution = Join-Path $sourcesPath "agent-modules\SharpClaw.AgentOrchestration.slnx"
$editorSolution = Join-Path $sourcesPath "editor-integrations\SharpClaw.EditorIntegrations.slnx"
$metricsProject = Join-Path $sourcesPath "metrics\SharpClaw.Modules.Metrics\SharpClaw.Modules.Metrics.csproj"
$providerSolution = Join-Path $sourcesPath "provider-integrations\SharpClaw.ProviderIntegrations.slnx"
$moduleDevProject = Join-Path $sourcesPath "module-dev\SharpClaw.Modules.ModuleDev\SharpClaw.Modules.ModuleDev.csproj"
$permissionRestrictionFixture = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\SharpClaw.Tests\Fixtures\PermissionRestriction\SharpClaw.TestFixtures.PermissionRestriction.csproj"))

Restore-And-Pack -Label "contracts" -Target $contractsProject -ArtifactGroup "contracts"
Restore-And-Pack -Label "gateway-contracts" -Target $gatewayProject -ArtifactGroup "gateway-contracts"
Restore-And-Pack -Label "core" -Target $coreProject -ArtifactGroup "core"
Restore-And-Pack -Label "module-sdk" -Target $moduleSdkProject -ArtifactGroup "module-sdk"

Restore-Target -Label "module-hosts" -Target $moduleHostsSolution -ArtifactGroup "module-hosts"
Pack-Target -Label "module-in-process" -Target $moduleInProcessProject -ArtifactGroup "module-hosts"
Pack-Target -Label "module-testing" -Target $moduleTestingProject -ArtifactGroup "module-hosts"
Pack-Target -Label "module-host-operations" -Target $moduleHostOperationsProject -ArtifactGroup "module-hosts"

$moduleHostsArtifacts = Join-Path $artifactsPath "module-hosts"
Invoke-BoundedProcess `
    -Label "build-module-out-of-process" `
    -FilePath "dotnet" `
    -Arguments @(
        "build",
        $moduleOutOfProcessProject,
        "--no-restore",
        "--configuration",
        "Release",
        "-p:ArtifactsPath=$moduleHostsArtifacts",
        "-p:UseArtifactsOutput=true",
        "-p:SharpClawArtifactsRoot=$moduleHostsArtifacts",
        "-p:ContinuousIntegrationBuild=true"
    ) `
    -WorkingDirectory (Split-Path -Parent $moduleOutOfProcessProject) | Out-Null

$outOfProcessPayload = Join-Path $rootPath "out-of-process-payload"
New-Item -ItemType Directory -Path $outOfProcessPayload | Out-Null
$outOfProcessOutput = Join-Path $moduleHostsArtifacts "bin\SharpClaw.SidecarHost.OutOfProcess\release"
foreach ($payloadName in @(
    "SharpClaw.SidecarHost.OutOfProcess.exe",
    "SharpClaw.SidecarHost.OutOfProcess.dll",
    "SharpClaw.SidecarHost.OutOfProcess.deps.json",
    "SharpClaw.SidecarHost.OutOfProcess.runtimeconfig.json",
    "SharpClaw.Contracts.dll",
    "SharpClaw.Core.dll",
    "SharpClaw.SidecarHost.InProcess.dll"
))
{
    Copy-Item -LiteralPath (Join-Path $outOfProcessOutput $payloadName) -Destination $outOfProcessPayload
}

$sdkArchive = Join-Path $feedPath "SharpClaw.ModuleSDK.0.5.0-dev.20260904.5.nupkg"
$sdkZip = [System.IO.Compression.ZipFile]::OpenRead($sdkArchive)
try
{
    $sdkEntry = $sdkZip.GetEntry("lib/net10.0/SharpClaw.ModuleSDK.dll")
    if ($null -eq $sdkEntry)
    {
        throw "The accepted ModuleSDK package does not contain its runtime DLL."
    }

    [System.IO.Compression.ZipFileExtensions]::ExtractToFile(
        $sdkEntry,
        (Join-Path $outOfProcessPayload "SharpClaw.ModuleSDK.dll"),
        $false)
}
finally
{
    $sdkZip.Dispose()
}

Pack-Target `
    -Label "module-out-of-process" `
    -Target $moduleOutOfProcessProject `
    -ArtifactGroup "module-hosts" `
    -ExtraArguments @("-p:OutOfProcessHostPayloadSource=$outOfProcessPayload")
Restore-And-Pack -Label "agent-contracts" -Target $agentContractsProject -ArtifactGroup "agent-modules"

Restore-Target -Label "agent-modules" -Target $agentSolution -ArtifactGroup "agent-modules"
foreach ($agentProject in @(
    "SharpClaw.Modules.Context\SharpClaw.Modules.Context.csproj",
    "SharpClaw.Modules.TwoTierPermission\SharpClaw.Modules.TwoTierPermission.csproj",
    "SharpClaw.Modules.Agents\SharpClaw.Modules.Agents.csproj"
))
{
    $target = Join-Path (Join-Path $sourcesPath "agent-modules") $agentProject
    $label = [System.IO.Path]::GetFileNameWithoutExtension($target)
    Pack-Target -Label $label -Target $target -ArtifactGroup "agent-modules"
}

Restore-Target `
    -Label "permission-restriction-fixture" `
    -Target $permissionRestrictionFixture `
    -ArtifactGroup "permission-restriction-fixture"
Pack-Target `
    -Label "permission-restriction-fixture" `
    -Target $permissionRestrictionFixture `
    -ArtifactGroup "permission-restriction-fixture" `
    -ExtraArguments @("-p:PackageVersion=0.5.0-beta.1")

Restore-And-Pack -Label "editor-integrations" -Target $editorSolution -ArtifactGroup "editor-integrations"
Restore-And-Pack -Label "metrics" -Target $metricsProject -ArtifactGroup "metrics"
Restore-And-Pack -Label "provider-integrations" -Target $providerSolution -ArtifactGroup "provider-integrations"
Restore-And-Pack -Label "module-dev" -Target $moduleDevProject -ArtifactGroup "module-dev"

$expectedPackages = @(
    "SharpClaw.AgentOrchestration.Contracts.0.5.0-dev.20260904.5.nupkg",
    "SharpClaw.Contracts.0.5.0-dev.20260904.5.nupkg",
    "SharpClaw.Core.0.5.0-dev.20260904.5.nupkg",
    "SharpClaw.Gateway.Contracts.0.5.0-dev.20260904.5.nupkg",
    "SharpClaw.SidecarHost.InProcess.0.5.0-dev.20260904.5.nupkg",
    "SharpClaw.SidecarHost.OutOfProcess.0.5.0-dev.20260904.5.nupkg",
    "SharpClaw.ModuleSDK.0.5.0-dev.20260904.5.nupkg",
    "SharpClaw.ModuleSDK.HostOperations.0.5.0-dev.20260904.5.nupkg",
    "SharpClaw.ModuleSDK.Testing.0.5.0-dev.20260904.5.nupkg",
    "SharpClaw.Modules.Agents.0.5.0-dev.20260904.5.nupkg",
    "SharpClaw.Modules.Context.0.5.0-dev.20260904.5.nupkg",
    "SharpClaw.Modules.EditorCommon.0.5.0-dev.20260904.5.nupkg",
    "SharpClaw.Modules.Metrics.0.5.0-dev.20260904.5.nupkg",
    "SharpClaw.Modules.ModuleDev.0.5.0-dev.20260904.5.nupkg",
    "SharpClaw.Modules.Providers.Anthropic.0.5.0-dev.20260904.5.nupkg",
    "SharpClaw.Modules.Providers.Google.0.5.0-dev.20260904.5.nupkg",
    "SharpClaw.Modules.Providers.LlamaSharp.0.5.0-dev.20260904.5.nupkg",
    "SharpClaw.Modules.Providers.Ollama.0.5.0-dev.20260904.5.nupkg",
    "SharpClaw.Modules.Providers.OpenAICompatible.0.5.0-dev.20260904.5.nupkg",
    "SharpClaw.Modules.TwoTierPermission.0.5.0-dev.20260904.5.nupkg",
    "SharpClaw.Modules.VS2026Editor.0.5.0-dev.20260904.5.nupkg",
    "SharpClaw.Modules.VSCodeEditor.0.5.0-dev.20260904.5.nupkg",
    "SharpClaw.Providers.Common.0.5.0-dev.20260904.5.nupkg",
    "SharpClaw.Providers.LocalCommon.0.5.0-dev.20260904.5.nupkg",
    "SharpClaw.TestFixtures.PermissionRestriction.0.5.0-beta.1.nupkg"
)

$actualPackages = Get-ChildItem -LiteralPath $feedPath -Filter "*.nupkg" -File |
    Where-Object { -not $_.Name.EndsWith(".snupkg", [System.StringComparison]::OrdinalIgnoreCase) } |
    Sort-Object Name

$actualNames = @($actualPackages.Name)
$missingPackages = @($expectedPackages | Where-Object { $_ -notin $actualNames })
$unexpectedPackages = @($actualNames | Where-Object { $_ -notin $expectedPackages })
if ($missingPackages.Count -ne 0 -or $unexpectedPackages.Count -ne 0)
{
    throw "The frozen feed package set is not exact. Missing: $($missingPackages -join ', '). Unexpected: $($unexpectedPackages -join ', ')."
}

$maximumPackageLength = 250MB
$oversizedPackages = @($actualPackages | Where-Object { $_.Length -ge $maximumPackageLength })
if ($oversizedPackages.Count -ne 0)
{
    throw "The frozen feed contains packages at or above 250 MB: $($oversizedPackages.Name -join ', ')."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($package in $actualPackages)
{
    $zip = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
    try
    {
        $nuspecEntries = @(
            $zip.Entries |
                Where-Object { $_.FullName.EndsWith(".nuspec", [System.StringComparison]::OrdinalIgnoreCase) }
        )
        if ($nuspecEntries.Count -ne 1)
        {
            throw "Package '$($package.Name)' must contain one nuspec."
        }

        $reader = [System.IO.StreamReader]::new($nuspecEntries[0].Open())
        try
        {
            [xml] $nuspec = $reader.ReadToEnd()
        }
        finally
        {
            $reader.Dispose()
        }

        foreach ($dependency in $nuspec.SelectNodes("//*[local-name()='dependency']"))
        {
            $id = [string] $dependency.id
            $version = [string] $dependency.version
            if ($id.StartsWith("SharpClaw.", [System.StringComparison]::Ordinal) -and
                $version -notmatch '^\[[^,\[\]]+\]$')
            {
                throw "Package '$($package.Name)' has non-exact dependency '$id' version '$version'."
            }
        }
    }
    finally
    {
        $zip.Dispose()
    }
}

Invoke-BoundedProcess `
    -Label "materialize-contribution-bundle" `
    -FilePath "pwsh" `
    -Arguments @(
        "-NoLogo",
        "-NoProfile",
        "-File",
        (Join-Path $PSScriptRoot "MaterializeContributionBundle.ps1"),
        "-Feed",
        $feedPath,
        "-Output",
        $bundlePath
    ) `
    -WorkingDirectory $PSScriptRoot `
    -TimeoutSeconds 300 | Out-Null

$canonicalAssemblies = @{}
foreach ($package in $actualPackages)
{
    $zip = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
    try
    {
        foreach ($entry in $zip.Entries | Where-Object { $_.FullName -like "lib/net10.0/SharpClaw.*.dll" })
        {
            $stream = $entry.Open()
            try
            {
                $hash = [System.Convert]::ToHexString(
                    [System.Security.Cryptography.SHA256]::HashData($stream))
            }
            finally
            {
                $stream.Dispose()
            }

            if ($canonicalAssemblies.ContainsKey($entry.Name) -and
                $canonicalAssemblies[$entry.Name] -ne $hash)
            {
                throw "The frozen feed has multiple payloads for assembly '$($entry.Name)'."
            }

            $canonicalAssemblies[$entry.Name] = $hash
        }
    }
    finally
    {
        $zip.Dispose()
    }
}

$bundleAssemblyGroups = Get-ChildItem -LiteralPath (Join-Path $bundlePath "contributions") `
    -Filter "SharpClaw.*.dll" -File -Recurse |
    Group-Object Name
foreach ($group in $bundleAssemblyGroups)
{
    $hashes = @($group.Group | ForEach-Object {
        (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
    } | Sort-Object -Unique)
    if ($hashes.Count -ne 1)
    {
        throw "The contribution bundle has multiple payloads for assembly '$($group.Name)'."
    }

    if (-not $canonicalAssemblies.ContainsKey($group.Name) -or
        $canonicalAssemblies[$group.Name] -ne $hashes[0])
    {
        throw "The contribution bundle assembly '$($group.Name)' does not match its package."
    }
}

$manifest = [pscustomobject]@{
    Repositories = $repositories
    ContributionBundleManifestSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (
        Join-Path $bundlePath "contribution-bundle-manifest.json")).Hash
    ContributionAssemblies = @($canonicalAssemblies.GetEnumerator() | Sort-Object Key | ForEach-Object {
        [pscustomobject]@{
            Name = $_.Key
            Sha256 = $_.Value
        }
    })
    Packages = @($actualPackages | ForEach-Object {
        [pscustomobject]@{
            Name = $_.Name
            Length = $_.Length
            Sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
        }
    })
}

$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $rootPath "bom-manifest.json") -Encoding utf8
Write-Host "Frozen BOM package count: $($actualPackages.Count)"
