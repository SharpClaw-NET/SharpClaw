<#
.SYNOPSIS
    Packages SharpClaw as MSIX for sideload testing and/or Microsoft Store.

.DESCRIPTION
    Produces up to two MSIX variants from a single Skia-desktop publish:

      - Sideload:  Self-signed MSIX for testing / direct installation.
                   Requires the certificate to be trusted on the target machine.
      - Store:     Unsigned MSIX for Microsoft Store submission via Partner
                   Center. The Store re-signs with its own certificate.

    Workflow:
      1. Publishes the Uno Skia-desktop app + bundled API backend
      2. Strips foreign-platform native libraries and PDBs
      3. Generates AppxManifest.xml from the provided identity values
      4. Packages with makeappx.exe
      5. Signs the sideload copy with signtool.exe (self-signed cert)

    Certificate handling:
      The first sideload build auto-generates a self-signed code-signing
      certificate at signing\SharpClaw-Dev.pfx. Subsequent builds reuse it.
      The certificate subject MUST match -IdentityPublisher exactly.

      To install sideload MSIX the cert must be trusted (elevated, one-time):
        certutil -addstore TrustedPeople signing\SharpClaw-Dev.cer

.PARAMETER Mode
    Which variants to build.
      Both     - sideload + store (default).
      Sideload - signed MSIX only.
      Store    - unsigned MSIX only.

.PARAMETER Version
    Package version in Major.Minor.Build.0 format. The last quad MUST be 0
    for Store packages. Default: 0.0.1.0

.PARAMETER IdentityName
    Package identity name. Default: mkn8rn.SharpClaw
    For Store: get from Partner Center -> Product identity -> Package/Identity/Name.

.PARAMETER IdentityPublisher
    Package publisher. Default: CN=SharpClaw Dev (matches the self-signed cert).
    For Store: get from Partner Center -> Product identity -> Package/Identity/Publisher.
    The Store re-signs, so the dev value works for submission too.

.PARAMETER PublisherDisplayName
    Human-readable publisher name. Default: marko

.PARAMETER Configuration
    Build configuration. Default: Release.

.PARAMETER OutputDir
    Final output directory. Default: publish\ at repo root.

.PARAMETER Rid
    Runtime identifier. Default: win-x64.

.PARAMETER CertPassword
    PFX password. Override with -CertPassword or $env:MSIX_CERT_PASSWORD.

.EXAMPLE
    .\publish-msix.ps1
    .\publish-msix.ps1 -Mode Sideload
    .\publish-msix.ps1 -Mode Store
    .\publish-msix.ps1 -Rid win-arm64
#>
param(
    [ValidateSet("Both", "Sideload", "Store")]
    [string]$Mode = "Both",

    [string]$Version              = "0.0.2.0",
    [string]$IdentityName         = "mkn8rn.SharpClaw",
    [string]$IdentityPublisher    = "CN=SharpClaw Dev",
    [string]$PublisherDisplayName = "marko",
    [string]$Configuration        = "Release",
    [string]$OutputDir            = (Join-Path $PSScriptRoot "publish"),
    [string]$Rid                  = "win-x64",
    [string]$CertPassword         = $(if ($env:MSIX_CERT_PASSWORD) { $env:MSIX_CERT_PASSWORD } else { "SharpClaw123!" })
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$buildSideload = $Mode -in @("Both", "Sideload")
$buildStore    = $Mode -in @("Both", "Store")

# -- Validate version format (must end in .0 for Store) --
if ($Version -notmatch '^\d+\.\d+\.\d+\.0$') {
    Write-Error "Version must be in Major.Minor.Build.0 format (last quad must be 0 for Store). Got: $Version"
    exit 1
}

# -- Locate tools --
function Find-SdkTool {
    param([string]$Name)
    # Try VS shared NuGet packages first
    $tool = Get-ChildItem "C:\Program Files (x86)\Microsoft Visual Studio\Shared\NuGetPackages\microsoft.windows.sdk.buildtools" -Recurse -Filter $Name -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "x64" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    if ($tool) { return $tool }
    # Fallback: Windows SDK
    $tool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" -Recurse -Filter $Name -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "x64" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    return $tool
}

$makeappx = Find-SdkTool "makeappx.exe"
if (-not $makeappx) {
    Write-Error "makeappx.exe not found. Install the Windows SDK or ensure microsoft.windows.sdk.buildtools NuGet is present."
    exit 1
}
Write-Host "Using makeappx: $makeappx" -ForegroundColor DarkGray

$signtool = $null
if ($buildSideload) {
    $signtool = Find-SdkTool "signtool.exe"
    if (-not $signtool) {
        Write-Error "signtool.exe not found. Install the Windows SDK for MSIX signing."
        exit 1
    }
    Write-Host "Using signtool: $signtool" -ForegroundColor DarkGray
}

# -- Paths --
$repoRoot   = $PSScriptRoot
$unoProject = Join-Path (Join-Path $repoRoot "SharpClaw.Uno") "SharpClaw.Uno.csproj"
$signingDir = Join-Path $repoRoot "signing"
$certFile   = Join-Path $signingDir "SharpClaw-Dev.pfx"
$certPublic = Join-Path $signingDir "SharpClaw-Dev.cer"
$stageDir   = Join-Path $OutputDir "SharpClaw-msix-stage"
$tfm        = "net10.0-desktop"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  SharpClaw MSIX Build"                  -ForegroundColor Cyan
Write-Host "  Mode:      $Mode"                      -ForegroundColor Cyan
Write-Host "  Version:   $Version"                   -ForegroundColor Cyan
Write-Host "  RID:       $Rid"                       -ForegroundColor Cyan
Write-Host "  Identity:  $IdentityName"              -ForegroundColor Cyan
Write-Host "  Publisher: $IdentityPublisher"         -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# -- Self-signed certificate (sideload) --
if ($buildSideload) {
    if (-not (Test-Path $signingDir)) {
        New-Item $signingDir -ItemType Directory -Force | Out-Null
    }

    if (-not (Test-Path $certFile)) {
        Write-Host "Generating self-signed certificate ($IdentityPublisher) ..." -ForegroundColor Yellow

        $cert = New-SelfSignedCertificate `
            -Type Custom `
            -Subject $IdentityPublisher `
            -KeyUsage DigitalSignature `
            -FriendlyName "SharpClaw Dev Signing" `
            -CertStoreLocation "Cert:\CurrentUser\My" `
            -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")

        $securePwd = ConvertTo-SecureString $CertPassword -Force -AsPlainText
        Export-PfxCertificate -Cert $cert -FilePath $certFile -Password $securePwd | Out-Null

        # Export the public certificate (.cer) for trusting on target machines.
        # certutil -addstore TrustedPeople requires a .cer, not a .pfx.
        [System.IO.File]::WriteAllBytes(
            $certPublic,
            $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))

        Write-Host "  PFX (signing): $certFile" -ForegroundColor Green
        Write-Host "  CER (trust):   $certPublic" -ForegroundColor Green
        Write-Host "  Thumbprint:    $($cert.Thumbprint)" -ForegroundColor DarkGray
        Write-Host ""
        Write-Host "  To trust for sideloading (elevated, one-time):" -ForegroundColor Yellow
        Write-Host "    certutil -addstore TrustedPeople `"$certPublic`"" -ForegroundColor White
        Write-Host ""
    }
    else {
        Write-Host "Using existing certificate: $certFile" -ForegroundColor Green

        # Ensure the .cer is exported (may be missing from older runs)
        if (-not (Test-Path $certPublic)) {
            $pfxObj = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certFile, $CertPassword)
            [System.IO.File]::WriteAllBytes(
                $certPublic,
                $pfxObj.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
            Write-Host "  Exported public cert: $certPublic" -ForegroundColor Green
        }
    }
}

# -- Clean --
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }

# -- Publish app + backend --
Write-Host "Publishing Uno app + bundled backend ..." -ForegroundColor Cyan

$publishArgs = @(
    "publish", $unoProject,
    "-c", $Configuration,
    "-f", $tfm,
    "-r", $Rid,
    "--self-contained",
    "-p:BundleBackend=true",
    "-p:UseMonoRuntime=false",
    "-p:PublishReadyToRun=true",
    "-o", $stageDir
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "Publish failed with exit code $LASTEXITCODE"
    exit 1
}

# -- Strip foreign-platform natives (same logic as publish-release.ps1) --
Write-Host "Stripping foreign-platform natives ..." -ForegroundColor Cyan

$ridArch = ($Rid -split '-')[-1]
$ridOs   = ($Rid -split '-')[0]

foreach ($runtimesDir in (Get-ChildItem $stageDir -Recurse -Directory -Filter "runtimes" -ErrorAction SilentlyContinue)) {
    foreach ($ridDir in (Get-ChildItem $runtimesDir.FullName -Directory -ErrorAction SilentlyContinue)) {
        if ($ridDir.Name -notlike "$ridOs-*" -and $ridDir.Name -ne $ridOs) {
            Remove-Item $ridDir.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

$vlcDir = Join-Path $stageDir "libvlc"
if (Test-Path $vlcDir) {
    foreach ($archDir in (Get-ChildItem $vlcDir -Directory -ErrorAction SilentlyContinue)) {
        if ($archDir.Name -notlike "*$ridArch*") {
            Remove-Item $archDir.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# -- Remove PDB files to reduce size --
$pdbCount = (Get-ChildItem $stageDir -Recurse -Filter "*.pdb" -ErrorAction SilentlyContinue).Count
Get-ChildItem $stageDir -Recurse -Filter "*.pdb" -ErrorAction SilentlyContinue | Remove-Item -Force
Write-Host "  Removed $pdbCount PDB files" -ForegroundColor DarkGray

# -- Generate AppxManifest.xml --
Write-Host "Generating AppxManifest.xml ..." -ForegroundColor Cyan

# Map RID to MSIX architecture
$msixArch = switch -Wildcard ($Rid) {
    "*-x64"   { "x64" }
    "*-x86"   { "x86" }
    "*-arm64" { "arm64" }
    default   { "x64" }
}

# Resolve icon - copy into package at Assets/
$assetsDir = Join-Path $stageDir "Assets"
if (-not (Test-Path $assetsDir)) { New-Item $assetsDir -ItemType Directory -Force | Out-Null }

$iconSrc = Join-Path $repoRoot "SharpClaw.Uno\Environment\icon.png"
$iconDest = Join-Path $assetsDir "icon.png"
if (Test-Path $iconSrc) {
    Copy-Item $iconSrc $iconDest -Force
}

$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  IgnorableNamespaces="uap rescap">

  <Identity
    Name="$IdentityName"
    Version="$Version"
    Publisher="$IdentityPublisher"
    ProcessorArchitecture="$msixArch" />

  <Properties>
    <DisplayName>SharpClaw</DisplayName>
    <PublisherDisplayName>$PublisherDisplayName</PublisherDisplayName>
    <Logo>Assets\icon.png</Logo>
    <Description>SharpClaw - AI agent orchestrator with local model inference, chat, transcription, and developer tool integration.</Description>
  </Properties>

  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>

  <Resources>
    <Resource Language="en-us" />
  </Resources>

  <Applications>
    <Application Id="SharpClaw"
      Executable="SharpClaw.Uno.exe"
      EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements
        DisplayName="SharpClaw"
        Description="AI agent orchestrator"
        BackgroundColor="transparent"
        Square150x150Logo="Assets\icon.png"
        Square44x44Logo="Assets\icon.png" />
    </Application>
  </Applications>

  <Capabilities>
    <Capability Name="internetClient" />
    <Capability Name="privateNetworkClientServer" />
    <rescap:Capability Name="runFullTrust" />
    <DeviceCapability Name="microphone" />
  </Capabilities>
</Package>
"@

$manifestPath = Join-Path $stageDir "AppxManifest.xml"
[System.IO.File]::WriteAllText($manifestPath, $manifest, [System.Text.Encoding]::UTF8)

# -- Remove the template manifest (not the real one) --
$templateManifest = Join-Path $stageDir "Package.appxmanifest"
if (Test-Path $templateManifest) { Remove-Item $templateManifest -Force }

# -- Build MSIX variants --
$storeMsix    = Join-Path $OutputDir "SharpClaw-$Rid-store.msix"
$sideloadMsix = Join-Path $OutputDir "SharpClaw-$Rid-sideload.msix"
$failed = @()

if ($buildStore) {
    $target = $storeMsix
    if (Test-Path $target) { Remove-Item $target -Force }

    Write-Host "Packaging Store MSIX (unsigned) ..." -ForegroundColor Cyan
    & $makeappx pack /d $stageDir /p $target /o
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  makeappx pack failed (exit $LASTEXITCODE)" -ForegroundColor Red
        $failed += "Store"
    }
    else {
        $sz = [math]::Round((Get-Item $target).Length / 1MB, 1)
        Write-Host "  Store package: $target ($sz MB)" -ForegroundColor Green
    }
}

if ($buildSideload) {
    $target = $sideloadMsix
    if (Test-Path $target) { Remove-Item $target -Force }

    Write-Host "Packaging Sideload MSIX ..." -ForegroundColor Cyan
    & $makeappx pack /d $stageDir /p $target /o
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  makeappx pack failed (exit $LASTEXITCODE)" -ForegroundColor Red
        $failed += "Sideload"
    }
    else {
        Write-Host "Signing Sideload MSIX ..." -ForegroundColor Cyan
        & $signtool sign /fd SHA256 /a /f $certFile /p $CertPassword $target
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  signtool sign failed (exit $LASTEXITCODE)" -ForegroundColor Red
            $failed += "Sideload (signing)"
        }
        else {
            $sz = [math]::Round((Get-Item $target).Length / 1MB, 1)
            Write-Host "  Sideload package: $target ($sz MB, signed)" -ForegroundColor Green
        }
    }
}

# -- Summary --
$stageSize = [math]::Round(
    ((Get-ChildItem $stageDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB), 1
)

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  MSIX Build Complete"                   -ForegroundColor Green
Write-Host "  Stage size: $stageSize MB"             -ForegroundColor White
if ($failed.Count -gt 0) {
    Write-Host "  Failed: $($failed -join ', ')"     -ForegroundColor Red
}
Write-Host "========================================" -ForegroundColor Green

if ($buildStore -and "Store" -notin $failed) {
    Write-Host ""
    Write-Host "  Store upload:" -ForegroundColor Yellow
    Write-Host "    Upload $storeMsix via Partner Center." -ForegroundColor White
    Write-Host "    The Store will re-sign and re-stamp the package identity." -ForegroundColor White
}

if ($buildSideload -and $failed -notcontains "Sideload" -and $failed -notcontains "Sideload (signing)") {
    Write-Host ""
    Write-Host "  Sideload install:" -ForegroundColor Yellow
    Write-Host "    1. Trust the certificate (elevated, one-time):" -ForegroundColor White
    Write-Host "       certutil -addstore TrustedPeople `"$certPublic`"" -ForegroundColor White
    Write-Host "    2. Double-click $sideloadMsix to install." -ForegroundColor White
}

Write-Host ""
if ($failed.Count -gt 0) { exit 1 }
