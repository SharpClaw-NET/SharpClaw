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
        Commit = "900cd0ab8fb2d23a86f481444b26f9d4f5ee5e8c"
    },
    [pscustomobject]@{
        Name = "gateway-contracts"
        Repository = "https://github.com/SharpClaw-NET/SharpClaw.Contracts.git"
        Commit = "bfad19087d824813cb9539837e628f6debf1df12"
    },
    [pscustomobject]@{
        Name = "core"
        Repository = "https://github.com/SharpClaw-NET/SharpClaw.Core.git"
        Commit = "4bb1d2f978628fef78dedb009c8cc819216d597f"
    },
    [pscustomobject]@{
        Name = "module-sdk"
        Repository = "https://github.com/SharpClaw-NET/SharpClaw.ModuleSDK.git"
        Commit = "4859e332175abec593504f571a4446cc5f5980ab"
    },
    [pscustomobject]@{
        Name = "agent-contracts"
        Repository = "https://github.com/SharpClaw-NET/SharpClaw.AgentOrchestration.git"
        Commit = "910d6adc96d77e074a63db422b1e8b05333ff936"
    },
    [pscustomobject]@{
        Name = "agent-modules"
        Repository = "https://github.com/SharpClaw-NET/SharpClaw.AgentOrchestration.git"
        Commit = "910d6adc96d77e074a63db422b1e8b05333ff936"
    },
    [pscustomobject]@{
        Name = "editor-integrations"
        Repository = "https://github.com/SharpClaw-NET/SharpClaw.EditorIntegrations.git"
        Commit = "cd64c32cde94129e7344d7b05eb9480bb560f845"
    },
    [pscustomobject]@{
        Name = "metrics"
        Repository = "https://github.com/SharpClaw-NET/SharpClaw.Metrics.git"
        Commit = "1e915dff9377a95c79170b74d2dc4f568a8798e5"
    },
    [pscustomobject]@{
        Name = "provider-integrations"
        Repository = "https://github.com/SharpClaw-NET/SharpClaw.ProviderIntegrations.git"
        Commit = "76ab80bd83409e2b08f5d2aaa80f1ae5610e85f9"
    },
    [pscustomobject]@{
        Name = "module-dev"
        Repository = "https://github.com/SharpClaw-NET/SharpClaw.ModuleDevKit.git"
        Commit = "d229e626644aa269646c097c06f6ccfe6e243278"
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
            "-p:UseArtifactsOutput=true"
        ) `
        -WorkingDirectory (Split-Path -Parent $Target) | Out-Null
}

function Pack-Target
{
    param(
        [string] $Label,
        [string] $Target,
        [string] $ArtifactGroup
    )

    $targetArtifacts = Join-Path $artifactsPath $ArtifactGroup
    Invoke-BoundedProcess `
        -Label "pack-$Label" `
        -FilePath "dotnet" `
        -Arguments @(
            "pack",
            $Target,
            "--no-restore",
            "--configuration",
            "Release",
            "-p:ArtifactsPath=$targetArtifacts",
            "-p:UseArtifactsOutput=true",
            "-p:PackageOutputPath=$feedPath",
            "-p:ContinuousIntegrationBuild=true"
        ) `
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
$gatewayProject = Join-Path $sourcesPath "gateway-contracts\SharpClaw.Gateway.Contracts\SharpClaw.Gateway.Contracts.csproj"
$coreProject = Join-Path $sourcesPath "core\SharpClaw.Core\SharpClaw.Core.csproj"
$moduleSdkSolution = Join-Path $sourcesPath "module-sdk\SharpClaw.ModuleSDK.slnx"
$agentContractsProject = Join-Path $sourcesPath "agent-contracts\SharpClaw.AgentOrchestration.Contracts\SharpClaw.AgentOrchestration.Contracts.csproj"
$agentSolution = Join-Path $sourcesPath "agent-modules\SharpClaw.AgentOrchestration.slnx"
$editorSolution = Join-Path $sourcesPath "editor-integrations\SharpClaw.EditorIntegrations.slnx"
$metricsProject = Join-Path $sourcesPath "metrics\SharpClaw.Modules.Metrics\SharpClaw.Modules.Metrics.csproj"
$providerSolution = Join-Path $sourcesPath "provider-integrations\SharpClaw.ProviderIntegrations.slnx"
$moduleDevProject = Join-Path $sourcesPath "module-dev\SharpClaw.Modules.ModuleDev\SharpClaw.Modules.ModuleDev.csproj"

Restore-And-Pack -Label "contracts" -Target $contractsProject -ArtifactGroup "contracts"
Restore-And-Pack -Label "gateway-contracts" -Target $gatewayProject -ArtifactGroup "gateway-contracts"
Restore-And-Pack -Label "core" -Target $coreProject -ArtifactGroup "core"
Restore-And-Pack -Label "module-sdk" -Target $moduleSdkSolution -ArtifactGroup "module-sdk"
Restore-And-Pack -Label "agent-contracts" -Target $agentContractsProject -ArtifactGroup "agent-contracts"

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

Restore-And-Pack -Label "editor-integrations" -Target $editorSolution -ArtifactGroup "editor-integrations"
Restore-And-Pack -Label "metrics" -Target $metricsProject -ArtifactGroup "metrics"
Restore-And-Pack -Label "provider-integrations" -Target $providerSolution -ArtifactGroup "provider-integrations"
Restore-And-Pack -Label "module-dev" -Target $moduleDevProject -ArtifactGroup "module-dev"

$expectedPackages = @(
    "SharpClaw.AgentOrchestration.Contracts.0.5.0-beta.17.nupkg",
    "SharpClaw.Contracts.0.5.0-beta.38.nupkg",
    "SharpClaw.Core.0.5.0-beta.34.nupkg",
    "SharpClaw.Gateway.Contracts.0.5.0-beta.2.nupkg",
    "SharpClaw.ModuleHost.InProcess.0.5.0-beta.21.nupkg",
    "SharpClaw.ModuleHost.OutOfProcess.0.5.0-beta.30.nupkg",
    "SharpClaw.ModuleSDK.0.5.0-beta.20.nupkg",
    "SharpClaw.ModuleSDK.HostOperations.0.5.0-beta.8.nupkg",
    "SharpClaw.ModuleSDK.Testing.0.5.0-beta.15.nupkg",
    "SharpClaw.Modules.Agents.0.5.0-beta.19.nupkg",
    "SharpClaw.Modules.Context.0.5.0-beta.18.nupkg",
    "SharpClaw.Modules.EditorCommon.0.5.0-beta.2.nupkg",
    "SharpClaw.Modules.Metrics.0.5.0-beta.2.nupkg",
    "SharpClaw.Modules.ModuleDev.0.5.0-beta.2.nupkg",
    "SharpClaw.Modules.Providers.Anthropic.0.5.0-beta.2.nupkg",
    "SharpClaw.Modules.Providers.Google.0.5.0-beta.2.nupkg",
    "SharpClaw.Modules.Providers.LlamaSharp.0.5.0-beta.2.nupkg",
    "SharpClaw.Modules.Providers.Ollama.0.5.0-beta.2.nupkg",
    "SharpClaw.Modules.Providers.OpenAICompatible.0.5.0-beta.2.nupkg",
    "SharpClaw.Modules.TwoTierPermission.0.5.0-beta.19.nupkg",
    "SharpClaw.Modules.VS2026Editor.0.5.0-beta.2.nupkg",
    "SharpClaw.Modules.VSCodeEditor.0.5.0-beta.2.nupkg",
    "SharpClaw.Providers.Common.0.5.0-beta.2.nupkg",
    "SharpClaw.Providers.LocalCommon.0.5.0-beta.2.nupkg"
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
    -Label "materialize-module-bundle" `
    -FilePath "pwsh" `
    -Arguments @(
        "-NoLogo",
        "-NoProfile",
        "-File",
        (Join-Path $PSScriptRoot "MaterializeModuleBundle.ps1"),
        "-Feed",
        $feedPath,
        "-Output",
        $bundlePath
    ) `
    -WorkingDirectory $PSScriptRoot `
    -TimeoutSeconds 300 | Out-Null

$manifest = [pscustomobject]@{
    Repositories = $repositories
    ModuleBundleManifestSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (
        Join-Path $bundlePath "module-bundle-manifest.json")).Hash
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
