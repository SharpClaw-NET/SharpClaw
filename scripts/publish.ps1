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

.EXAMPLE
    .\scripts\publish.ps1 -Include Application -Rid win
    .\scripts\publish.ps1 -Include Server -ServerRid linux
    .\scripts\publish.ps1 -Include Runtime -RuntimeRid win -SkipZip
#>
param(
    [string]$Include = "All",
    [string]$Exclude = "",
    [string]$Rid = "all",
    [string]$ServerRid = "all",
    [string]$RuntimeRid = "all",
    [string]$Configuration = "Release",
    [string]$OutputDir = (Join-Path (Split-Path -Parent $PSScriptRoot) "publish"),
    [switch]$SkipZip,
    [switch]$Parallel
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
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
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    Compress-Archive -Path "$SourceDir\*" -DestinationPath $ZipPath -Force
}

function Strip-ForeignNatives {
    param([string]$StageDir, [string]$TargetRid)

    $ridOs = ($TargetRid -split "-")[0]
    foreach ($runtimesDir in (Get-ChildItem $StageDir -Recurse -Directory -Filter "runtimes" -ErrorAction SilentlyContinue)) {
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
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Publish-Application {
    param([string]$TargetRid)

    $stageDir = Join-Path $OutputDir "SharpClaw-Application-$TargetRid"
    $zipPath = Join-Path $OutputDir "SharpClaw-Application-$TargetRid.zip"

    Write-Host ""
    Write-Host "-- Application: $TargetRid ------------------------" -ForegroundColor Green
    if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

    Invoke-Dotnet @(
        "publish", $clientProject,
        "-c", $Configuration,
        "-f", $clientTfm,
        "-r", $TargetRid,
        "--self-contained",
        "-p:BundleBackend=true",
        "-p:UseMonoRuntime=false",
        "-p:PublishReadyToRun=true",
        "-o", $stageDir
    )

    $runtimeExe = Join-Path (Join-Path $stageDir "backend") (Get-ExeName "SharpClaw.Runtime.Host" $TargetRid)
    $gatewayExe = Join-Path (Join-Path $stageDir "gateway") (Get-ExeName "SharpClaw.Gateway" $TargetRid)
    if (-not (Test-Path $runtimeExe)) { throw "Application deployment did not produce bundled Runtime at $runtimeExe." }
    if (-not (Test-Path $gatewayExe)) { throw "Application deployment did not produce bundled Gateway at $gatewayExe." }

    Strip-ForeignNatives $stageDir $TargetRid
    $size = Get-DirSizeMB $stageDir
    if (-not $SkipZip) { New-ZipArchive $stageDir $zipPath }
    Add-Result "Application" $TargetRid $true $stageDir $size
}

function Publish-Server {
    param([string]$TargetRid)

    $stageDir = Join-Path $OutputDir "SharpClaw-Server-$TargetRid"
    $zipPath = Join-Path $OutputDir "SharpClaw-Server-$TargetRid.zip"
    $runtimeDir = Join-Path $stageDir "backend"
    $gatewayDir = Join-Path $stageDir "gateway"

    Write-Host ""
    Write-Host "-- Server: $TargetRid -----------------------------" -ForegroundColor Magenta
    if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

    Invoke-Dotnet @(
        "publish", $runtimeProject,
        "-c", $Configuration,
        "-r", $TargetRid,
        "--self-contained",
        "-p:PublishReadyToRun=true",
        "-p:PublishTrimmed=false",
        "-o", $runtimeDir
    )

    Invoke-Dotnet @(
        "publish", $gatewayProject,
        "-c", $Configuration,
        "-r", $TargetRid,
        "--self-contained",
        "-p:PublishReadyToRun=true",
        "-p:PublishTrimmed=false",
        "-o", $gatewayDir
    )

    $runtimeExe = Join-Path $runtimeDir (Get-ExeName "SharpClaw.Runtime.Host" $TargetRid)
    $gatewayExe = Join-Path $gatewayDir (Get-ExeName "SharpClaw.Gateway" $TargetRid)
    if (-not (Test-Path $runtimeExe)) { throw "Server deployment did not produce Runtime at $runtimeExe." }
    if (-not (Test-Path $gatewayExe)) { throw "Server deployment did not produce Gateway at $gatewayExe." }

    Strip-ForeignNatives $stageDir $TargetRid
    $size = Get-DirSizeMB $stageDir
    if (-not $SkipZip) { New-ZipArchive $stageDir $zipPath }
    Add-Result "Server" $TargetRid $true $stageDir $size
}

function Publish-Runtime {
    param([string]$TargetRid)

    $stageDir = Join-Path $OutputDir "SharpClaw-Runtime-$TargetRid"
    $zipPath = Join-Path $OutputDir "SharpClaw-Runtime-$TargetRid.zip"

    Write-Host ""
    Write-Host "-- Runtime: $TargetRid ----------------------------" -ForegroundColor Cyan
    if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

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

    Strip-ForeignNatives $stageDir $TargetRid
    $size = Get-DirSizeMB $stageDir
    if (-not $SkipZip) { New-ZipArchive $stageDir $zipPath }
    Add-Result "Runtime" $TargetRid $true $stageDir $size
}

$selectedTypes = Resolve-DeploymentTypes $Include $Exclude
if (-not (Test-Path $OutputDir)) { New-Item $OutputDir -ItemType Directory -Force | Out-Null }

Write-Host ""
Write-Host "SharpClaw publish: $($selectedTypes -join ', ') ($Configuration)" -ForegroundColor White

foreach ($type in $selectedTypes) {
    switch ($type) {
        "Application" {
            foreach ($targetRid in (Resolve-Rids $Rid)) { Publish-Application $targetRid }
        }
        "Server" {
            foreach ($targetRid in (Resolve-Rids $ServerRid)) { Publish-Server $targetRid }
        }
        "Runtime" {
            foreach ($targetRid in (Resolve-Rids $RuntimeRid)) { Publish-Runtime $targetRid }
        }
    }
}

Write-Host ""
Write-Host "SharpClaw publish results" -ForegroundColor White
foreach ($result in $results) {
    $status = if ($result.Ok) { "OK" } else { "FAILED" }
    Write-Host "  [$status] $($result.Type)/$($result.Target) $($result.SizeMB) MB $($result.Artifact)"
}

if (($results | Where-Object { -not $_.Ok }).Count -gt 0) { exit 1 }
