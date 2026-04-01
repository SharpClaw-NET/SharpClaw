<#
.SYNOPSIS
    Publishes SharpClaw (Uno desktop + API backend + public gateway) as a
    self-contained distributable for GitHub Releases.

.DESCRIPTION
    Produces a self-contained distributable per platform:
      - SharpClaw.Uno.exe      (Uno desktop app, self-contained, R2R)
      - backend\               (API backend, self-contained, R2R)
      - gateway\               (Public gateway proxy, self-contained, R2R)

    Platform packages (unless -SkipPackaging):
      - Windows:  .msix  (via MakeAppx.exe from Windows SDK)
      - Linux:    .snap  (via snapcraft) and .flatpak (via flatpak-builder)

    Neither project is trimmed
    (anonymous types, DTOs) that the IL trimmer cannot preserve.
    Uses CoreCLR runtime (UseMonoRuntime=false) because the Mono runtime
    NuGet packages are not published for .NET 10.

    The Uno app automatically launches the backend as a hidden child process
    (no terminal window required). The gateway is optional — enable it via
    the Application Interface .env editor. Double-click SharpClaw.Uno.exe
    to run.

.PARAMETER Rid
    Runtime identifier. Default: win-x64.
    Supported: win-x64, linux-x64, linux-arm64, osx-arm64.
    Shorthands: "win" is an alias for win-x64.
               "linux" publishes both linux-x64 and linux-arm64.
               "osx" is an alias for osx-arm64 (Apple Silicon).
               "all" publishes every supported RID.
    Note: win-arm64 is NOT supported by Uno desktop (Skia). Use win-x64
    on ARM64 Windows -- it runs under x64 emulation with no issues.

.PARAMETER Configuration
    Build configuration. Default: Release.

.PARAMETER OutputDir
    Final output directory for the zip. Default: publish\ at repo root.

.PARAMETER SkipZip
    If set, skips creating the zip archive (useful for local testing).

.PARAMETER SkipPackaging
    If set, skips platform-specific packaging (MSIX, Snap, Flatpak).
    The base publish and zip archive are still produced.

.EXAMPLE
    .\publish-release.ps1
    .\publish-release.ps1 -Rid osx
    .\publish-release.ps1 -Rid all
    .\publish-release.ps1 -Rid linux -SkipZip
    .\publish-release.ps1 -Rid all -SkipPackaging
#>
param(
    [string]$Rid = "win-x64",
    [string]$Configuration = "Release",
    [string]$OutputDir = (Join-Path $PSScriptRoot "publish"),
    [switch]$SkipZip,
    [switch]$SkipPackaging
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# -- RID shorthand expansion --
$supportedRids = @(
    "win-x64",
    "linux-x64", "linux-arm64",
    "osx-arm64"
)
$ridGroups = @{
    "win"   = @("win-x64")
    "linux" = @("linux-x64", "linux-arm64")
    "osx"   = @("osx-arm64")
    "all"   = $supportedRids
}

if ($ridGroups.ContainsKey($Rid)) {
    $ridsToPublish = $ridGroups[$Rid]
} elseif ($supportedRids -contains $Rid) {
    $ridsToPublish = @($Rid)
} else {
    Write-Error ("RID '$Rid' is not supported for Uno desktop (Skia) builds.`n" +
        "Supported RIDs: $($supportedRids -join ', ')`n" +
        "Shorthands: win, linux, osx, all`n" +
        "Note: On ARM64 Windows, use 'win-x64' -- it runs under x64 emulation with no issues.`n" +
        "Note: osx-arm64 targets Apple Silicon (M1/M2/M3/M4). Intel Macs are no longer supported.")
    exit 1
}

$repoRoot   = $PSScriptRoot
$unoProject = Join-Path (Join-Path $repoRoot "SharpClaw.Uno") "SharpClaw.Uno.csproj"

# -- Version helper --
function Get-ProjectVersion {
    $propsPath = Join-Path $repoRoot "Directory.Build.props"
    if (-not (Test-Path $propsPath)) { return "0.0.0" }
    [xml]$xml = Get-Content $propsPath -Raw
    $v = $xml.Project.PropertyGroup.Version
    if ($v) { return $v } else { return "0.0.0" }
}

# -- MSIX packaging (Windows) --
function Package-Msix {
    param([string]$StageDir, [string]$TargetRid)

    Write-Host ""
    Write-Host "-- MSIX Packaging ($TargetRid) --" -ForegroundColor Cyan

    # Locate MakeAppx.exe in Windows 10/11 SDK
    $makeAppx = $null
    $sdkBin = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path $sdkBin) {
        $sdkVer = Get-ChildItem $sdkBin -Directory |
                  Where-Object { $_.Name -match '^\d+\.' } |
                  Sort-Object Name -Descending | Select-Object -First 1
        if ($sdkVer) {
            $c = Join-Path $sdkVer.FullName "x64\makeappx.exe"
            if (Test-Path $c) { $makeAppx = $c }
        }
    }
    if (-not $makeAppx) {
        Write-Warning "MakeAppx.exe not found — install the Windows 10/11 SDK to enable MSIX packaging."
        return
    }
    Write-Host "  Tool: $makeAppx" -ForegroundColor DarkGray

    $version = Get-ProjectVersion
    $vParts = $version.Split('-')[0].Split('.')
    while ($vParts.Count -lt 4) { $vParts += "0" }
    $msixVer = $vParts[0..3] -join '.'

    $iconSrc = Join-Path $repoRoot "SharpClaw.Uno\Environment\icon.png"

    # Inject Assets/StoreLogo.png into stage dir for the manifest
    $assetsDir = Join-Path $StageDir "Assets"
    $createdAssets = -not (Test-Path $assetsDir)
    if ($createdAssets) { New-Item $assetsDir -ItemType Directory | Out-Null }
    Copy-Item $iconSrc (Join-Path $assetsDir "StoreLogo.png") -Force

    # Generate a fully-resolved AppxManifest.xml
    # (Package.appxmanifest uses MSBuild variables that MakeAppx cannot process)
    $manifestDst = Join-Path $StageDir "AppxManifest.xml"
    $xml = @(
        '<?xml version="1.0" encoding="utf-8"?>',
        '<Package',
        '  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"',
        '  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"',
        '  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"',
        '  IgnorableNamespaces="uap rescap">',
        '  <Identity Name="com.mkn8rn.SharpClaw"',
        '           Publisher="CN=SharpClaw Dev"',
        "           Version=`"$msixVer`" />",
        '  <Properties>',
        '    <DisplayName>SharpClaw</DisplayName>',
        '    <PublisherDisplayName>marko</PublisherDisplayName>',
        '    <Logo>Assets\StoreLogo.png</Logo>',
        '  </Properties>',
        '  <Dependencies>',
        '    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.26100.0" />',
        '  </Dependencies>',
        '  <Resources>',
        '    <Resource Language="en-us" />',
        '  </Resources>',
        '  <Applications>',
        '    <Application Id="App" Executable="SharpClaw.Uno.exe" EntryPoint="Windows.FullTrustApplication">',
        '      <uap:VisualElements DisplayName="SharpClaw" Description="SharpClaw AI Agent Platform"',
        '        BackgroundColor="#0D0D0D" Square150x150Logo="Assets\StoreLogo.png"',
        '        Square44x44Logo="Assets\StoreLogo.png" />',
        '    </Application>',
        '  </Applications>',
        '  <Capabilities>',
        '    <Capability Name="internetClient" />',
        '    <Capability Name="privateNetworkClientServer" />',
        '    <rescap:Capability Name="runFullTrust" />',
        '    <DeviceCapability Name="microphone" />',
        '  </Capabilities>',
        '</Package>'
    )
    $xml -join "`r`n" | Set-Content $manifestDst -Encoding UTF8

    # Pack MSIX
    $msixPath = Join-Path $OutputDir "SharpClaw-$msixVer-$TargetRid.msix"
    if (Test-Path $msixPath) { Remove-Item $msixPath -Force }
    & $makeAppx pack /d $StageDir /p $msixPath /o
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "MSIX packaging failed (exit code $LASTEXITCODE)."
    } else {
        $sz = [math]::Round((Get-Item $msixPath).Length / 1MB, 1)
        Write-Host "  MSIX: $msixPath ($sz MB)" -ForegroundColor Green
        Write-Host "  Unsigned — use SignTool for distribution." -ForegroundColor DarkGray
    }

    # Clean injected packaging files from stage dir
    Remove-Item $manifestDst -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $assetsDir "StoreLogo.png") -Force -ErrorAction SilentlyContinue
    if ($createdAssets) { Remove-Item $assetsDir -Force -ErrorAction SilentlyContinue }
}

# -- Snap packaging (Linux) --
function Package-Snap {
    param([string]$StageDir, [string]$TargetRid)

    Write-Host ""
    Write-Host "-- Snap Packaging ($TargetRid) --" -ForegroundColor Cyan

    if (-not (Get-Command snapcraft -ErrorAction SilentlyContinue)) {
        Write-Warning "snapcraft not found — install it to enable Snap packaging."
        Write-Warning "  Linux: sudo snap install snapcraft --classic"
        Write-Warning "  Template: packaging/snap/snapcraft.yaml"
        return
    }

    $version  = Get-ProjectVersion
    $snapArch = if ($TargetRid -like "*arm64") { "arm64" } else { "amd64" }

    $workDir = Join-Path $OutputDir "_snap-$TargetRid"
    if (Test-Path $workDir) { Remove-Item $workDir -Recurse -Force }
    New-Item $workDir -ItemType Directory | Out-Null
    New-Item (Join-Path $workDir "snap")     -ItemType Directory | Out-Null
    New-Item (Join-Path $workDir "snap\gui") -ItemType Directory | Out-Null

    Copy-Item $StageDir -Destination (Join-Path $workDir "publish") -Recurse

    $yaml = (Get-Content (Join-Path $repoRoot "packaging\snap\snapcraft.yaml") -Raw) -replace '__VERSION__', $version
    Set-Content (Join-Path $workDir "snap\snapcraft.yaml") $yaml -Encoding UTF8
    Copy-Item (Join-Path $repoRoot "packaging\snap\gui\sharpclaw.desktop") (Join-Path $workDir "snap\gui\sharpclaw.desktop") -Force
    Copy-Item (Join-Path $repoRoot "SharpClaw.Uno\Environment\icon.png")   (Join-Path $workDir "snap\gui\icon.png")          -Force

    Push-Location $workDir
    try {
        & snapcraft pack --build-for $snapArch
        if ($LASTEXITCODE -ne 0) { Write-Warning "Snap packaging failed (exit code $LASTEXITCODE)."; return }
        $snap = Get-ChildItem $workDir -Filter "*.snap" | Select-Object -First 1
        if ($snap) {
            $dest = Join-Path $OutputDir $snap.Name
            Move-Item $snap.FullName $dest -Force
            $sz = [math]::Round((Get-Item $dest).Length / 1MB, 1)
            Write-Host "  Snap: $dest ($sz MB)" -ForegroundColor Green
        }
    } finally {
        Pop-Location
        Remove-Item $workDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# -- Flatpak packaging (Linux) --
function Package-Flatpak {
    param([string]$StageDir, [string]$TargetRid)

    Write-Host ""
    Write-Host "-- Flatpak Packaging ($TargetRid) --" -ForegroundColor Cyan

    if (-not (Get-Command flatpak-builder -ErrorAction SilentlyContinue)) {
        Write-Warning "flatpak-builder not found — install it to enable Flatpak packaging."
        Write-Warning "  Linux: sudo apt install flatpak-builder"
        Write-Warning "  Manifest: packaging/flatpak/com.mkn8rn.SharpClaw.yml"
        return
    }

    $version = Get-ProjectVersion
    $fpArch  = if ($TargetRid -like "*arm64") { "aarch64" } else { "x86_64" }

    $workDir = Join-Path $OutputDir "_flatpak-$TargetRid"
    if (Test-Path $workDir) { Remove-Item $workDir -Recurse -Force }
    New-Item $workDir -ItemType Directory | Out-Null
    Copy-Item $StageDir -Destination (Join-Path $workDir "publish") -Recurse

    # Stamp metainfo with version and date
    $meta = (Get-Content (Join-Path $repoRoot "packaging\flatpak\com.mkn8rn.SharpClaw.metainfo.xml") -Raw)
    $meta = $meta -replace '__VERSION__', $version
    $meta = $meta -replace '__DATE__', (Get-Date -Format "yyyy-MM-dd")
    Set-Content (Join-Path $workDir "com.mkn8rn.SharpClaw.metainfo.xml") $meta -Encoding UTF8

    Copy-Item (Join-Path $repoRoot "packaging\flatpak\com.mkn8rn.SharpClaw.yml")     (Join-Path $workDir "com.mkn8rn.SharpClaw.yml")     -Force
    Copy-Item (Join-Path $repoRoot "packaging\flatpak\com.mkn8rn.SharpClaw.desktop")  (Join-Path $workDir "com.mkn8rn.SharpClaw.desktop")  -Force
    Copy-Item (Join-Path $repoRoot "SharpClaw.Uno\Environment\icon.png")              (Join-Path $workDir "icon.png")                      -Force

    $buildDir = Join-Path $workDir "build"
    $repoDir  = Join-Path $workDir "repo"

    Push-Location $workDir
    try {
        & flatpak-builder --force-clean "--repo=$repoDir" "--arch=$fpArch" $buildDir "com.mkn8rn.SharpClaw.yml"
        if ($LASTEXITCODE -ne 0) { Write-Warning "Flatpak build failed (exit code $LASTEXITCODE)."; return }

        $bundlePath = Join-Path $OutputDir "SharpClaw-$version-$TargetRid.flatpak"
        & flatpak build-bundle $repoDir $bundlePath com.mkn8rn.SharpClaw "--arch=$fpArch"
        if ($LASTEXITCODE -ne 0) { Write-Warning "Flatpak bundle creation failed."; return }

        $sz = [math]::Round((Get-Item $bundlePath).Length / 1MB, 1)
        Write-Host "  Flatpak: $bundlePath ($sz MB)" -ForegroundColor Green
    } finally {
        Pop-Location
        Remove-Item $workDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# -- Publish function --
function Publish-SharpClaw {
    param([string]$TargetRid)

    $stageDir = Join-Path $OutputDir "SharpClaw-$TargetRid"
    $zipPath  = Join-Path $OutputDir "SharpClaw-$TargetRid.zip"

    Write-Host ""
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "  SharpClaw Release Build" -ForegroundColor Green
    Write-Host "  RID:    $TargetRid" -ForegroundColor Green
    Write-Host "  Config: $Configuration" -ForegroundColor Green
    Write-Host "  Output: $stageDir" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host ""

    # Clean previous output
    if (Test-Path $stageDir) {
        Write-Host "Cleaning previous build at $stageDir ..." -ForegroundColor Yellow
        Remove-Item $stageDir -Recurse -Force
    }
    if (Test-Path $zipPath) {
        Remove-Item $zipPath -Force
    }

    # All Uno desktop targets use the same TFM
    $tfm = "net10.0-desktop"

    # Publish
    Write-Host "Publishing Uno app + bundled backend + gateway ..." -ForegroundColor Cyan
    Write-Host "  TFM: $tfm | RID: $TargetRid | Self-contained + R2R (no trimming)" -ForegroundColor DarkGray

    $publishArgs = @(
        "publish", $unoProject,
        "-c", $Configuration,
        "-f", $tfm,
        "-r", $TargetRid,
        "--self-contained",
        "-p:BundleBackend=true",
        "-p:UseMonoRuntime=false",
        "-p:PublishReadyToRun=true",
        "-o", $stageDir
    )

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Publish failed for $TargetRid with exit code $LASTEXITCODE"
        return $false
    }

    # Verify backend was bundled
    $backendDir = Join-Path $stageDir "backend"
    $isWin = $TargetRid -like "win-*"
    $backendExe = if ($isWin) {
        Join-Path $backendDir "SharpClaw.Application.API.exe"
    } else {
        Join-Path $backendDir "SharpClaw.Application.API"
    }

    if (-not (Test-Path $backendExe)) {
        Write-Error "Backend executable not found at '$backendExe'. The BundleBackend target may have failed."
        return $false
    }

    Write-Host ""
    Write-Host "Backend bundled at: $backendExe" -ForegroundColor Green

    # Verify gateway was bundled
    $gatewayDir = Join-Path $stageDir "gateway"
    $gatewayExe = if ($isWin) {
        Join-Path $gatewayDir "SharpClaw.Gateway.exe"
    } else {
        Join-Path $gatewayDir "SharpClaw.Gateway"
    }

    if (-not (Test-Path $gatewayExe)) {
        Write-Warning "Gateway executable not found at '$gatewayExe'. The BundleGateway target may have failed — gateway will not be available."
    } else {
        Write-Host "Gateway bundled at: $gatewayExe" -ForegroundColor Green
    }

    # -- Strip foreign-platform native libraries to reclaim ~500+ MB --
    # The LLamaSharp platform-conditional packages handle the bulk of this at
    # NuGet-restore time, but leftover runtimes/ directories for non-target
    # RIDs can still sneak in from transitive dependencies. This cleanup pass
    # removes them along with the unused VLC architecture (e.g. win-x86 when
    # publishing for win-x64).
    $ridArch  = ($TargetRid -split '-')[-1]          # x64, arm64, etc.
    $ridOs    = ($TargetRid -split '-')[0]            # win, linux, osx

    # Remove non-target-RID runtimes/ sub-folders from both frontend and backend
    foreach ($runtimesDir in (Get-ChildItem $stageDir -Recurse -Directory -Filter "runtimes" -ErrorAction SilentlyContinue)) {
        foreach ($ridDir in (Get-ChildItem $runtimesDir.FullName -Directory -ErrorAction SilentlyContinue)) {
            $name = $ridDir.Name
            # Keep only folders that match the target OS prefix (win, linux, osx)
            $matchesOs = $name -like "$ridOs-*" -or $name -eq $ridOs
            if (-not $matchesOs) {
                Remove-Item $ridDir.FullName -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    # Remove non-target VLC architecture (e.g. libvlc/win-x86 when publishing win-x64)
    $vlcDir = Join-Path $stageDir "libvlc"
    if (Test-Path $vlcDir) {
        foreach ($archDir in (Get-ChildItem $vlcDir -Directory -ErrorAction SilentlyContinue)) {
            if ($archDir.Name -notlike "*$ridArch*") {
                Remove-Item $archDir.FullName -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    $cleanedSize = [math]::Round(
        ((Get-ChildItem $stageDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB), 1
    )
    Write-Host "  After cleanup:  $cleanedSize MB (stripped foreign-platform natives)" -ForegroundColor DarkGray

    # Summary
    $unoExe = if ($isWin) {
        Join-Path $stageDir "SharpClaw.Uno.exe"
    } else {
        Join-Path $stageDir "SharpClaw.Uno"
    }

    $frontendSize = if (Test-Path $unoExe) {
        [math]::Round((Get-Item $unoExe).Length / 1MB, 1)
    } else { "?" }

    $totalSize = [math]::Round(
        ((Get-ChildItem $stageDir -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB), 1
    )

    Write-Host ""
    Write-Host "-- Build Summary ($TargetRid) --" -ForegroundColor Green
    Write-Host "  Frontend:   $frontendSize MB" -ForegroundColor White
    Write-Host "  Total size: $totalSize MB" -ForegroundColor White
    Write-Host "  Output:     $stageDir" -ForegroundColor White

    # Zip for GitHub release
    if (-not $SkipZip) {
        Write-Host ""
        Write-Host "Creating zip archive ..." -ForegroundColor Cyan
        Compress-Archive -Path "$stageDir\*" -DestinationPath $zipPath -Force
        $zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
        Write-Host "  Archive: $zipPath ($zipSize MB)" -ForegroundColor Green
    }

    return $true
}

# -- Run publishes + packaging --
$failed = @()
foreach ($rid in $ridsToPublish) {
    $ok = Publish-SharpClaw -TargetRid $rid
    if (-not $ok) { $failed += $rid; continue }

    if (-not $SkipPackaging) {
        $stageDir = Join-Path $OutputDir "SharpClaw-$rid"
        if ($rid -like "win-*")   { Package-Msix    -StageDir $stageDir -TargetRid $rid }
        if ($rid -like "linux-*") { Package-Snap    -StageDir $stageDir -TargetRid $rid }
        if ($rid -like "linux-*") { Package-Flatpak -StageDir $stageDir -TargetRid $rid }
    }
}

# -- Final report --
Write-Host ""
if ($ridsToPublish.Count -gt 1) {
    $succeeded = $ridsToPublish.Count - $failed.Count
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "  Published $succeeded / $($ridsToPublish.Count) targets" -ForegroundColor Green
    if ($failed.Count -gt 0) {
        Write-Host "  Failed: $($failed -join ', ')" -ForegroundColor Red
    }
    Write-Host "========================================" -ForegroundColor Green
}
Write-Host "Done. No terminal required to run -- double-click the app executable." -ForegroundColor Green
Write-Host "Gateway is opt-in: enable it from the .env editor (Application Interface)." -ForegroundColor DarkGray
Write-Host ""

if ($failed.Count -gt 0) { exit 1 }
