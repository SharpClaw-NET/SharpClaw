<# Builds, but never installs, a Debian 13 Server package from a verified bundle. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StageRoot,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$PublishManifestSha256,
    [Parameter(Mandatory)][string]$OutputDir
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsLinux) { throw 'Build the Debian installer on Linux with dpkg-deb.' }
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $repoRoot 'build/PublishSupport.ps1')
$stage = [IO.Path]::GetFullPath($StageRoot)
$manifest = Assert-PublishedStage $stage $PublishManifestSha256
Assert-InstallerSource $repoRoot $manifest.SourceCommit
if ($manifest.DeploymentType -ne 'Server' -or $manifest.Rid -notin @('linux-x64', 'linux-arm64')) {
    throw 'Debian packaging requires a linux-x64 or linux-arm64 Server bundle.'
}
$architecture = if ($manifest.Rid -eq 'linux-x64') { 'amd64' } else { 'arm64' }
$version = $manifest.Version.Replace('-preview.', '~preview.')
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
if (Get-ChildItem -LiteralPath $OutputDir -Force) { throw 'Installer output must be empty.' }
$root = Join-Path $OutputDir '.deb-stage'
$control = Join-Path $root 'DEBIAN'
$lib = Join-Path $root 'usr/lib/sharpclaw'
$etc = Join-Path $root 'etc/sharpclaw'
$units = Join-Path $root 'usr/lib/systemd/system'
$docs = Join-Path $root 'usr/share/doc/sharpclaw-server'
New-Item -ItemType Directory -Path $control, $lib, $etc, $units, $docs -Force | Out-Null
Get-ChildItem -LiteralPath $stage -Force | Copy-Item -Destination $lib -Recurse
$templateRoot = Join-Path $repoRoot 'packaging/debian'
$text = Get-Content -LiteralPath (Join-Path $templateRoot 'control.template') -Raw
$text.Replace('@VERSION@', $version).Replace('@ARCHITECTURE@', $architecture) |
    Set-Content -LiteralPath (Join-Path $control 'control') -Encoding utf8NoBOM
foreach ($script in @('postinst', 'prerm', 'postrm')) {
    # Git's Windows checkout may have CRLF; Debian scripts must have LF.
    $text = (Get-Content -LiteralPath (Join-Path $templateRoot $script) -Raw).Replace("`r`n", "`n")
    [IO.File]::WriteAllText((Join-Path $control $script), $text, [Text.UTF8Encoding]::new($false))
}
foreach ($name in @('runtime.env', 'gateway.env')) {
    $text = (Get-Content -LiteralPath (Join-Path $templateRoot $name) -Raw).Replace("`r`n", "`n")
    [IO.File]::WriteAllText((Join-Path $etc $name), $text, [Text.UTF8Encoding]::new($false))
}
"/etc/sharpclaw/runtime.env`n/etc/sharpclaw/gateway.env" |
    Set-Content -LiteralPath (Join-Path $control 'conffiles') -Encoding utf8NoBOM
Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging/systemd/sharpclaw-api.service'),
    (Join-Path $repoRoot 'packaging/systemd/sharpclaw-gateway.service') -Destination $units
$text = (Get-Content -LiteralPath (Join-Path $templateRoot 'wait-ready') -Raw).Replace("`r`n", "`n")
[IO.File]::WriteAllText((Join-Path $lib 'wait-ready'), $text, [Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE.md') -Destination (Join-Path $docs 'copyright')
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/Installers.md') -Destination (Join-Path $docs 'README.md')

# Windows-created ZIPs do not carry dependable executable modes. Set them explicitly.
$directoryMode = [IO.UnixFileMode]493 # 0755
$fileMode = [IO.UnixFileMode]420 # 0644
$secretMode = [IO.UnixFileMode]384 # 0600
foreach ($directory in Get-ChildItem -LiteralPath $root -Recurse -Directory -Force) {
    [IO.File]::SetUnixFileMode($directory.FullName, $directoryMode)
}
foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Force) {
    [IO.File]::SetUnixFileMode($file.FullName, $fileMode)
}
foreach ($path in @('backend/SharpClaw.Runtime.Host', 'backend/SharpClaw.SidecarHost.OutOfProcess', 'gateway/SharpClaw.Gateway', 'wait-ready')) {
    $file = Join-Path $lib $path
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing Linux launcher '$path'." }
    [IO.File]::SetUnixFileMode($file, $directoryMode)
}
foreach ($script in @('postinst', 'prerm', 'postrm')) { [IO.File]::SetUnixFileMode((Join-Path $control $script), $directoryMode) }
foreach ($name in @('runtime.env', 'gateway.env')) { [IO.File]::SetUnixFileMode((Join-Path $etc $name), $secretMode) }
$package = Join-Path $OutputDir "sharpclaw-server_${version}_${architecture}.deb"
& dpkg-deb --root-owner-group --build $root $package
if ($LASTEXITCODE -ne 0) { throw 'Debian package construction failed.' }
& dpkg-deb --info $package
if ($LASTEXITCODE -ne 0) { throw 'Debian package inspection failed.' }
[pscustomobject]@{
    SourceCommit = $manifest.SourceCommit; Version = $version; Architecture = $architecture
    PublishManifestSha256 = $PublishManifestSha256; BomManifestSha256 = $manifest.BomManifestSha256
    Package = [IO.Path]::GetFileName($package); PackageSha256 = (Get-FileHash $package).Hash
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDir 'installer-manifest.json') -Encoding utf8NoBOM
Write-Host "Debian installer: $package. No app or service was installed."
