# Run personally only after reviewing the package, hashes, and signing certificate.
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedPackageSha256,
    [Parameter(Mandatory)][string]$CertificatePath,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedCertificateSha256,
    [switch]$TrustDevelopmentCertificate
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Windows is required.' }
if ((Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash -ne $ExpectedPackageSha256 -or
    (Get-FileHash -LiteralPath $CertificatePath -Algorithm SHA256).Hash -ne $ExpectedCertificateSha256) {
    throw 'Package or certificate SHA-256 differs from the reviewed delivery.'
}
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
    [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $CertificatePath).Path))
try {
    if ($certificate.Subject -ne 'CN=SharpClaw Dev' -or $certificate.NotAfter -le (Get-Date)) {
        throw 'Unexpected or expired development certificate.'
    }
    if ($TrustDevelopmentCertificate) {
        if (-not $PSCmdlet.ShouldProcess($certificate.Thumbprint, 'Trust development signer in LocalMachine TrustedPeople (administrator required)')) { return }
        $null = Import-Certificate -FilePath $CertificatePath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople'
    }
    if ($PSCmdlet.ShouldProcess($PackagePath, 'Install reviewed SharpClaw MSIX for the current Windows user')) {
        Add-AppxPackage -Path $PackagePath
    }
} finally { $certificate.Dispose() }
