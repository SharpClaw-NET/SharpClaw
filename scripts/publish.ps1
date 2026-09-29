<#
.SYNOPSIS
    Publishes SharpClaw deployment bundles.

.DESCRIPTION
    SharpClaw has three deployment types. Application publishes the Client.Uno
    app and bundles Gateway plus Runtime under gateway\ and backend\. Server
    publishes Gateway plus Runtime without the client. Runtime publishes only
    SharpClaw.Runtime.Host.

.PARAMETER Include
    Comma-separated list of deployment types to include. Valid values are
    Application, Server, Runtime, and All.

.PARAMETER Exclude
    Comma-separated list of deployment types to skip after Include is resolved.

.PARAMETER Rid
    Runtime identifier shorthand for Application builds. Valid shorthands are
    win, linux, osx, and all.

.PARAMETER ServerRid
    Runtime identifier shorthand for Server builds. Valid shorthands are win,
    linux, osx, and all.

.PARAMETER RuntimeRid
    Runtime identifier shorthand for Runtime builds. Valid shorthands are win,
    linux, osx, and all.

.PARAMETER BomRoot
    Required frozen graph directory containing feed, bundle, and bom-manifest.json.

.PARAMETER BomManifestSha256
    Required independently verified SHA-256 of bom-manifest.json.

.PARAMETER InstallerRevision
    Monotonic 0.5.0 preview revision, shared by binary, MSIX, and Debian identities.

.EXAMPLE
    .\scripts\publish.ps1 -BomRoot C:\artifacts\bom -BomManifestSha256 <verified-sha256>
#>
param(
    [string]$Include = "All",
    [string]$Exclude = "",
    [string]$Rid = "all",
    [string]$ServerRid = "all",
    [string]$RuntimeRid = "all",
    [string]$Configuration = "Release",
    [Parameter(Mandatory = $true)]
    [string]$BomRoot,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-fA-F0-9]{64}$')]
    [string]$BomManifestSha256,
    [ValidateRange(1, 65535)]
    [int]$InstallerRevision = 1,
    [string]$OutputDir = (Join-Path (Split-Path -Parent $PSScriptRoot) "publish"),
    [switch]$SkipZip,
    [switch]$Parallel
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $repoRoot 'build/PublishSupport.ps1')
if ($Parallel) { throw 'Parallel publishing is not supported; shared project outputs require a sequential loop.' }
$bom = Assert-PublishBom $BomRoot $BomManifestSha256
$version = "0.5.0-preview.$InstallerRevision"
$installerVersion = "0.5.0.$InstallerRevision"
$sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[a-f0-9]{40}$') { throw 'Cannot determine source commit.' }
if (& git -C $repoRoot status --porcelain) { throw 'Publish only a clean, committed source tree.' }
$branch = (& git -C $repoRoot branch --show-current).Trim()
if ($branch -notin @('main', 'dev')) { throw 'Publish only from main or dev.' }
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
if (Get-ChildItem -LiteralPath $OutputDir -Force) { throw 'Publish output must be empty; use a fresh directory.' }
$restoreConfig = Join-Path $OutputDir 'NuGet.config'
$feed = [Security.SecurityElement]::Escape((Join-Path $bom.Root 'feed'))
@"
<configuration>
  <packageSources><clear /><add key="frozen" value="$feed" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
  <packageSourceMapping><packageSource key="frozen"><package pattern="SharpClaw.*" /></packageSource><packageSource key="nuget.org"><package pattern="*" /></packageSource></packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath $restoreConfig -Encoding utf8
$env:NUGET_PACKAGES = Join-Path $OutputDir '.nuget/packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $OutputDir '.nuget/http-cache'
$publishProperties = @(
    "-p:SharpClawContributionPayloadRoot=$($bom.BundleRoot)",
    "-p:RestoreConfigFile=$restoreConfig",
    "-p:Version=$version", '-p:AssemblyVersion=0.5.0.0', "-p:FileVersion=$installerVersion",
    "-p:InformationalVersion=$version+$sourceCommit", '-p:IncludeSourceRevisionInInformationalVersion=false'
)
$clientProject = Join-Path (Join-Path $repoRoot "SharpClaw.Client.Uno") "SharpClaw.Client.Uno.csproj"
$runtimeProject = Join-Path (Join-Path (Join-Path $repoRoot "SharpClaw.Runtime") "Host") "SharpClaw.Runtime.Host.csproj"
$gatewayProject = Join-Path (Join-Path $repoRoot "SharpClaw.Gateway") "SharpClaw.Gateway.csproj"

$clientTfm = "net10.0-desktop"
$supportedRids = @("win-x64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")
$deploymentTypes = @("Application", "Server", "Runtime")
$results = [System.Collections.Generic.List[PSCustomObject]]::new()

function Get-ExeName {
    param([string]$BaseName, [string]$TargetRid)
    if ($TargetRid -like "win-*") { return "$BaseName.exe" }
    return $BaseName
}

function Resolve-DeploymentTypes {
    param([string]$Included, [string]$Excluded)

    $selected = if ($Included -eq "All") {
        $deploymentTypes
    } else {
        $Included -split "," | ForEach-Object { $_.Trim() } | Where-Object { $_ }
    }

    if ($Excluded) {
        $excludedSet = $Excluded -split "," | ForEach-Object { $_.Trim() } | Where-Object { $_ }
        $selected = $selected | Where-Object { $_ -notin $excludedSet }
    }

    foreach ($type in $selected) {
        if ($type -notin $deploymentTypes) {
            throw "Unknown deployment type '$type'. Valid values: $($deploymentTypes -join ', '), All."
        }
    }

    return @($selected | Select-Object -Unique)
}

function Resolve-Rids {
    param([string]$RidValue)

    $groups = @{
        "win" = @("win-x64")
        "linux" = @("linux-x64", "linux-arm64")
        "osx" = @("osx-x64", "osx-arm64")
        "all" = $supportedRids
    }

    if ($groups.ContainsKey($RidValue)) { return $groups[$RidValue] }
    if ($RidValue -in $supportedRids) { return @($RidValue) }
    throw "RID '$RidValue' is not supported. Valid values: $($supportedRids -join ', '), win, linux, osx, all."
}

function Get-DirSizeMB {
    param([string]$Path)
    $sum = (Get-ChildItem $Path -Recurse -File | Measure-Object -Property Length -Sum).Sum
    return [math]::Round($sum / 1MB, 1)
}

function New-ZipArchive {
    param([string]$SourceDir, [string]$ZipPath)
    [IO.Compression.ZipFile]::CreateFromDirectory($SourceDir, $ZipPath)
}

function Strip-ForeignNatives {
    param([string]$StageDir, [string]$TargetRid)

    $ridOs = ($TargetRid -split "-")[0]
    # Contribution files are immutable reviewed package bytes, including all RID assets.
    foreach ($runtimesDir in (Get-ChildItem $StageDir -Recurse -Directory -Filter "runtimes" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '[/\\]contributions[/\\]' })) {
        foreach ($subdir in (Get-ChildItem $runtimesDir.FullName -Directory -ErrorAction SilentlyContinue)) {
            if ($subdir.Name -notlike "$ridOs-*" -and $subdir.Name -ne $ridOs) {
                Remove-Item $subdir.FullName -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

function Add-Result {
    param([string]$Type, [string]$Target, [bool]$Ok, [string]$Artifact = "", [double]$SizeMB = 0)
    $results.Add([PSCustomObject]@{
        Type = $Type
        Target = $Target
        Ok = $Ok
        Artifact = $Artifact
        SizeMB = $SizeMB
    })
}

function Invoke-Dotnet {
    param([string[]]$Arguments)
    "dotnet $($Arguments -join ' ') $($publishProperties -join ' ')" |
        Add-Content -LiteralPath $script:currentBuildLog
    & dotnet @Arguments @publishProperties 2>&1 | Tee-Object -FilePath $script:currentBuildLog -Append
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Complete-Deployment {
    param([string]$Type, [string]$TargetRid, [string]$StageDir, [string]$RuntimeDir, [string]$ZipPath)
    $null = Assert-PublishBom $BomRoot $BomManifestSha256
    Assert-InstallerSource $repoRoot $sourceCommit
    Assert-NoActiveSecrets $StageDir
    Assert-NativeLauncher (Join-Path $RuntimeDir (Get-ExeName 'SharpClaw.Runtime.Host' $TargetRid)) $TargetRid
    Assert-NativeLauncher (Join-Path $RuntimeDir (Get-ExeName 'SharpClaw.SidecarHost.OutOfProcess' $TargetRid)) $TargetRid
    if ($Type -ne 'Runtime') {
        Assert-NativeLauncher (Join-Path $StageDir "gateway/$(Get-ExeName 'SharpClaw.Gateway' $TargetRid)") $TargetRid
    }
    if ($Type -eq 'Application') {
        Assert-NativeLauncher (Join-Path $StageDir (Get-ExeName 'SharpClaw.Client.Uno' $TargetRid)) $TargetRid
    }
    $runtimeFiles = @($bom.BundleFiles | ForEach-Object {
        [pscustomobject]@{ Path = $_.Path; Length = $_.Length; Sha256 = $_.Sha256 }
    })
    foreach ($file in $runtimeFiles) {
        Assert-FileDigest (Resolve-PayloadPath $RuntimeDir $file.Path) $file.Sha256 $file.Length
    }
    Assert-FileInventory (Join-Path $RuntimeDir 'contributions') @($runtimeFiles | ForEach-Object {
        [pscustomobject]@{ Path = $_.Path.Substring('contributions/'.Length); Length = $_.Length; Sha256 = $_.Sha256 }
    })
    foreach ($required in @(
        (Get-ExeName 'SharpClaw.SidecarHost.OutOfProcess' $TargetRid),
        'SharpClaw.SidecarHost.OutOfProcess.dll', 'SharpClaw.SidecarHost.OutOfProcess.deps.json',
        'SharpClaw.SidecarHost.OutOfProcess.runtimeconfig.json', 'SharpClaw.SidecarHost.THIRD-PARTY-NOTICES.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $RuntimeDir $required))) { throw "Missing sidecar payload '$required'." }
    }
    $config = Get-Content -LiteralPath (Join-Path $RuntimeDir 'SharpClaw.SidecarHost.OutOfProcess.runtimeconfig.json') -Raw | ConvertFrom-Json
    if (-not $config.runtimeOptions.PSObject.Properties['includedFrameworks']) {
        throw 'The sidecar must use the bundled self-contained runtime.'
    }
    $runtimeDependencies = Get-Content -LiteralPath (Join-Path $RuntimeDir 'SharpClaw.Runtime.Host.deps.json') -Raw | ConvertFrom-Json -AsHashtable
    if (@($runtimeDependencies.libraries.Keys | Where-Object { $_ -like 'SharpClaw.SidecarHost.OutOfProcess/*' }).Count -ne 1) {
        throw 'Runtime dependency metadata must contain its out-of-process host reference.'
    }
    foreach ($suffix in @('deps.json', 'runtimeconfig.json')) {
        $runtimeHash = (Get-FileHash -LiteralPath (Join-Path $RuntimeDir "SharpClaw.Runtime.Host.$suffix") -Algorithm SHA256).Hash
        $sidecarHash = (Get-FileHash -LiteralPath (Join-Path $RuntimeDir "SharpClaw.SidecarHost.OutOfProcess.$suffix") -Algorithm SHA256).Hash
        if ($runtimeHash -cne $sidecarHash) { throw "The sidecar must share the local Runtime $suffix closure." }
    }
    $sidecarStream = [IO.File]::OpenRead((Join-Path $RuntimeDir 'SharpClaw.SidecarHost.OutOfProcess.dll'))
    $sidecarPe = [Reflection.PortableExecutable.PEReader]::new($sidecarStream)
    try {
        if ($sidecarPe.PEHeaders.CoffHeader.Machine -ne [Reflection.PortableExecutable.Machine]::I386 -or
            -not $sidecarPe.PEHeaders.CorHeader.Flags.HasFlag([Reflection.PortableExecutable.CorFlags]::ILOnly) -or
            $sidecarPe.PEHeaders.CorHeader.Flags.HasFlag([Reflection.PortableExecutable.CorFlags]::Requires32Bit) -or
            $sidecarPe.PEHeaders.CorHeader.EntryPointTokenOrRelativeVirtualAddress -eq 0) {
            throw 'The shared sidecar entry assembly must be portable AnyCPU IL with a managed entry point.'
        }
    } finally { $sidecarPe.Dispose(); $sidecarStream.Dispose() }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE.md') -Destination $StageDir
    Copy-PackageNotices (Join-Path $bom.Root 'feed') $StageDir
    $provenance = Join-Path $StageDir 'provenance'
    New-Item -ItemType Directory -Path $provenance | Out-Null
    Copy-Item -LiteralPath (Join-Path $bom.Root 'bom-manifest.json'),
        (Join-Path $bom.BundleRoot 'contribution-bundle-manifest.json') -Destination $provenance
    Strip-ForeignNatives $StageDir $TargetRid
    Copy-ResolvedDependencyNotices $StageDir (Join-Path $OutputDir '.nuget/legal-cache') (Join-Path $repoRoot 'build/ThirdPartyNotices.json')
    $manifest = [pscustomobject]@{
        DeploymentType = $Type; Rid = $TargetRid; Version = $version; InstallerVersion = $installerVersion
        SourceCommit = $sourceCommit; BomManifestSha256 = $BomManifestSha256
        ContributionBundleManifestSha256 = $bom.Manifest.ContributionBundleManifestSha256
        Files = @(Get-PayloadInventory $StageDir)
    }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $StageDir 'publish-manifest.json') -Encoding utf8
    if (-not $SkipZip) { New-ZipArchive $StageDir $ZipPath }
    Add-Result $Type $TargetRid $true $StageDir (Get-DirSizeMB $StageDir)
}

function Publish-ServerComponents {
    param([string]$TargetRid, [string]$RuntimeDir, [string]$GatewayDir)
    Invoke-Dotnet @('publish', $runtimeProject, '-c', $Configuration, '-r', $TargetRid,
        '--self-contained', '-p:PublishReadyToRun=true', '-p:PublishTrimmed=false', '-o', $RuntimeDir)
    Invoke-Dotnet @('publish', $gatewayProject, '-c', $Configuration, '-r', $TargetRid,
        '--self-contained', '-p:PublishReadyToRun=true', '-p:PublishTrimmed=false', '-o', $GatewayDir)
}

function Publish-Application {
    param([string]$TargetRid)

    $stageDir = Join-Path $OutputDir "SharpClaw-Application-$TargetRid"
    $zipPath = Join-Path $OutputDir "SharpClaw-Application-$TargetRid.zip"

    Write-Host ""
    Write-Host "-- Application: $TargetRid ------------------------" -ForegroundColor Green

    Invoke-Dotnet @(
        "publish", $clientProject,
        "-c", $Configuration,
        "-f", $clientTfm,
        "-r", $TargetRid,
        "--self-contained",
        "-p:BundleBackend=false",
        "-p:BundleGateway=false",
        "-p:UseMonoRuntime=false",
        "-p:PublishReadyToRun=true",
        "-o", $stageDir
    )

    Publish-ServerComponents $TargetRid (Join-Path $stageDir 'backend') (Join-Path $stageDir 'gateway')

    $runtimeExe = Join-Path (Join-Path $stageDir "backend") (Get-ExeName "SharpClaw.Runtime.Host" $TargetRid)
    $gatewayExe = Join-Path (Join-Path $stageDir "gateway") (Get-ExeName "SharpClaw.Gateway" $TargetRid)
    if (-not (Test-Path $runtimeExe)) { throw "Application deployment did not produce bundled Runtime at $runtimeExe." }
    if (-not (Test-Path $gatewayExe)) { throw "Application deployment did not produce bundled Gateway at $gatewayExe." }

    Complete-Deployment 'Application' $TargetRid $stageDir (Join-Path $stageDir 'backend') $zipPath
}

function Publish-Server {
    param([string]$TargetRid)

    $stageDir = Join-Path $OutputDir "SharpClaw-Server-$TargetRid"
    $zipPath = Join-Path $OutputDir "SharpClaw-Server-$TargetRid.zip"
    $runtimeDir = Join-Path $stageDir "backend"
    $gatewayDir = Join-Path $stageDir "gateway"

    Write-Host ""
    Write-Host "-- Server: $TargetRid -----------------------------" -ForegroundColor Magenta
    Publish-ServerComponents $TargetRid $runtimeDir $gatewayDir

    $runtimeExe = Join-Path $runtimeDir (Get-ExeName "SharpClaw.Runtime.Host" $TargetRid)
    $gatewayExe = Join-Path $gatewayDir (Get-ExeName "SharpClaw.Gateway" $TargetRid)
    if (-not (Test-Path $runtimeExe)) { throw "Server deployment did not produce Runtime at $runtimeExe." }
    if (-not (Test-Path $gatewayExe)) { throw "Server deployment did not produce Gateway at $gatewayExe." }

    Complete-Deployment 'Server' $TargetRid $stageDir $runtimeDir $zipPath
}

function Publish-Runtime {
    param([string]$TargetRid)

    $stageDir = Join-Path $OutputDir "SharpClaw-Runtime-$TargetRid"
    $zipPath = Join-Path $OutputDir "SharpClaw-Runtime-$TargetRid.zip"

    Write-Host ""
    Write-Host "-- Runtime: $TargetRid ----------------------------" -ForegroundColor Cyan

    Invoke-Dotnet @(
        "publish", $runtimeProject,
        "-c", $Configuration,
        "-r", $TargetRid,
        "--self-contained",
        "-p:PublishReadyToRun=true",
        "-p:PublishTrimmed=false",
        "-o", $stageDir
    )

    $runtimeExe = Join-Path $stageDir (Get-ExeName "SharpClaw.Runtime.Host" $TargetRid)
    if (-not (Test-Path $runtimeExe)) { throw "Runtime deployment did not produce Runtime at $runtimeExe." }

    Complete-Deployment 'Runtime' $TargetRid $stageDir $stageDir $zipPath
}

$selectedTypes = Resolve-DeploymentTypes $Include $Exclude
if (-not (Test-Path $OutputDir)) { New-Item $OutputDir -ItemType Directory -Force | Out-Null }

Write-Host ""
Write-Host "SharpClaw publish: $($selectedTypes -join ', ') ($Configuration)" -ForegroundColor White

foreach ($type in $selectedTypes) {
    $ridValue = switch ($type) { 'Application' { $Rid }; 'Server' { $ServerRid }; 'Runtime' { $RuntimeRid } }
    foreach ($targetRid in (Resolve-Rids $ridValue)) {
        $script:currentBuildLog = Join-Path $OutputDir "$type-$targetRid.log"
        try {
            switch ($type) {
                'Application' { Publish-Application $targetRid }
                'Server' { Publish-Server $targetRid }
                'Runtime' { Publish-Runtime $targetRid }
            }
        } catch {
            $_ | Out-String | Add-Content -LiteralPath $script:currentBuildLog
            Write-Host "FAILED $type/$targetRid : $_" -ForegroundColor Red
            Add-Result $type $targetRid $false
        }
    }
}

Write-Host ""
Write-Host "SharpClaw publish results" -ForegroundColor White
foreach ($result in $results) {
    $status = if ($result.Ok) { "OK" } else { "FAILED" }
    Write-Host "  [$status] $($result.Type)/$($result.Target) $($result.SizeMB) MB $($result.Artifact)"
}

if (@($results | Where-Object { -not $_.Ok }).Count -gt 0) { exit 1 }
exit 0
