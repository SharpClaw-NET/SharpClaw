# Portable behavioral tests; the tiny fixture files are not release packages.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PublishSupport.ps1')
$root = Join-Path ([IO.Path]::GetTempPath()) ('sharpclaw-publish-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$script:passed = 0
function Test-Case {
    param([string]$Name, [scriptblock]$Body)
    & $Body
    $script:passed++
    Write-Host "PASS $Name"
}
function Assert-Rejected {
    param([scriptblock]$Body)
    $rejected = $false
    try { $null = & $Body } catch { $rejected = $true }
    if (-not $rejected) { throw 'Expected rejection.' }
}
function Save-Json {
    param([object]$Value, [string]$Path)
    $Value | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath $Path -Encoding utf8
}
try {
    $feed = Join-Path $root 'feed'
    $bundle = Join-Path $root 'bundle'
    $contribution = Join-Path $bundle 'contributions/test-module'
    New-Item -ItemType Directory -Path $feed, $contribution -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $feed 'Test.Module.1.0.0.nupkg'), 'unit-only package integrity fixture')
    [IO.File]::WriteAllText((Join-Path $contribution 'package.json'), '{"id":"test-module"}')
    $bundleManifest = [pscustomobject]@{
        ContributionCount = 1
        Contributions = @([pscustomobject]@{ Id = 'test-module'; Files = @(Get-PayloadInventory $contribution) })
    }
    $bundleManifestPath = Join-Path $bundle 'contribution-bundle-manifest.json'
    Save-Json $bundleManifest $bundleManifestPath
    $bom = [pscustomobject]@{
        Packages = @(Get-PayloadInventory $feed | ForEach-Object {
            [pscustomobject]@{ Name = $_.Path; Length = $_.Length; Sha256 = $_.Sha256 }
        })
        ContributionBundleManifestSha256 = (Get-FileHash $bundleManifestPath).Hash
    }
    $bomPath = Join-Path $root 'bom-manifest.json'
    Save-Json $bom $bomPath
    $bomHash = (Get-FileHash $bomPath).Hash
    Test-Case 'valid complete BOM' { $null = Assert-PublishBom $root $bomHash }
    Test-Case 'BOM hash mismatch' { Assert-Rejected { Assert-PublishBom $root ('0' * 64) } }
    Test-Case 'missing contribution' {
        Move-Item (Join-Path $contribution 'package.json') (Join-Path $root 'saved.json')
        try { Assert-Rejected { Assert-PublishBom $root $bomHash } }
        finally { Move-Item (Join-Path $root 'saved.json') (Join-Path $contribution 'package.json') }
    }
    Test-Case 'changed contribution bytes' {
        $path = Join-Path $contribution 'package.json'
        $original = [IO.File]::ReadAllBytes($path)
        try { [IO.File]::WriteAllText($path, 'tampered'); Assert-Rejected { Assert-PublishBom $root $bomHash } }
        finally { [IO.File]::WriteAllBytes($path, $original) }
    }
    Test-Case 'extra contribution file' {
        $path = Join-Path $contribution 'unexpected.dll'
        try { [IO.File]::WriteAllText($path, 'extra'); Assert-Rejected { Assert-PublishBom $root $bomHash } }
        finally { Remove-Item -LiteralPath $path }
    }
    Test-Case 'extra package' {
        $path = Join-Path $feed 'Extra.1.0.0.nupkg'
        try { [IO.File]::WriteAllText($path, 'extra'); Assert-Rejected { Assert-PublishBom $root $bomHash } }
        finally { Remove-Item -LiteralPath $path }
    }
    Test-Case 'length mismatch' { Assert-Rejected { Assert-FileDigest (Join-Path $feed $bom.Packages[0].Name) $bom.Packages[0].Sha256 0 } }
    foreach ($unsafe in @('../escape', 'a/../escape', '/absolute', 'C:\absolute', 'a//b', 'a/./b')) {
        Test-Case "unsafe path $unsafe" { Assert-Rejected { Resolve-PayloadPath $root $unsafe } }
    }
    Test-Case 'case-colliding manifest paths' {
        $file = (Get-PayloadInventory $contribution)[0]
        $other = [pscustomobject]@{ Path = 'PACKAGE.JSON'; Length = $file.Length; Sha256 = $file.Sha256 }
        Assert-Rejected { Assert-FileInventory $contribution @($file, $other) }
    }
    Test-Case 'payload directory links' {
        $path = Join-Path $bundle 'linked'
        $type = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
        $null = New-Item -ItemType $type -Path $path -Target $contribution
        try { Assert-Rejected { Resolve-PayloadPath $bundle 'linked/package.json' } }
        finally { Remove-Item -LiteralPath $path -Force }
    }
    $stage = Join-Path $root 'stage'
    New-Item -ItemType Directory -Path $stage | Out-Null
    [IO.File]::WriteAllText((Join-Path $stage '.env.template'), 'retained dotfile')
    $stageManifest = [pscustomobject]@{
        SourceCommit = 'a' * 40; Version = '0.5.0-preview.1'; InstallerVersion = '0.5.0.1'
        DeploymentType = 'Server'; Rid = 'linux-x64'; BomManifestSha256 = $bomHash
        Files = @(Get-PayloadInventory $stage)
    }
    $stagePath = Join-Path $stage 'publish-manifest.json'
    Save-Json $stageManifest $stagePath
    Test-Case 'valid stage' { $null = Assert-PublishedStage $stage (Get-FileHash $stagePath).Hash }
    Test-Case 'contradictory version identity' {
        $stageManifest.InstallerVersion = '0.5.0.2'
        try { Save-Json $stageManifest $stagePath; Assert-Rejected { Assert-PublishedStage $stage (Get-FileHash $stagePath).Hash } }
        finally { $stageManifest.InstallerVersion = '0.5.0.1'; Save-Json $stageManifest $stagePath }
    }
    Test-Case 'out-of-range MSIX identity' {
        $stageManifest.Version = '0.5.0-preview.65536'; $stageManifest.InstallerVersion = '0.5.0.65536'
        try { Save-Json $stageManifest $stagePath; Assert-Rejected { Assert-PublishedStage $stage (Get-FileHash $stagePath).Hash } }
        finally { $stageManifest.Version = '0.5.0-preview.1'; $stageManifest.InstallerVersion = '0.5.0.1'; Save-Json $stageManifest $stagePath }
    }
    Test-Case 'ZIP retains dotfiles' {
        $zipPath = Join-Path $root 'dotfiles.zip'
        [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipPath)
        $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
        try { if ($null -eq $zip.GetEntry('.env.template')) { throw 'Dotfile omitted.' } }
        finally { $zip.Dispose() }
    }
    Test-Case 'strict-mode zero-failure Count' {
        $results = [Collections.Generic.List[pscustomobject]]::new()
        $results.Add([pscustomobject]@{ Ok = $true })
        if (@($results | Where-Object { -not $_.Ok }).Count -gt 0) { throw 'Unexpected failure.' }
    }
    Test-Case 'active secrets never ship' {
        $path = Join-Path $stage '.env'
        try { [IO.File]::WriteAllText($path, 'unit-test only'); Assert-Rejected { Assert-NoActiveSecrets $stage } }
        finally { Remove-Item -LiteralPath $path }
        Assert-NoActiveSecrets $stage
    }
    Test-Case 'RID-specific launcher validation' {
        $path = Join-Path $stage 'unit-only-elf-header'
        $bytes = [byte[]]::new(64)
        $bytes[0] = 0x7f; $bytes[1] = 0x45; $bytes[2] = 0x4c; $bytes[3] = 0x46
        $bytes[4] = 2; $bytes[5] = 1; $bytes[18] = 62
        [IO.File]::WriteAllBytes($path, $bytes)
        Assert-NativeLauncher $path 'linux-x64'
        Assert-Rejected { Assert-NativeLauncher $path 'linux-arm64' }
        Assert-Rejected { Assert-NativeLauncher $path 'win-x64' }
        Assert-Rejected { Assert-NativeLauncher $path 'osx-x64' }
    }
    Write-Host "Publishing behavioral tests: $script:passed passed; zero skipped."
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
