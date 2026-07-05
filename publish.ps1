<#
.SYNOPSIS
    Unified publish script for SharpClaw. Builds every supported release
    type across all platforms in a single invocation.

.DESCRIPTION
    Consolidates all SharpClaw distribution workflows into one script:

      +-------------+---------------------------------------------------+
      | Type        | Description                                       |
      +-------------+---------------------------------------------------+
      | Uno         | Uno app + Core API + Gateway for end users.       |
      |             | Platforms: win-x64, linux-x64, linux-arm64,       |
      |             | osx-x64, osx-arm64.                               |
      | MSIX        | Windows MSIX (sideload + store). Windows only.    |
      | Server      | Headless API + Gateway (no Uno frontend).         |
      |             | For deployment on a remote server / Docker host.   |
      | Core        | Headless API only (no Gateway, no Uno).           |
      |             | Smallest useful deployment; behind an existing    |
      |             | reverse proxy or for container-only hosts.         |
      | WASM        | Static WebAssembly build of the Uno frontend.     |
      +-------------+---------------------------------------------------+

    By default, publishes everything. Use -Include / -Exclude to filter.
    Outputs are placed under publish\ (configurable via -OutputDir).

    The gateway is always bundled alongside the backend in Uno and
    MSIX builds. For Server builds the gateway is a peer to the API.

.PARAMETER Include
    Comma-separated list of build types to include.
    Values: Uno, MSIX, Server, Core, WASM, All (default: All).
    Desktop is accepted as a compatibility alias for Uno.

.PARAMETER Exclude
    Comma-separated list of build types to skip.
    Applied after -Include. Example: -Exclude MSIX,WASM

.PARAMETER Rid
    Runtime identifier(s) for Uno builds.
    Default: all.  Shorthands: win, linux, osx, all.

.PARAMETER MsixMode
    MSIX variants: Both, Sideload, Store. Default: Both.

.PARAMETER MsixVersion
    Package version for MSIX (Major.Minor.Build.0).
    Default: read from Directory.Build.props AssemblyVersion.

.PARAMETER MsixRid
    RID for the MSIX build. Default: win-x64.

.PARAMETER ServerRid
    RID(s) for Server builds.
    Default: all.  Shorthands: win, linux, osx, all.

.PARAMETER CoreRid
    RID(s) for Core builds.
    Default: all.  Shorthands: win, linux, osx, all.

.PARAMETER Configuration
    Build configuration. Default: Release.

.PARAMETER OutputDir
    Root output directory. Default: publish\ at repo root.

.PARAMETER SkipZip
    If set, skips creating zip archives for Uno, Server, and Core builds.

.PARAMETER Package
    Comma-separated post-stage packaging targets to run after staging
    Uno, Server, or Core builds. Each target warns-and-skips when its
    tooling or host OS is unavailable.
    Values: Snap, Flatpak, Deb, Rpm, Pkg, Container, All, None.
    Default: None.

      Snap       -> Linux .snap (desktop variant for Uno, server variant
                    for Server/Core). Requires snapcraft.
      Flatpak    -> Linux .flatpak (Uno only). Requires flatpak-builder.
      Deb / Rpm  -> Linux native packages for Server/Core. Requires fpm.
      Pkg        -> macOS .pkg installer (Server/Core/Uno). Requires
                    pkgbuild (macOS host).
      Container  -> SDK container publish for API + Gateway (Server only,
                    linux-x64/linux-arm64). Requires Docker.

.PARAMETER SkipPackaging
    If set, all post-stage packaging is bypassed regardless of -Package.

.PARAMETER Parallel
    If set, publishes multiple RIDs concurrently (Uno + Server + Core).
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
    .\publish.ps1 -Include Uno -Rid win              # Uno win-x64 only
    .\publish.ps1 -Include Uno,Server -Rid all       # Uno + Server, all RIDs
    .\publish.ps1 -Exclude WASM,MSIX                 # Uno + Server + Core only
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
    [string]$MsixVersion           = "",
    [string]$MsixRid               = "win-x64",
    [string]$ServerRid             = "all",
    [string]$CoreRid               = "all",
    [string]$Configuration         = "Release",
    [string]$OutputDir             = (Join-Path $PSScriptRoot "publish"),
    [switch]$SkipZip,
    [switch]$Parallel,
    # Comma-separated packaging targets to run after staging Uno/Server/Core builds.
    # Values: Snap, Flatpak, Deb, Rpm, Pkg, Container, All, None. Default: None.
    # Each target warns-and-skips when its tooling or host OS isn't available.
    [string]$Package               = "None",
    [switch]$SkipPackaging,
    [string]$CertPassword          = $(if ($env:MSIX_CERT_PASSWORD) { $env:MSIX_CERT_PASSWORD } else { "SharpClaw123!" }),
    [string]$IdentityName          = "mkn8rn.SharpClaw",
    [string]$IdentityPublisher     = "CN=SharpClaw Dev",
    [string]$PublisherDisplayName  = "marko"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# ===================================================================
#  Resolve MSIX version from Directory.Build.props when not supplied
# ===================================================================

if (-not $MsixVersion) {
    $propsPath = Join-Path $PSScriptRoot "Directory.Build.props"
    if (Test-Path $propsPath) {
        [xml]$propsXml = Get-Content $propsPath -Raw
        $rawVersion = $propsXml.Project.PropertyGroup.AssemblyVersion
    }
    if ($rawVersion) {
        $MsixVersion = $rawVersion
    } else {
        Write-Error "Could not read AssemblyVersion from Directory.Build.props. Pass -MsixVersion explicitly."
        exit 1
    }
    Write-Host "Resolved MsixVersion from Directory.Build.props: $MsixVersion" -ForegroundColor DarkGray
}

# ===================================================================
#  Constants & paths
# ===================================================================

$repoRoot       = $PSScriptRoot
$unoProject     = Join-Path (Join-Path $repoRoot "SharpClaw.Client.Uno") "SharpClaw.Client.Uno.csproj"
$apiProject     = Join-Path (Join-Path $repoRoot "SharpClaw.Runtime.Host") "SharpClaw.Runtime.Host.csproj"
$gatewayProject = Join-Path (Join-Path $repoRoot "SharpClaw.Gateway") "SharpClaw.Gateway.csproj"
$signingDir     = Join-Path $repoRoot "signing"

$unoTfm    = "net10.0-desktop"
$wasmTfm   = "net10.0-browserwasm"
$serverTfm = "net10.0"

$supportedUnoRids    = @("win-x64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")
$supportedServerRids = @("win-x64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")

# ===================================================================
#  Resolve build types
# ===================================================================

$allTypes = @("Uno", "MSIX", "Server", "Core", "WASM")

function Normalize-BuildType {
    param([string]$Type)

    $name = $Type.Trim()
    if ($name -eq "Desktop") { return "Uno" }
    return $name
}

function Resolve-Types {
    param([string]$Inc, [string]$Exc)
    $set = if ($Inc -eq "All") {
        $allTypes
    } else {
        $Inc -split ',' | ForEach-Object { Normalize-BuildType $_ }
    }
    if ($Exc) {
        $excSet = $Exc -split ',' | ForEach-Object { Normalize-BuildType $_ }
        $set = $set | Where-Object { $_ -notin $excSet }
    }
    foreach ($t in $set) {
        if ($t -notin $allTypes) {
            Write-Error "Unknown build type '$t'. Valid: $($allTypes -join ', '), All. Desktop is an alias for Uno."
            exit 1
        }
    }
    return @($set | Select-Object -Unique)
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
#  Uno: app + Core API + Gateway folder + zip for GitHub Releases
# ===================================================================

function Publish-Uno {
    param([string]$TargetRid)

    $label    = "Uno/$TargetRid"
    $stageDir = Join-Path $OutputDir "SharpClaw-Client-Uno-$TargetRid"
    $zipPath  = Join-Path $OutputDir "SharpClaw-Client-Uno-$TargetRid.zip"

    Write-Host ""
    Write-Host "-- Uno: $TargetRid ----------------------------------" -ForegroundColor Green

    if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
    if (Test-Path $zipPath)  { Remove-Item $zipPath -Force }

    $publishArgs = @(
        "publish", $unoProject,
        "-c", $Configuration,
        "-f", $unoTfm,
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
        Add-Result "Uno" $TargetRid $false
        return
    }

    # Verify backend
    $backendExe = Join-Path (Join-Path $stageDir "backend") (Get-ExeName "SharpClaw.Runtime.Host" $TargetRid)
    if (-not (Test-Path $backendExe)) {
        Write-Host "  [X] Backend not found at $backendExe" -ForegroundColor Red
        Add-Result "Uno" $TargetRid $false
        return
    }
    Write-Host "  Backend:  $backendExe" -ForegroundColor DarkGray

    $gatewayExe = Join-Path (Join-Path $stageDir "gateway") (Get-ExeName "SharpClaw.Gateway" $TargetRid)
    if (-not (Test-Path $gatewayExe)) {
        Write-Host "  [X] Gateway not found at $gatewayExe" -ForegroundColor Red
        Add-Result "Uno" $TargetRid $false
        return
    }
    Write-Host "  Gateway:  $gatewayExe" -ForegroundColor DarkGray

    Strip-ForeignNatives $stageDir $TargetRid

    $totalMB = Get-DirSizeMB $stageDir
    Write-Host "  Size:     $totalMB MB (after cleanup)" -ForegroundColor DarkGray

    $zipMB = 0
    if (-not $SkipZip) {
        $zipMB = New-ZipArchive $stageDir $zipPath
    }

    Write-Host "  [OK] Uno/$TargetRid complete" -ForegroundColor Green
    Add-Result "Uno" $TargetRid $true $stageDir $totalMB

    Invoke-PostStagePackaging "Uno" $stageDir $TargetRid
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
        "-f", $unoTfm,
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
    $iconSrc = Join-Path (Join-Path (Join-Path $repoRoot "SharpClaw.Client.Uno") "Environment") "icon.png"
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
        '    <Description>SharpClaw - AI agent orchestrator with local model inference, chat, and developer tool integration.</Description>'
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
        '      Executable="SharpClaw.Client.Uno.exe"'
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
    $apiExe = Join-Path $apiDir (Get-ExeName "SharpClaw.Runtime.Host" $TargetRid)
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

    Invoke-PostStagePackaging "Server" $stageDir $TargetRid
}

# ===================================================================
#  Core: headless API only (no Gateway, no Uno frontend)
# ===================================================================

function Publish-Core {
    param([string]$TargetRid)

    $label    = "Core/$TargetRid"
    $stageDir = Join-Path $OutputDir "SharpClaw-Core-$TargetRid"
    $zipPath  = Join-Path $OutputDir "SharpClaw-Core-$TargetRid.zip"

    Write-Host ""
    Write-Host "-- Core: $TargetRid --------------------------------" -ForegroundColor Magenta

    if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
    if (Test-Path $zipPath)  { Remove-Item $zipPath -Force }

    $apiDir = Join-Path $stageDir "api"

    # Publish API only
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
        Add-Result "Core" $TargetRid $false
        return
    }

    # Verify
    $apiExe = Join-Path $apiDir (Get-ExeName "SharpClaw.Runtime.Host" $TargetRid)
    if (-not (Test-Path $apiExe)) {
        Write-Host "  [X] API executable not found at $apiExe" -ForegroundColor Red
        Add-Result "Core" $TargetRid $false
        return
    }

    Strip-ForeignNatives $stageDir $TargetRid

    $totalMB = Get-DirSizeMB $stageDir
    Write-Host "  API:      $apiExe" -ForegroundColor DarkGray
    Write-Host "  Size:     $totalMB MB" -ForegroundColor DarkGray

    if (-not $SkipZip) {
        New-ZipArchive $stageDir $zipPath | Out-Null
    }

    Write-Host "  [OK] Core/$TargetRid complete" -ForegroundColor Magenta
    Add-Result "Core" $TargetRid $true $stageDir $totalMB

    Invoke-PostStagePackaging "Core" $stageDir $TargetRid
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
#  Packaging helpers (Snap / Flatpak / Deb / Rpm / Pkg / Container)
# ===================================================================

$allPackageTargets = @("Snap", "Flatpak", "Deb", "Rpm", "Pkg", "Container")

function Resolve-PackageTargets {
    param([string]$Value)
    if ($SkipPackaging) { return @() }
    if (-not $Value -or $Value -eq "None") { return @() }
    if ($Value -eq "All") { return $allPackageTargets }
    $set = $Value -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }
    foreach ($t in $set) {
        if ($t -notin $allPackageTargets) {
            Write-Error "Unknown package target '$t'. Valid: $($allPackageTargets -join ', '), All, None"
            exit 1
        }
    }
    return @($set)
}

$packageTargets = @(Resolve-PackageTargets $Package)

function Get-ProjectVersion {
    $propsPath = Join-Path $repoRoot "Directory.Build.props"
    if (-not (Test-Path $propsPath)) { return "0.0.0" }
    [xml]$xml = Get-Content $propsPath -Raw
    $v = $xml.Project.PropertyGroup.Version
    if ($v) { return $v } else { return "0.0.0" }
}

# Snap (Linux): warns-and-skips when snapcraft is missing.
# $Profile selects the snap template variant: "desktop" (Uno + Core API + Gateway)
# or "server" (headless API + optional gateway, daemon-style).
function Package-Snap {
    param([string]$StageDir, [string]$TargetRid, [string]$Profile = "desktop")

    if ($TargetRid -notlike "linux-*") { return }
    Write-Host ""
    Write-Host "-- Snap ($Profile/$TargetRid) --" -ForegroundColor Cyan

    if (-not (Get-Command snapcraft -ErrorAction SilentlyContinue)) {
        Write-Warning "snapcraft not found - skipping Snap packaging."
        Write-Warning "  Linux: sudo snap install snapcraft --classic"
        return
    }

    $version    = Get-ProjectVersion
    $snapArch   = if ($TargetRid -like "*arm64") { "arm64" } else { "amd64" }
    $yamlSrc    = if ($Profile -eq "server") {
        Join-Path $repoRoot "packaging\snap\snapcraft.server.yaml"
    } else {
        Join-Path $repoRoot "packaging\snap\snapcraft.yaml"
    }
    if (-not (Test-Path $yamlSrc)) {
        Write-Warning "Snap template missing: $yamlSrc - skipping."
        return
    }

    $workDir = Join-Path $OutputDir "_snap-$Profile-$TargetRid"
    if (Test-Path $workDir) { Remove-Item $workDir -Recurse -Force }
    New-Item $workDir -ItemType Directory | Out-Null
    New-Item (Join-Path $workDir "snap") -ItemType Directory | Out-Null

    Copy-Item $StageDir -Destination (Join-Path $workDir "publish") -Recurse

    $yaml = (Get-Content $yamlSrc -Raw) -replace '__VERSION__', $version
    Set-Content (Join-Path $workDir "snap\snapcraft.yaml") $yaml -Encoding UTF8

    if ($Profile -eq "desktop") {
        New-Item (Join-Path $workDir "snap\gui") -ItemType Directory | Out-Null
        Copy-Item (Join-Path $repoRoot "packaging\snap\gui\sharpclaw.desktop") (Join-Path $workDir "snap\gui\sharpclaw.desktop") -Force
        Copy-Item (Join-Path $repoRoot "SharpClaw.Client.Uno\Environment\icon.png")   (Join-Path $workDir "snap\gui\icon.png")          -Force
    }

    Push-Location $workDir
    try {
        & snapcraft pack --build-for $snapArch
        if ($LASTEXITCODE -ne 0) { Write-Warning "Snap packaging failed (exit $LASTEXITCODE)."; return }
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

# Flatpak (Linux Uno only): warns-and-skips when flatpak-builder is missing.
function Package-Flatpak {
    param([string]$StageDir, [string]$TargetRid)

    if ($TargetRid -notlike "linux-*") { return }
    Write-Host ""
    Write-Host "-- Flatpak ($TargetRid) --" -ForegroundColor Cyan

    if (-not (Get-Command flatpak-builder -ErrorAction SilentlyContinue)) {
        Write-Warning "flatpak-builder not found - skipping Flatpak packaging."
        return
    }

    $version = Get-ProjectVersion
    $fpArch  = if ($TargetRid -like "*arm64") { "aarch64" } else { "x86_64" }

    $workDir = Join-Path $OutputDir "_flatpak-$TargetRid"
    if (Test-Path $workDir) { Remove-Item $workDir -Recurse -Force }
    New-Item $workDir -ItemType Directory | Out-Null
    Copy-Item $StageDir -Destination (Join-Path $workDir "publish") -Recurse

    $meta = (Get-Content (Join-Path $repoRoot "packaging\flatpak\com.mkn8rn.SharpClaw.metainfo.xml") -Raw)
    $meta = $meta -replace '__VERSION__', $version
    $meta = $meta -replace '__DATE__', (Get-Date -Format "yyyy-MM-dd")
    Set-Content (Join-Path $workDir "com.mkn8rn.SharpClaw.metainfo.xml") $meta -Encoding UTF8

    Copy-Item (Join-Path $repoRoot "packaging\flatpak\com.mkn8rn.SharpClaw.yml")     (Join-Path $workDir "com.mkn8rn.SharpClaw.yml")     -Force
    Copy-Item (Join-Path $repoRoot "packaging\flatpak\com.mkn8rn.SharpClaw.desktop") (Join-Path $workDir "com.mkn8rn.SharpClaw.desktop") -Force
    Copy-Item (Join-Path $repoRoot "SharpClaw.Client.Uno\Environment\icon.png")             (Join-Path $workDir "icon.png")                     -Force

    $buildDir = Join-Path $workDir "build"
    $repoDir  = Join-Path $workDir "repo"

    Push-Location $workDir
    try {
        & flatpak-builder --force-clean "--repo=$repoDir" "--arch=$fpArch" $buildDir "com.mkn8rn.SharpClaw.yml"
        if ($LASTEXITCODE -ne 0) { Write-Warning "Flatpak build failed (exit $LASTEXITCODE)."; return }

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

# .deb / .rpm via fpm (Linux only). Stages the API (and gateway, when present)
# under /usr/lib/sharpclaw and installs a systemd unit to /lib/systemd/system.
function Package-LinuxNative {
    param(
        [string]$StageDir,
        [string]$TargetRid,
        [ValidateSet("deb", "rpm")]
        [string]$Format,
        [string]$ProfileName  # "Server" or "Core"
    )

    if ($TargetRid -notlike "linux-*") { return }
    Write-Host ""
    Write-Host "-- $($Format.ToUpper()) ($ProfileName/$TargetRid) --" -ForegroundColor Cyan

    if (-not (Get-Command fpm -ErrorAction SilentlyContinue)) {
        Write-Warning "fpm not found - skipping $Format packaging."
        Write-Warning "  Install: gem install --no-document fpm"
        return
    }

    $version = Get-ProjectVersion
    $arch    = if ($TargetRid -like "*arm64") {
        if ($Format -eq "deb") { "arm64" } else { "aarch64" }
    } else {
        if ($Format -eq "deb") { "amd64" } else { "x86_64" }
    }

    $workDir = Join-Path $OutputDir "_$Format-$ProfileName-$TargetRid"
    if (Test-Path $workDir) { Remove-Item $workDir -Recurse -Force }
    $rootDir = Join-Path $workDir "rootfs"
    $libDir  = Join-Path $rootDir "usr/lib/sharpclaw"
    $sysdDir = Join-Path $rootDir "lib/systemd/system"
    $etcDir  = Join-Path $rootDir "etc/sharpclaw"
    New-Item $libDir  -ItemType Directory -Force | Out-Null
    New-Item $sysdDir -ItemType Directory -Force | Out-Null
    New-Item $etcDir  -ItemType Directory -Force | Out-Null

    Copy-Item (Join-Path $StageDir "*") -Destination $libDir -Recurse -Force

    $unitTemplate = Join-Path $repoRoot "packaging\systemd\sharpclaw-api.service"
    $unitDest     = Join-Path $sysdDir "sharpclaw-api.service"
    if (Test-Path $unitTemplate) {
        Copy-Item $unitTemplate $unitDest -Force
    }

    if ($ProfileName -eq "Server") {
        $gwUnit = Join-Path $repoRoot "packaging\systemd\sharpclaw-gateway.service"
        if (Test-Path $gwUnit) {
            Copy-Item $gwUnit (Join-Path $sysdDir "sharpclaw-gateway.service") -Force
        }
    }

    $envSample = Join-Path $repoRoot "packaging\systemd\sharpclaw.env.sample"
    if (Test-Path $envSample) {
        Copy-Item $envSample (Join-Path $etcDir ".env.sample") -Force
    }

    $pkgName = "sharpclaw-$($ProfileName.ToLower())"
    $outFile = Join-Path $OutputDir "$pkgName-$version-$arch.$Format"
    if (Test-Path $outFile) { Remove-Item $outFile -Force }

    Push-Location $workDir
    try {
        & fpm -s dir -t $Format -n $pkgName -v $version -a $arch `
              --description "SharpClaw $ProfileName build" `
              --url "https://github.com/mkn8rn/SharpClaw" `
              --license "Proprietary" `
              -p $outFile `
              -C $rootDir .
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "$Format packaging failed (exit $LASTEXITCODE)."
            return
        }
        $sz = [math]::Round((Get-Item $outFile).Length / 1MB, 1)
        Write-Host "  $($Format.ToUpper()): $outFile ($sz MB)" -ForegroundColor Green
    } finally {
        Pop-Location
        Remove-Item $workDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# macOS .pkg via pkgbuild/productbuild (macOS host only).
function Package-MacPkg {
    param(
        [string]$StageDir,
        [string]$TargetRid,
        [string]$ProfileName
    )

    if ($TargetRid -notlike "osx-*") { return }
    Write-Host ""
    Write-Host "-- PKG ($ProfileName/$TargetRid) --" -ForegroundColor Cyan

    if (-not (Get-Command pkgbuild -ErrorAction SilentlyContinue)) {
        Write-Warning "pkgbuild not found (macOS-only) - skipping .pkg packaging."
        return
    }

    $version = Get-ProjectVersion
    $workDir = Join-Path $OutputDir "_pkg-$ProfileName-$TargetRid"
    if (Test-Path $workDir) { Remove-Item $workDir -Recurse -Force }
    $rootDir = Join-Path $workDir "rootfs"
    $instDir = Join-Path $rootDir "usr/local/sharpclaw"
    $launchd = Join-Path $rootDir "Library/LaunchDaemons"
    New-Item $instDir -ItemType Directory -Force | Out-Null
    New-Item $launchd -ItemType Directory -Force | Out-Null

    Copy-Item (Join-Path $StageDir "*") -Destination $instDir -Recurse -Force

    $plistTemplate = Join-Path $repoRoot "packaging\macos\com.mkn8rn.sharpclaw.api.plist"
    if (Test-Path $plistTemplate) {
        Copy-Item $plistTemplate (Join-Path $launchd "com.mkn8rn.sharpclaw.api.plist") -Force
    }

    $pkgName = "sharpclaw-$($ProfileName.ToLower())"
    $outFile = Join-Path $OutputDir "$pkgName-$version-$TargetRid.pkg"

    & pkgbuild --root $rootDir `
               --identifier "com.mkn8rn.sharpclaw.$($ProfileName.ToLower())" `
               --version $version `
               --install-location "/" `
               $outFile
    if ($LASTEXITCODE -ne 0) {
        Write-Warning ".pkg build failed (exit $LASTEXITCODE)."
        Remove-Item $workDir -Recurse -Force -ErrorAction SilentlyContinue
        return
    }
    $sz = [math]::Round((Get-Item $outFile).Length / 1MB, 1)
    Write-Host "  PKG: $outFile ($sz MB)" -ForegroundColor Green
    Remove-Item $workDir -Recurse -Force -ErrorAction SilentlyContinue
}

# SDK container publish for API and Gateway (linux-x64 / linux-arm64).
# Driven by `dotnet publish /t:PublishContainer`. Warns-and-skips when Docker
# isn't available locally.
function Package-Container {
    param([string]$TargetRid)

    if ($TargetRid -notlike "linux-*") { return }
    Write-Host ""
    Write-Host "-- Container ($TargetRid) --" -ForegroundColor Cyan

    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        Write-Warning "docker not found - skipping container publish."
        return
    }

    $containerArch = if ($TargetRid -like "*arm64") { "linux-arm64" } else { "linux-x64" }
    $version = Get-ProjectVersion

    foreach ($spec in @(
        @{ Project = $apiProject;     Repo = "sharpclaw-api" },
        @{ Project = $gatewayProject; Repo = "sharpclaw-gateway" }
    )) {
        Write-Host "  Publishing container: $($spec.Repo) ..." -ForegroundColor DarkGray
        $args = @(
            "publish", $spec.Project,
            "-c", $Configuration,
            "-r", $containerArch,
            "/t:PublishContainer",
            "/p:ContainerRepository=$($spec.Repo)",
            "/p:ContainerImageTag=$version"
        )
        & dotnet @args
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Container publish failed for $($spec.Repo) (exit $LASTEXITCODE)."
        } else {
            Write-Host "  [OK] $($spec.Repo):$version" -ForegroundColor Green
        }
    }
}

function Invoke-PostStagePackaging {
    param(
        [string]$ProfileName,   # Uno | Server | Core
        [string]$StageDir,
        [string]$TargetRid
    )

    if ($packageTargets.Count -eq 0) { return }
    if (-not (Test-Path $StageDir)) { return }

    foreach ($t in $packageTargets) {
        switch ($t) {
            "Snap" {
                $variant = if ($ProfileName -eq "Uno") { "desktop" } else { "server" }
                Package-Snap $StageDir $TargetRid $variant
            }
            "Flatpak" {
                if ($ProfileName -eq "Uno") { Package-Flatpak $StageDir $TargetRid }
            }
            "Deb" {
                if ($ProfileName -in @("Server", "Core")) {
                    Package-LinuxNative $StageDir $TargetRid "deb" $ProfileName
                }
            }
            "Rpm" {
                if ($ProfileName -in @("Server", "Core")) {
                    Package-LinuxNative $StageDir $TargetRid "rpm" $ProfileName
                }
            }
            "Pkg" {
                if ($ProfileName -in @("Server", "Core", "Uno")) {
                    Package-MacPkg $StageDir $TargetRid $ProfileName
                }
            }
            "Container" {
                if ($ProfileName -eq "Server") { Package-Container $TargetRid }
            }
        }
    }
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

# -- Uno ---------------------------------------------------------
if ("Uno" -in $buildTypes) {
    $unoRids = Resolve-Rids $Rid $supportedUnoRids

    if ($Parallel -and $unoRids.Count -gt 1) {
        Write-Host ""
        Write-Host "Launching $($unoRids.Count) Uno builds in parallel ..." -ForegroundColor Cyan

        $jobs = @()
        foreach ($uRid in $unoRids) {
            $jobs += Start-Job -ScriptBlock {
                param($Script, $TargetRid, $Config, $Out, $NoZip)
                & $Script -Include Uno -Rid $TargetRid -Configuration $Config -OutputDir $Out $(if ($NoZip) { "-SkipZip" })
            } -ArgumentList (Join-Path $repoRoot "publish.ps1"), $uRid, $Configuration, $OutputDir, $SkipZip.IsPresent
        }

        $jobs | Wait-Job | ForEach-Object {
            Receive-Job $_
            Remove-Job $_
        }
    } else {
        foreach ($uRid in $unoRids) {
            Publish-Uno $uRid
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

# -- Core --------------------------------------------------------
if ("Core" -in $buildTypes) {
    $coreRids = Resolve-Rids $CoreRid $supportedServerRids

    if ($Parallel -and $coreRids.Count -gt 1) {
        Write-Host ""
        Write-Host "Launching $($coreRids.Count) Core builds in parallel ..." -ForegroundColor Cyan

        $jobs = @()
        foreach ($cRid in $coreRids) {
            $jobs += Start-Job -ScriptBlock {
                param($Script, $TargetRid, $Config, $Out, $NoZip)
                & $Script -Include Core -CoreRid $TargetRid -Configuration $Config -OutputDir $Out $(if ($NoZip) { "-SkipZip" })
            } -ArgumentList (Join-Path $repoRoot "publish.ps1"), $cRid, $Configuration, $OutputDir, $SkipZip.IsPresent
        }

        $jobs | Wait-Job | ForEach-Object {
            Receive-Job $_
            Remove-Job $_
        }
    } else {
        foreach ($cRid in $coreRids) {
            Publish-Core $cRid
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
Write-Host "Gateway is opt-in: enable it from the .env editor (Client Interface)." -ForegroundColor DarkGray
Write-Host ""

if ($failedItems.Count -gt 0) { exit 1 }
