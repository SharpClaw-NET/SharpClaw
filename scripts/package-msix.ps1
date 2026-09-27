<# Creates a signed MSIX from an already verified win-x64 Application bundle.
   Does not install the app or change certificate trust. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StageRoot,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$PublishManifestSha256,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$CertificateThumbprint,
    [Parameter(Mandatory)][string]$OutputDir,
    [string]$MakeAppxPath,
    [string]$SignToolPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'MSIX packaging requires Windows and the Windows SDK.' }
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $repoRoot 'build/PublishSupport.ps1')
$stage = [IO.Path]::GetFullPath($StageRoot)
$manifest = Assert-PublishedStage $stage $PublishManifestSha256
Assert-InstallerSource $repoRoot $manifest.SourceCommit
if ($manifest.DeploymentType -ne 'Application' -or $manifest.Rid -ne 'win-x64') {
    throw 'MSIX requires a win-x64 Application bundle.'
}
$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint"
if (-not $certificate.HasPrivateKey -or $certificate.Subject -ne 'CN=SharpClaw Dev' -or
    $certificate.NotBefore -gt (Get-Date) -or $certificate.NotAfter -le (Get-Date) -or
    @($certificate.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq '1.3.6.1.5.5.7.3.3' }).Count -eq 0) {
    throw 'A current CN=SharpClaw Dev code-signing certificate with its private key is required.'
}
function Resolve-SdkTool {
    param([string]$ExplicitPath, [string]$Name)
    if ($ExplicitPath) { return (Resolve-Path -LiteralPath $ExplicitPath).Path }
    $tools = @(Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\$Name" |
        Sort-Object { [version]$_.Directory.Parent.Name } -Descending)
    if ($tools.Count -eq 0) { throw "Windows SDK tool '$Name' was not found." }
    return $tools[0].FullName
}
$MakeAppxPath = Resolve-SdkTool $MakeAppxPath 'makeappx.exe'
$SignToolPath = Resolve-SdkTool $SignToolPath 'signtool.exe'
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
if (Get-ChildItem -LiteralPath $OutputDir -Force) { throw 'Installer output must be empty.' }
$work = Join-Path $OutputDir '.msix-stage'
New-Item -ItemType Directory -Path $work | Out-Null
Get-ChildItem -LiteralPath $stage -Force | Copy-Item -Destination $work -Recurse
[xml]$appx = Get-Content -LiteralPath (Join-Path $repoRoot 'packaging/windows/AppxManifest.xml') -Raw
$appx.Package.Identity.Version = $manifest.InstallerVersion
$appx.Save((Join-Path $work 'AppxManifest.xml'))
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $work 'Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
$icon = [Drawing.Image]::FromFile((Join-Path $repoRoot 'SharpClaw.Client.Uno/Environment/icon.png'))
try {
    foreach ($asset in @(@('StoreLogo.png', 50), @('Square150x150Logo.png', 150), @('Square44x44Logo.png', 44))) {
        $bitmap = [Drawing.Bitmap]::new([int]$asset[1], [int]$asset[1])
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.DrawImage($icon, 0, 0, [int]$asset[1], [int]$asset[1])
            $bitmap.Save((Join-Path $assets $asset[0]), [Drawing.Imaging.ImageFormat]::Png)
        } finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $icon.Dispose() }
$packagePath = Join-Path $OutputDir "SharpClaw-$($manifest.InstallerVersion)-win-x64.msix"
& $MakeAppxPath pack /d $work /p $packagePath
if ($LASTEXITCODE -ne 0) { throw 'MakeAppx packaging or validation failed.' }
& $SignToolPath sign /fd SHA256 /sha1 $CertificateThumbprint $packagePath
if ($LASTEXITCODE -ne 0) { throw 'MSIX signing failed.' }

# Check all package payload bytes and its CMS signature without modifying trust.
# Device trust is a separate, explicitly reported prerequisite for installation.
Add-Type -AssemblyName System.Security.Cryptography.Pkcs
$zip = [IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    foreach ($file in Get-PayloadInventory $work) {
        $entry = $zip.GetEntry($file.Path)
        if ($null -eq $entry -or $entry.Length -ne $file.Length) { throw "Missing MSIX payload '$($file.Path)'." }
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        if ($hash -ne $file.Sha256) { throw "Changed MSIX payload '$($file.Path)'." }
    }
    $signature = $zip.GetEntry('AppxSignature.p7x')
    $stream = $signature.Open()
    $memory = [IO.MemoryStream]::new()
    try { $stream.CopyTo($memory); $bytes = $memory.ToArray() }
    finally { $stream.Dispose(); $memory.Dispose() }
    if ([Text.Encoding]::ASCII.GetString($bytes, 0, 4) -ne 'PKCX') { throw 'Invalid MSIX signature container.' }
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new()
    $cms.Decode($bytes[4..($bytes.Length - 1)])
    $cms.CheckSignature($true)
    if ($cms.SignerInfos.Count -ne 1 -or
        $cms.SignerInfos[0].Certificate.Thumbprint -ne $CertificateThumbprint) {
        throw 'MSIX signer identity mismatch.'
    }
} finally { $zip.Dispose() }
$cerPath = Join-Path $OutputDir 'SharpClaw-Dev.cer'
$null = Export-Certificate -Cert $certificate -FilePath $cerPath
$chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
try { $deviceTrust = $chain.Build($certificate) }
finally { $chain.Dispose() }
Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging/windows/Install-SharpClaw.ps1') -Destination $OutputDir
[pscustomobject]@{
    SourceCommit = $manifest.SourceCommit; Version = $manifest.InstallerVersion
    PublishManifestSha256 = $PublishManifestSha256; BomManifestSha256 = $manifest.BomManifestSha256
    Package = [IO.Path]::GetFileName($packagePath); PackageSha256 = (Get-FileHash $packagePath).Hash
    Certificate = [IO.Path]::GetFileName($cerPath); CertificateSha256 = (Get-FileHash $cerPath).Hash
    CertificateThumbprint = $CertificateThumbprint; CertificateSubject = $certificate.Subject
    CertificateExpires = $certificate.NotAfter.ToUniversalTime().ToString('O'); DeviceTrust = $deviceTrust
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDir 'installer-manifest.json') -Encoding utf8
Write-Host "Signed MSIX: $packagePath. Device certificate trust: $deviceTrust. No app was installed."
