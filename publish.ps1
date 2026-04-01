<#
.SYNOPSIS
    Unified publish script for SharpClaw. Builds every supported release
    type across all platforms in a single invocation.

.DESCRIPTION
    Consolidates all SharpClaw distribution workflows into one script:

      +-------------+---------------------------------------------------+
      | Type        | Description                                       |
      +-------------+---------------------------------------------------+
      | Desktop     | Self-contained folder + zip (GitHub Releases).    |
      |             | Platforms: win-x64, linux-x64, linux-arm64,       |
      |             | osx-x64, osx-arm64.                               |
      | MSIX        | Windows MSIX (sideload + store). Windows only.    |
      | Server      | Headless API + Gateway (no Uno frontend).         |
      |             | For deployment on a remote server / Docker host.   |
      | WASM        | Static WebAssembly build of the Uno frontend.     |
      +-------------+---------------------------------------------------+

    By default, publishes everything. Use -Include / -Exclude to filter.
    Outputs are placed under publish\ (configurable via -OutputDir).

    The gateway is always bundled alongside the backend in Desktop and
    MSIX builds. For Server builds the gateway is a peer to the API.

.PARAMETER Include
    Comma-separated list of build types to include.
    Values: Desktop, MSIX, Server, WASM, All (default: All).

.PARAMETER Exclude
    Comma-separated list of build types to skip.
    Applied after -Include. Example: -Exclude MSIX,WASM

.PARAMETER Rid
    Runtime identifier(s) for Desktop builds.
    Default: all.  Shorthands: win, linux, osx, all.

.PARAMETER MsixMode
    MSIX variants: Both, Sideload, Store. Default: Both.

.PARAMETER MsixVersion
    Package version for MSIX (Major.Minor.Build.0). Default: 0.0.2.0.

.PARAMETER MsixRid
    RID for the MSIX build. Default: win-x64.

.PARAMETER ServerRid
    RID(s) for Server builds.
    Default: all.  Shorthands: win, linux, osx, all.

.PARAMETER Configuration
    Build configuration. Default: Release.

.PARAMETER OutputDir
    Root output directory. Default: publish\ at repo root.

.PARAMETER SkipZip
    If set, skips creating zip archives for Desktop and Server builds.

.PARAMETER Parallel
    If set, publishes multiple RIDs concurrently (Desktop + Server).
    Reduces wall-clock time on multi-core machines but uses more RAM.

.PARAMETER CertPassword
    PFX password for MSIX sideload signing.
    Default: $env:MSIX_CERT_PASSWORD or "SharpClaw123!".

.PARAMETER IdentityName
    MSIX identity name. Default: mkn8rn.SharpClaw.

.PARAMETER IdentityPublisher
    MSIX publisher. Default: CN=SharpClaw Dev.

.PARAMETER PublisherDisplayName
    MSIX display name. Default: marko.

.EXAMPLE
    .\publish.ps1                                    # Everything
    .\publish.ps1 -Include Desktop -Rid win          # Desktop win-x64 only
    .\publish.ps1 -Include Desktop,Server -Rid all   # Desktop + Server, all RIDs
    .\publish.ps1 -Exclude WASM,MSIX                 # Desktop + Server only
    .\publish.ps1 -Include MSIX -MsixMode Sideload   # MSIX sideload only
    .\publish.ps1 -Include Server -ServerRid linux    # Headless server for Linux
    .\publish.ps1 -Include WASM                       # WASM static site only
    .\publish.ps1 -Parallel                           # Full build, RIDs in parallel
#>
param(
    [string]$Include               = "All",
    [string]$Exclude               = "",
    [string]$Rid                   = "all",
    [ValidateSet("Both", "Sideload", "Store")]
    [string]$MsixMode              = "Both",
    [string]$MsixVersion           = "0.0.2.0",
    [string]$MsixRid               = "win-x64",
    [string]$ServerRid             = "all",
    [string]$Configuration         = "Release",
    [string]$OutputDir             = (Join-Path $PSScriptRoot "publish"),
    [switch]$SkipZip,
    [switch]$Parallel,
    [string]$CertPassword          = $(if ($env:MSIX_CERT_PASSWORD) { $env:MSIX_CERT_PASSWORD } else { "SharpClaw123!" }),
    [string]$IdentityName          = "mkn8rn.SharpClaw",
    [string]$IdentityPublisher     = "CN=SharpClaw Dev",
    [string]$PublisherDisplayName  = "marko"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# ===================================================================
#  Constants & paths
# ===================================================================

$repoRoot       = $PSScriptRoot
$unoProject     = Join-Path (Join-Path $repoRoot "SharpClaw.Uno") "SharpClaw.Uno.csproj"
$apiProject     = Join-Path (Join-Path $repoRoot "SharpClaw.Application.API") "SharpClaw.Application.API.csproj"
$gatewayProject = Join-Path (Join-Path $repoRoot "SharpClaw.Gateway") "SharpClaw.Gateway.csproj"
$signingDir     = Join-Path $repoRoot "signing"

$desktopTfm = "net10.0-desktop"
$wasmTfm    = "net10.0-browserwasm"
$serverTfm  = "net10.0"

$supportedDesktopRids = @("win-x64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")
$supportedServerRids  = @("win-x64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")

$ridGroups = @{
    "win"   = @("win-x64")
    "linux" = @("linux-x64", "linux-arm64")
    "osx"   = @("osx-x64", "osx-arm64")
    "all"   = $null  # filled per-context
}

# ===================================================================
#  Resolve build types
# ===================================================================

$allTypes = @("Desktop", "MSIX", "Server", "WASM")

function Resolve-Types {
    param([string]$Inc, [string]$Exc)
    $set = if ($Inc -eq "All") { $allTypes } else { $Inc -split ',' | ForEach-Object { $_.Trim() } }
    if ($Exc) {
        $excSet = $Exc -split ',' | ForEach-Object { $_.Trim() }
        $set = $set | Where-Object { $_ -notin $excSet }
    }
    foreach ($t in $set) {
        if ($t -notin $allTypes) {
            Write-Error "Unknown build type '$t'. Valid: $($allTypes -join ', '), All"
            exit 1
        }
    }
    return @($set)
}

$buildTypes = Resolve-Types $Include $Exclude

function Resolve-Rids {
    param([string]$RidValue, [string[]]$Supported)
    $groups = @{
        "win"   = @("win-x64")
        "linux" = @("linux-x64", "linux-arm64")
        "osx"   = @("osx-x64", "osx-arm64")
        "all"   = $Supported
    }
    if ($groups.ContainsKey($RidValue)) { return $groups[$RidValue] }
    if ($RidValue -in $Supported) { return @($RidValue) }
    Write-Error "RID '$RidValue' is not supported. Valid: $($Supported -join ', '), win, linux, osx, all"
    exit 1
}

# ===================================================================
#  Tracking
# ===================================================================

$global:results = [System.Collections.Generic.List[PSCustomObject]]::new()

function Add-Result {
    param([string]$Type, [string]$Target, [bool]$Ok, [string]$Artifact = "", [double]$SizeMB = 0)
    $global:results.Add([PSCustomObject]@{
        Type     = $Type
        Target   = $Target
        Ok       = $Ok
        Artifact = $Artifact
        SizeMB   = $SizeMB
    })
}

# ===================================================================
#  Shared helpers
# ===================================================================

function Get-ExeName {
    param([string]$BaseName, [string]$TargetRid)
    if ($TargetRid -like "win-*") { return "$BaseName.exe" } else { return $BaseName }
}

function Get-DirSizeMB {
    param([string]$Path)
    [math]::Round(((Get-ChildItem $Path -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
}

function Strip-ForeignNatives {
    param([string]$StageDir, [string]$TargetRid)

    $ridArch = ($TargetRid -split '-')[-1]
    $ridOs   = ($TargetRid -split '-')[0]

    foreach ($rDir in (Get-ChildItem $StageDir -Recurse -Directory -Filter "runtimes" -ErrorAction SilentlyContinue)) {
        foreach ($sub in (Get-ChildItem $rDir.FullName -Directory -ErrorAction SilentlyContinue)) {
            if ($sub.Name -notlike "$ridOs-*" -and $sub.Name -ne $ridOs) {
                Remove-Item $sub.FullName -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    $vlcDir = Join-Path $StageDir "libvlc"
    if (Test-Path $vlcDir) {
        foreach ($archDir in (Get-ChildItem $vlcDir -Directory -ErrorAction SilentlyContinue)) {
            if ($archDir.Name -notlike "*$ridArch*") {
                Remove-Item $archDir.FullName -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

function New-ZipArchive {
    param([string]$SourceDir, [string]$ZipPath)
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    Compress-Archive -Path "$SourceDir\*" -DestinationPath $ZipPath -Force
    $sz = [math]::Round((Get-Item $ZipPath).Length / 1MB, 1)
    Write-Host "  Archive: $ZipPath `($sz MB`)" -ForegroundColor Green
    return $sz
}

# ===================================================================
#  Desktop: self-contained folder + zip for GitHub Releases
# ===================================================================

function Publish-Desktop {
    param([string]$TargetRid)

    $label    = "Desktop/$TargetRid"
    $stageDir = Join-Path $OutputDir "SharpClaw-$TargetRid"
    $zipPath  = Join-Path $OutputDir "SharpClaw-$TargetRid.zip"

    Write-Host ""
    Write-Host "-- Desktop: $TargetRid ------------------------------" -ForegroundColor Green

    if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
    if (Test-Path $zipPath)  { Remove-Item $zipPath -Force }

    $publishArgs = @(
        "publish", $unoProject,
        "-c", $Configuration,
        "-f", $desktopTfm,
        "-r", $TargetRid,
        "--self-contained",
        "-p:BundleBackend=true",
        "-p:UseMonoRuntime=false",
        "-p:PublishReadyToRun=true",
        "-o", $stageDir
    )

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  [X] Publish failed (exit $LASTEXITCODE)" -ForegroundColor Red
        Add-Result "Desktop" $TargetRid $false
        return
    }

    # Verify backend
    $backendExe = Join-Path (Join-Path $stageDir "backend") (Get-ExeName "SharpClaw.Application.API" $TargetRid)
    if (-not (Test-Path $backendExe)) {
        Write-Host "  [X] Backend not found at $backendExe" -ForegroundColor Red
        Add-Result "Desktop" $TargetRid $false
        return
    }
    Write-Host "  Backend:  $backendExe" -ForegroundColor DarkGray

    # Verify gateway (non-fatal)
    $gatewayExe = Join-Path (Join-Path $stageDir "gateway") (Get-ExeName "SharpClaw.Gateway" $TargetRid)
    if (Test-Path $gatewayExe) {
        Write-Host "  Gateway:  $gatewayExe" -ForegroundColor DarkGray
    } else {
        Write-Warning "  Gateway not found -- will not be available in this build."
    }

    Strip-ForeignNatives $stageDir $TargetRid

    $totalMB = Get-DirSizeMB $stageDir
    Write-Host "  Size:     $totalMB MB (after cleanup)" -ForegroundColor DarkGray

    $zipMB = 0
    if (-not $SkipZip) {
        $zipMB = New-ZipArchive $stageDir $zipPath
    }

    Write-Host "  [OK] Desktop/$TargetRid complete" -ForegroundColor Green
    Add-Result "Desktop" $TargetRid $true $stageDir $totalMB
}

# ===================================================================
#  MSIX: Windows sideload + store
# ===================================================================

function Publish-MSIX {
    $label = "MSIX/$MsixRid"

    Write-Host ""
    Write-Host "-- MSIX: $MsixRid `($MsixMode`) ----------------------" -ForegroundColor Cyan

    # Validate version
    if ($MsixVersion -notmatch '^\d+\.\d+\.\d+\.0$') {
        Write-Host "  [X] MsixVersion must be Major.Minor.Build.0 -- got: $MsixVersion" -ForegroundColor Red
        Add-Result "MSIX" $MsixRid $false
        return
    }

    # Locate SDK tools
    $makeappx = Find-SdkTool "makeappx.exe"
    if (-not $makeappx) {
        Write-Host "  [X] makeappx.exe not found. Install the Windows SDK." -ForegroundColor Red
        Add-Result "MSIX" $MsixRid $false
        return
    }

    $buildSideload = $MsixMode -in @("Both", "Sideload")
    $buildStore    = $MsixMode -in @("Both", "Store")

    $signtool = $null
    if ($buildSideload) {
        $signtool = Find-SdkTool "signtool.exe"
        if (-not $signtool) {
            Write-Host "  [X] signtool.exe not found." -ForegroundColor Red
            Add-Result "MSIX" $MsixRid $false
            return
        }
    }

    # Certificate for sideload
    $certFile   = Join-Path $signingDir "SharpClaw-Dev.pfx"
    $certPublic = Join-Path $signingDir "SharpClaw-Dev.cer"

    if ($buildSideload) {
        if (-not (Test-Path $signingDir)) {
            New-Item $signingDir -ItemType Directory -Force | Out-Null
        }

        if (-not (Test-Path $certFile)) {
            Write-Host "  Generating self-signed certificate `($IdentityPublisher`) ..." -ForegroundColor Yellow
            $cert = New-SelfSignedCertificate `
                -Type Custom `
                -Subject $IdentityPublisher `
                -KeyUsage DigitalSignature `
                -FriendlyName "SharpClaw Dev Signing" `
                -CertStoreLocation "Cert:\CurrentUser\My" `
                -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')

            $securePwd = ConvertTo-SecureString $CertPassword -Force -AsPlainText
            Export-PfxCertificate -Cert $cert -FilePath $certFile -Password $securePwd | Out-Null
            [System.IO.File]::WriteAllBytes(
                $certPublic,
                $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
            Write-Host "  PFX: $certFile" -ForegroundColor Green
            Write-Host "  CER: $certPublic" -ForegroundColor Green
        } else {
            Write-Host "  Using existing certificate: $certFile" -ForegroundColor DarkGray
            if (-not (Test-Path $certPublic)) {
                $pfxObj = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certFile, $CertPassword)
                [System.IO.File]::WriteAllBytes(
                    $certPublic,
                    $pfxObj.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
            }
        }
    }

    # Publish to staging
    $stageDir = Join-Path $OutputDir "SharpClaw-msix-stage"
    if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }

    $publishArgs = @(
        "publish", $unoProject,
        "-c", $Configuration,
        "-f", $desktopTfm,
        "-r", $MsixRid,
        "--self-contained",
        "-p:BundleBackend=true",
        "-p:UseMonoRuntime=false",
        "-p:PublishReadyToRun=true",
        "-o", $stageDir
    )

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  [X] Publish failed (exit $LASTEXITCODE)" -ForegroundColor Red
        Add-Result "MSIX" $MsixRid $false
        return
    }

    # Strip
    Strip-ForeignNatives $stageDir $MsixRid

    # Remove PDBs
    $pdbCount = (Get-ChildItem $stageDir -Recurse -Filter "*.pdb" -ErrorAction SilentlyContinue).Count
    Get-ChildItem $stageDir -Recurse -Filter "*.pdb" -ErrorAction SilentlyContinue | Remove-Item -Force
    Write-Host "  Removed $pdbCount PDB files" -ForegroundColor DarkGray

    # Generate AppxManifest.xml
    $msixArch = switch -Wildcard ($MsixRid) {
        "*-x64"   { "x64" }
        "*-x86"   { "x86" }
        "*-arm64" { "arm64" }
        default   { "x64" }
    }

    $assetsDir = Join-Path $stageDir "Assets"
    if (-not (Test-Path $assetsDir)) { New-Item $assetsDir -ItemType Directory -Force | Out-Null }
    $iconSrc = Join-Path (Join-Path (Join-Path $repoRoot "SharpClaw.Uno") "Environment") "icon.png"
    if (Test-Path $iconSrc) { Copy-Item $iconSrc (Join-Path $assetsDir "icon.png") -Force }

    $manifest = @(
        '<?xml version="1.0" encoding="utf-8"?>'
        '<Package'
        '  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"'
        '  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"'
        '  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"'
        '  IgnorableNamespaces="uap rescap">'
        ''
        '  <Identity'
        "    Name=`"$IdentityName`""
        "    Version=`"$MsixVersion`""
        "    Publisher=`"$IdentityPublisher`""
        "    ProcessorArchitecture=`"$msixArch`" />"
        ''
        '  <Properties>'
        '    <DisplayName>SharpClaw</DisplayName>'
        ('    <PublisherDisplayName>' + $PublisherDisplayName + '</PublisherDisplayName>')
        '    <Logo>Assets\icon.png</Logo>'
        '    <Description>SharpClaw - AI agent orchestrator with local model inference, chat, transcription, and developer tool integration.</Description>'
        '  </Properties>'
        ''
        '  <Dependencies>'
        '    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.26100.0" />'
        '  </Dependencies>'
        ''
        '  <Resources>'
        '    <Resource Language="en-us" />'
        '  </Resources>'
        ''
        '  <Applications>'
        '    <Application Id="SharpClaw"'
        '      Executable="SharpClaw.Uno.exe"'
        '      EntryPoint="Windows.FullTrustApplication">'
        '      <uap:VisualElements'
        '        DisplayName="SharpClaw"'
        '        Description="AI agent orchestrator"'
        '        BackgroundColor="transparent"'
        '        Square150x150Logo="Assets\icon.png"'
        '        Square44x44Logo="Assets\icon.png" />'
        '    </Application>'
        '  </Applications>'
        ''
        '  <Capabilities>'
        '    <Capability Name="internetClient" />'
        '    <Capability Name="privateNetworkClientServer" />'
        '    <rescap:Capability Name="runFullTrust" />'
        '    <DeviceCapability Name="microphone" />'
        '  </Capabilities>'
        '</Package>'
    ) -join "`r`n"

    [System.IO.File]::WriteAllText((Join-Path $stageDir "AppxManifest.xml"), $manifest, [System.Text.Encoding]::UTF8)
    $templateManifest = Join-Path $stageDir "Package.appxmanifest"
    if (Test-Path $templateManifest) { Remove-Item $templateManifest -Force }

    # Package variants
    $msixFailed = @()

    if ($buildStore) {
        $target = Join-Path $OutputDir "SharpClaw-$MsixRid-store.msix"
        if (Test-Path $target) { Remove-Item $target -Force }
        & $makeappx pack /d $stageDir /p $target /o
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  [X] Store MSIX packaging failed" -ForegroundColor Red
            $msixFailed += "Store"
        } else {
            $sz = [math]::Round((Get-Item $target).Length / 1MB, 1)
            Write-Host "  [OK] Store:    $target `($sz MB`)" -ForegroundColor Green
        }
    }

    if ($buildSideload) {
        $target = Join-Path $OutputDir "SharpClaw-$MsixRid-sideload.msix"
        if (Test-Path $target) { Remove-Item $target -Force }
        & $makeappx pack /d $stageDir /p $target /o
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  [X] Sideload MSIX packaging failed" -ForegroundColor Red
            $msixFailed += "Sideload"
        } else {
            & $signtool sign /fd SHA256 /a /f $certFile /p $CertPassword $target
            if ($LASTEXITCODE -ne 0) {
                Write-Host "  [X] Sideload signing failed" -ForegroundColor Red
                $msixFailed += "Sideload (signing)"
            } else {
                $sz = [math]::Round((Get-Item $target).Length / 1MB, 1)
                Write-Host "  [OK] Sideload: $target `($sz MB, signed`)" -ForegroundColor Green
            }
        }
    }

    $msixOk = $msixFailed.Count -eq 0
    $stageMB = Get-DirSizeMB $stageDir
    Add-Result "MSIX" $MsixRid $msixOk $stageDir $stageMB

    if ($buildSideload -and $msixOk) {
        Write-Host ""
        Write-Host '  To trust for sideloading (elevated, one-time):' -ForegroundColor Yellow
        Write-Host "    certutil -addstore TrustedPeople `"$certPublic`"" -ForegroundColor White
    }
}

# ===================================================================
#  Server: headless API + Gateway (no Uno frontend)
# ===================================================================

function Publish-Server {
    param([string]$TargetRid)

    $label    = "Server/$TargetRid"
    $stageDir = Join-Path $OutputDir "SharpClaw-Server-$TargetRid"
    $zipPath  = Join-Path $OutputDir "SharpClaw-Server-$TargetRid.zip"

    Write-Host ""
    Write-Host "-- Server: $TargetRid ------------------------------" -ForegroundColor Magenta

    if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
    if (Test-Path $zipPath)  { Remove-Item $zipPath -Force }

    $apiDir     = Join-Path $stageDir "api"
    $gatewayDir = Join-Path $stageDir "gateway"

    # Publish API
    Write-Host "  Publishing API ..." -ForegroundColor DarkGray
    $apiArgs = @(
        "publish", $apiProject,
        "-c", $Configuration,
        "-r", $TargetRid,
        "--self-contained",
        "-p:PublishReadyToRun=true",
        "-p:PublishTrimmed=false",
        "-o", $apiDir
    )
    & dotnet @apiArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  [X] API publish failed (exit $LASTEXITCODE)" -ForegroundColor Red
        Add-Result "Server" $TargetRid $false
        return
    }

    # Publish Gateway
    Write-Host "  Publishing Gateway ..." -ForegroundColor DarkGray
    $gwArgs = @(
        "publish", $gatewayProject,
        "-c", $Configuration,
        "-r", $TargetRid,
        "--self-contained",
        "-p:PublishReadyToRun=true",
        "-p:PublishTrimmed=false",
        "-o", $gatewayDir
    )
    & dotnet @gwArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  [X] Gateway publish failed (exit $LASTEXITCODE)" -ForegroundColor Red
        Add-Result "Server" $TargetRid $false
        return
    }

    # Verify
    $apiExe = Join-Path $apiDir (Get-ExeName "SharpClaw.Application.API" $TargetRid)
    $gwExe  = Join-Path $gatewayDir (Get-ExeName "SharpClaw.Gateway" $TargetRid)

    if (-not (Test-Path $apiExe)) {
        Write-Host "  [X] API executable not found at $apiExe" -ForegroundColor Red
        Add-Result "Server" $TargetRid $false
        return
    }
    if (-not (Test-Path $gwExe)) {
        Write-Host "  [X] Gateway executable not found at $gwExe" -ForegroundColor Red
        Add-Result "Server" $TargetRid $false
        return
    }

    Strip-ForeignNatives $stageDir $TargetRid

    $totalMB = Get-DirSizeMB $stageDir
    Write-Host "  API:      $apiExe" -ForegroundColor DarkGray
    Write-Host "  Gateway:  $gwExe" -ForegroundColor DarkGray
    Write-Host "  Size:     $totalMB MB" -ForegroundColor DarkGray

    if (-not $SkipZip) {
        New-ZipArchive $stageDir $zipPath | Out-Null
    }

    Write-Host "  [OK] Server/$TargetRid complete" -ForegroundColor Magenta
    Add-Result "Server" $TargetRid $true $stageDir $totalMB
}

# ===================================================================
#  WASM: static WebAssembly build
# ===================================================================

function Publish-WASM {
    $label    = "WASM"
    $stageDir = Join-Path $OutputDir "SharpClaw-WASM"
    $zipPath  = Join-Path $OutputDir "SharpClaw-WASM.zip"

    Write-Host ""
    Write-Host "-- WASM --------------------------------------------" -ForegroundColor Yellow

    if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
    if (Test-Path $zipPath)  { Remove-Item $zipPath -Force }

    $publishArgs = @(
        "publish", $unoProject,
        "-c", $Configuration,
        "-f", $wasmTfm,
        "-p:UseMonoRuntime=true",
        "-o", $stageDir
    )

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  [X] WASM publish failed (exit $LASTEXITCODE)" -ForegroundColor Red
        Add-Result "WASM" "browserwasm" $false
        return
    }

    $totalMB = Get-DirSizeMB $stageDir
    Write-Host "  Size: $totalMB MB" -ForegroundColor DarkGray

    if (-not $SkipZip) {
        New-ZipArchive $stageDir $zipPath | Out-Null
    }

    Write-Host "  [OK] WASM build complete" -ForegroundColor Yellow
    Write-Host "  Deploy the contents of $stageDir to any static web host." -ForegroundColor DarkGray
    Add-Result "WASM" "browserwasm" $true $stageDir $totalMB
}

# ===================================================================
#  SDK tool locator (shared by MSIX)
# ===================================================================

function Find-SdkTool {
    param([string]$Name)
    $tool = Get-ChildItem "C:\Program Files (x86)\Microsoft Visual Studio\Shared\NuGetPackages\microsoft.windows.sdk.buildtools" -Recurse -Filter $Name -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "x64" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    if ($tool) { return $tool }
    $tool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" -Recurse -Filter $Name -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "x64" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    return $tool
}

# ===================================================================
#  Orchestrate
# ===================================================================

$sw = [System.Diagnostics.Stopwatch]::StartNew()

Write-Host ""
Write-Host "+======================================================+" -ForegroundColor White
Write-Host "|            SharpClaw Unified Publish                 |" -ForegroundColor White
Write-Host "|  Types: $($buildTypes -join ', ')$((' ' * (44 - ($buildTypes -join ', ').Length)))|" -ForegroundColor White
Write-Host "|  Config: $Configuration                                     |" -ForegroundColor White
Write-Host "+======================================================+" -ForegroundColor White

if (-not (Test-Path $OutputDir)) { New-Item $OutputDir -ItemType Directory -Force | Out-Null }

# -- Desktop -----------------------------------------------------
if ("Desktop" -in $buildTypes) {
    $desktopRids = Resolve-Rids $Rid $supportedDesktopRids

    if ($Parallel -and $desktopRids.Count -gt 1) {
        Write-Host ""
        Write-Host "Launching $($desktopRids.Count) Desktop builds in parallel ..." -ForegroundColor Cyan

        $jobs = @()
        foreach ($dRid in $desktopRids) {
            $jobs += Start-Job -ScriptBlock {
                param($Script, $TargetRid, $Config, $Out, $NoZip)
                & $Script -Include Desktop -Rid $TargetRid -Configuration $Config -OutputDir $Out $(if ($NoZip) { "-SkipZip" })
            } -ArgumentList (Join-Path $repoRoot "publish.ps1"), $dRid, $Configuration, $OutputDir, $SkipZip.IsPresent
        }

        $jobs | Wait-Job | ForEach-Object {
            Receive-Job $_
            Remove-Job $_
        }
    } else {
        foreach ($dRid in $desktopRids) {
            Publish-Desktop $dRid
        }
    }
}

# -- MSIX --------------------------------------------------------
if ("MSIX" -in $buildTypes) {
    $onWindows = ($PSVersionTable.PSEdition -eq "Desktop") -or ($null -ne (Get-Variable IsWindows -ValueOnly -ErrorAction SilentlyContinue) -and $IsWindows)
    if (-not $onWindows) {
        Write-Host ""
        Write-Warning "MSIX builds require Windows. Skipping."
        Add-Result "MSIX" $MsixRid $false
    } else {
        Publish-MSIX
    }
}

# -- Server ------------------------------------------------------
if ("Server" -in $buildTypes) {
    $serverRids = Resolve-Rids $ServerRid $supportedServerRids

    if ($Parallel -and $serverRids.Count -gt 1) {
        Write-Host ""
        Write-Host "Launching $($serverRids.Count) Server builds in parallel ..." -ForegroundColor Cyan

        $jobs = @()
        foreach ($sRid in $serverRids) {
            $jobs += Start-Job -ScriptBlock {
                param($Script, $TargetRid, $Config, $Out, $NoZip)
                & $Script -Include Server -ServerRid $TargetRid -Configuration $Config -OutputDir $Out $(if ($NoZip) { "-SkipZip" })
            } -ArgumentList (Join-Path $repoRoot "publish.ps1"), $sRid, $Configuration, $OutputDir, $SkipZip.IsPresent
        }

        $jobs | Wait-Job | ForEach-Object {
            Receive-Job $_
            Remove-Job $_
        }
    } else {
        foreach ($sRid in $serverRids) {
            Publish-Server $sRid
        }
    }
}

# -- WASM --------------------------------------------------------
if ("WASM" -in $buildTypes) {
    Publish-WASM
}

$sw.Stop()

# ===================================================================
#  Final report
# ===================================================================

Write-Host ""
Write-Host "+======================================================+" -ForegroundColor White
Write-Host "|                  Build Report                        |" -ForegroundColor White
Write-Host "+======================================================+" -ForegroundColor White

$passed = 0
$failedItems = @()

foreach ($r in $global:results) {
    $icon = if ($r.Ok) { "[OK]" } else { "[X]" }
    $color = if ($r.Ok) { "Green" } else { "Red" }
    $size  = if ($r.SizeMB -gt 0) { "$($r.SizeMB) MB" } else { "" }
    $line  = "  $icon $($r.Type)/$($r.Target)".PadRight(38) + $size
    Write-Host $line -ForegroundColor $color

    if ($r.Ok) { $passed++ } else { $failedItems += "$($r.Type)/$($r.Target)" }
}

$total = $global:results.Count
Write-Host "+======================================================+" -ForegroundColor White
Write-Host "|  $passed / $total succeeded    Time: $([math]::Round($sw.Elapsed.TotalMinutes, 1)) min$((' ' * 20))|" -ForegroundColor White
Write-Host "+======================================================+" -ForegroundColor White

if ($failedItems.Count -gt 0) {
    Write-Host ""
    Write-Host "Failed: $($failedItems -join ', ')" -ForegroundColor Red
}

Write-Host ""
Write-Host "Output directory: $OutputDir" -ForegroundColor DarkGray
Write-Host "Gateway is opt-in: enable it from the .env editor (Application Interface)." -ForegroundColor DarkGray
Write-Host ""

if ($failedItems.Count -gt 0) { exit 1 }
