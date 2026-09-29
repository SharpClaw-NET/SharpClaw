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
function New-NoticePackage {
    param([string]$Directory, [string]$Name, [hashtable]$Entries)
    $zip = [IO.Compression.ZipFile]::Open((Join-Path $Directory "$Name.nupkg"), [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($path in $Entries.Keys) {
            $writer = [IO.StreamWriter]::new($zip.CreateEntry($path).Open(), [Text.UTF8Encoding]::new($false))
            try { $writer.Write($Entries[$path]) } finally { $writer.Dispose() }
        }
    } finally { $zip.Dispose() }
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
        finally { Remove-Item -LiteralPath $path -Force }
    }
    Test-Case 'extra package' {
        $path = Join-Path $feed 'Extra.1.0.0.nupkg'
        try { [IO.File]::WriteAllText($path, 'extra'); Assert-Rejected { Assert-PublishBom $root $bomHash } }
        finally { Remove-Item -LiteralPath $path -Force }
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
    [IO.File]::WriteAllText((Join-Path $stage 'ThirdParty.dll'), 'unit-only binary fixture')
    $fixturePackageRoot = Join-Path $root 'nuget-packages'
    $fixturePackageDirectory = Join-Path $fixturePackageRoot 'thirdparty/1.0.0'
    New-Item -ItemType Directory -Path $fixturePackageDirectory -Force | Out-Null
    New-NoticePackage $fixturePackageDirectory 'thirdparty.1.0.0' @{
        'ThirdParty.nuspec' = '<package><metadata><id>ThirdParty</id><version>1.0.0</version><license type="file">LICENSE.txt</license></metadata></package>'
        'LICENSE.txt' = 'unit-only license text'
        'lib/ThirdParty.dll' = 'unit-only binary fixture'
    }
    Save-Json ([pscustomobject]@{ libraries = @{ 'ThirdParty/1.0.0' = @{ type = 'package' } } }) (Join-Path $stage 'SharpClaw.Test.deps.json')
    $originalNugetPackages = $env:NUGET_PACKAGES
    $env:NUGET_PACKAGES = $fixturePackageRoot
    try { Copy-ResolvedDependencyNotices $stage (Join-Path $root 'legal-cache') }
    finally { $env:NUGET_PACKAGES = $originalNugetPackages }
    $stageManifest = [pscustomobject]@{
        SourceCommit = 'a' * 40; Version = '0.5.0-preview.1'; InstallerVersion = '0.5.0.1'
        DeploymentType = 'Server'; Rid = 'linux-x64'; BomManifestSha256 = $bomHash
        Files = @(Get-PayloadInventory $stage)
    }
    $stagePath = Join-Path $stage 'publish-manifest.json'
    Save-Json $stageManifest $stagePath
    Test-Case 'valid stage' { $null = Assert-PublishedStage $stage (Get-FileHash $stagePath).Hash }
    Test-Case 'resolved package licence and binary are attributed' {
        $inventory = Get-Content (Join-Path $stage 'legal/redistribution-inventory.json') -Raw | ConvertFrom-Json
        if (@($inventory.Packages).Count -ne 1 -or
            $inventory.Packages[0].License -ne 'file:LICENSE.txt' -or
            'ThirdParty.dll' -notin @($inventory.Packages[0].PayloadFiles) -or
            -not (Test-Path (Join-Path $stage 'legal/third-party/ThirdParty.1.0.0/LICENSE.txt'))) {
            throw 'Resolved package audit omitted its exact licence or binary.'
        }
    }
    Test-Case 'unattributed binary fails closed' {
        $path = Join-Path $stage 'Rogue.dll'
        try {
            [IO.File]::WriteAllText($path, 'not in a resolved package')
            Assert-Rejected { Assert-RedistributionInventory $stage }
        } finally { Remove-Item -LiteralPath $path -Force }
    }
    Test-Case 'missing package licence fails closed' {
        $path = Join-Path $stage 'legal/third-party/ThirdParty.1.0.0/LICENSE.txt'
        $original = [IO.File]::ReadAllBytes($path)
        try {
            [IO.File]::WriteAllText($path, 'changed licence')
            Assert-Rejected { Assert-RedistributionInventory $stage }
        } finally { [IO.File]::WriteAllBytes($path, $original) }
    }
    Test-Case 'new resolved dependency needs a licence inventory record' {
        $path = Join-Path $stage 'SharpClaw.Test.deps.json'
        $original = [IO.File]::ReadAllBytes($path)
        try {
            Save-Json ([pscustomobject]@{ libraries = @{
                'ThirdParty/1.0.0' = @{ type = 'package' }
                'Unknown/2.0.0' = @{ type = 'package' }
            } }) $path
            Assert-Rejected { Assert-RedistributionInventory $stage }
        } finally { [IO.File]::WriteAllBytes($path, $original) }
    }
    Test-Case 'unlicensed source package fails closed' {
        $caseStage = Join-Path $root 'unlicensed-stage'
        $casePackage = Join-Path $fixturePackageRoot 'unlicensed/1.0.0'
        New-Item -ItemType Directory -Path $caseStage, $casePackage -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $caseStage 'Unlicensed.dll'), 'unit-only unlicensed binary')
        Save-Json ([pscustomobject]@{ libraries = @{ 'Unlicensed/1.0.0' = @{ type = 'package' } } }) (Join-Path $caseStage 'Unlicensed.deps.json')
        New-NoticePackage $casePackage 'unlicensed.1.0.0' @{
            'Unlicensed.nuspec' = '<package><metadata><id>Unlicensed</id><version>1.0.0</version></metadata></package>'
            'lib/Unlicensed.dll' = 'unit-only unlicensed binary'
        }
        $original = $env:NUGET_PACKAGES
        $env:NUGET_PACKAGES = $fixturePackageRoot
        try { Assert-Rejected { Copy-ResolvedDependencyNotices $caseStage (Join-Path $root 'legal-cache') } }
        finally { $env:NUGET_PACKAGES = $original }
    }
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
        finally { Remove-Item -LiteralPath $path -Force }
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
    foreach ($rid in @('osx-x64', 'osx-arm64')) {
        Test-Case "valid and wrong-architecture Mach-O $rid" {
            $path = Join-Path $stage "unit-only-$rid-header"
            $bytes = [byte[]]::new(64)
            $bytes[0] = 0xcf; $bytes[1] = 0xfa; $bytes[2] = 0xed; $bytes[3] = 0xfe
            $cpu = if ($rid -eq 'osx-x64') { 0x1000007 } else { 0x100000c }
            [BitConverter]::GetBytes([uint32]$cpu).CopyTo($bytes, 4)
            [IO.File]::WriteAllBytes($path, $bytes)
            Assert-NativeLauncher $path $rid
            $other = if ($rid -eq 'osx-x64') { 'osx-arm64' } else { 'osx-x64' }
            Assert-Rejected { Assert-NativeLauncher $path $other }
            $bytes[0] = 0
            [IO.File]::WriteAllBytes($path, $bytes)
            Assert-Rejected { Assert-NativeLauncher $path $rid }
        }
    }
    Test-Case 'valid and wrong-architecture Windows PE' {
        $path = Join-Path $stage 'unit-only-pe-header'
        $bytes = [byte[]]::new(128)
        $bytes[0] = 0x4d; $bytes[1] = 0x5a; $bytes[60] = 64
        [BitConverter]::GetBytes([uint32]0x4550).CopyTo($bytes, 64)
        [BitConverter]::GetBytes([uint16]0x8664).CopyTo($bytes, 68)
        [IO.File]::WriteAllBytes($path, $bytes)
        Assert-NativeLauncher $path 'win-x64'
        $bytes[68] = 0
        [IO.File]::WriteAllBytes($path, $bytes)
        Assert-Rejected { Assert-NativeLauncher $path 'win-x64' }
    }
    Test-Case 'valid and wrong-architecture Linux arm64 ELF' {
        $path = Join-Path $stage 'unit-only-arm64-elf-header'
        $bytes = [byte[]]::new(64)
        $bytes[0] = 0x7f; $bytes[1] = 0x45; $bytes[2] = 0x4c; $bytes[3] = 0x46
        $bytes[4] = 2; $bytes[5] = 1; $bytes[18] = 183
        [IO.File]::WriteAllBytes($path, $bytes)
        Assert-NativeLauncher $path 'linux-arm64'
        Assert-Rejected { Assert-NativeLauncher $path 'linux-x64' }
    }
    Test-Case 'all Persistence package source-offer READMEs retained byte-for-byte' {
        $noticeFeed = Join-Path $root 'notice-feed'
        $noticeStage = Join-Path $root 'notice-stage'
        New-Item -ItemType Directory -Path $noticeFeed, $noticeStage | Out-Null
        foreach ($id in @('SharpClaw.Persistence', 'SharpClaw.Persistence.JSONColdStore',
            'SharpClaw.Persistence.PostgreSQL', 'SharpClaw.Persistence.SQLite', 'SharpClaw.Persistence.SQLServer')) {
            New-NoticePackage $noticeFeed "$id.1.0.0" @{
                "$id.nuspec" = '<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><readme>README.md</readme></metadata></package>'
                'README.md' = "Source offer for $id`nhttps://github.com/SharpClaw-NET/SharpClaw.Persistence`n"
                'LICENSE.md' = 'unit-test license notice'
                'THIRD-PARTY-NOTICES.txt' = 'unit-test third-party notice'
            }
        }
        Copy-PackageNotices $noticeFeed $noticeStage
        foreach ($archive in Get-ChildItem $noticeFeed -Filter '*.nupkg') {
            $zip = [IO.Compression.ZipFile]::OpenRead($archive.FullName)
            try {
                foreach ($entry in $zip.Entries) {
                    $stream = $entry.Open()
                    try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
                    finally { $stream.Dispose() }
                    Assert-FileDigest (Join-Path $noticeStage "legal/$($archive.BaseName)/$($entry.FullName)") $hash $entry.Length
                }
            } finally { $zip.Dispose() }
        }
    }
    Test-Case 'nuspec-named nonstandard README retained' {
        $noticeFeed = Join-Path $root 'named-readme-feed'
        $noticeStage = Join-Path $root 'named-readme-stage'
        New-Item -ItemType Directory -Path $noticeFeed, $noticeStage | Out-Null
        New-NoticePackage $noticeFeed 'Named.Module.1.0.0' @{
            'Named.Module.nuspec' = '<package><metadata><readme>docs/SourceOffer.md</readme></metadata></package>'
            'docs/SourceOffer.md' = 'unit-test source offer'
        }
        Copy-PackageNotices $noticeFeed $noticeStage
        if ([IO.File]::ReadAllText((Join-Path $noticeStage 'legal/Named.Module.1.0.0/docs/SourceOffer.md')) -cne 'unit-test source offer') {
            throw 'Declared source-offer readme omitted or modified.'
        }
    }
    Test-Case 'root source offer identifies SharpClaw and its repository' {
        $license = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../LICENSE.md'))
        if ($license -notmatch 'included with SharpClaw' -or $license -match 'mk8\.identity' -or
            $license -notmatch 'https://github\.com/SharpClaw-NET/SharpClaw') { throw 'Incorrect root source offer.' }
    }
    Test-Case 'unsafe or missing declared README fails closed' {
        foreach ($readme in @('../escape.md', '/absolute.md', 'https://example.invalid/offer.md', 'missing.md')) {
            $caseRoot = Join-Path $root ([guid]::NewGuid().ToString('N'))
            $noticeFeed = Join-Path $caseRoot 'feed'
            $noticeStage = Join-Path $caseRoot 'stage'
            New-Item -ItemType Directory -Path $noticeFeed, $noticeStage -Force | Out-Null
            New-NoticePackage $noticeFeed 'Unsafe.Module.1.0.0' @{
                'Unsafe.Module.nuspec' = "<package><metadata><readme>$readme</readme></metadata></package>"
            }
            Assert-Rejected { Copy-PackageNotices $noticeFeed $noticeStage }
        }
    }
    Test-Case 'nuspec cannot resolve external XML entities' {
        $noticeFeed = Join-Path $root 'dtd-feed'
        $noticeStage = Join-Path $root 'dtd-stage'
        New-Item -ItemType Directory -Path $noticeFeed, $noticeStage | Out-Null
        New-NoticePackage $noticeFeed 'Entity.Module.1.0.0' @{
            'Entity.Module.nuspec' = '<!DOCTYPE package [<!ENTITY offer SYSTEM "https://example.invalid/offer">]><package><metadata><readme>&offer;</readme></metadata></package>'
        }
        Assert-Rejected { Copy-PackageNotices $noticeFeed $noticeStage }
    }
    Test-Case 'installed MSIX gate requires an isolated guest and owns machine trust cleanup' {
        $gate = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../scripts/test-installed-msix.ps1'))
        $tokens = $null
        $errors = $null
        $null = [Management.Automation.Language.Parser]::ParseInput($gate, [ref]$tokens, [ref]$errors)
        if (@($errors).Count -ne 0 -or
            $gate -notmatch 'ExpectedGuestComputerName' -or
            $gate -notmatch 'WindowsBuiltInRole\]::Administrator' -or
            $gate -notmatch 'Cert:\\LocalMachine\\TrustedPeople' -or
            $gate -match 'Cert:\\CurrentUser\\TrustedPeople' -or
            $gate -notmatch 'Remove-Item -LiteralPath \$guestTrustPath' -or
            $gate -notmatch 'Temporary guest signer trust survived cleanup' -or
            $gate -notmatch 'if \(!bootUiProbe.IsCompleted\) return false;' -or
            $gate -notmatch 'Get-TestPackageProcesses -AllowTransientTimeout' -or
            $gate -notmatch 'ProcessSnapshotTimeouts' -or
            $gate -notmatch 'Name LIKE ''SharpClaw%''' -or
            $gate -match 'Boot UI Automation probe timed out') {
            throw 'The installed gate must use the disposable guest machine store and verify exact trust cleanup.'
        }
    }
    Test-Case 'desktop publish narrows the multi-target restore graph' {
        $publisher = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../scripts/publish.ps1'))
        $project = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../SharpClaw.Client.Uno/SharpClaw.Client.Uno.csproj'))
        if ($publisher -notmatch '-p:SharpClawDesktopPublish=true' -or
            -not $project.Contains('<TargetFrameworks Condition="''$(SharpClawDesktopPublish)''==''true''">net10.0-desktop</TargetFrameworks>')) {
            throw 'Uno desktop publish must not restore unrelated mobile workloads.'
        }
    }
    Write-Host "Publishing behavioral tests: $script:passed passed; zero skipped."
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
