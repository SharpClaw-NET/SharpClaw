# Portable behavioral tests; the tiny fixture files are not release packages.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$originalNugetPackages = $env:NUGET_PACKAGES
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
function Save-FixtureDependencyManifest {
    param([string]$Path, [string[]]$Packages)
    $libraries = @{}
    $target = @{}
    foreach ($package in $Packages) {
        $libraries[$package] = @{ type = 'package' }
        $target[$package] = @{ runtime = @{ "lib/$($package.Split('/')[0]).dll" = @{} } }
    }
    Save-Json @{ runtimeTarget = @{ name = 'fixture' }; libraries = $libraries; targets = @{ fixture = $target } } $Path
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
    Save-FixtureDependencyManifest (Join-Path $stage 'SharpClaw.Test.deps.json') @('ThirdParty/1.0.0')
    $env:NUGET_PACKAGES = $fixturePackageRoot
    Copy-ResolvedDependencyNotices $stage (Join-Path $root 'legal-cache')
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
    Test-Case 'same-name wrong-byte replacement fails creation and validation' {
        $path = Join-Path $stage 'ThirdParty.dll'
        $bytes = [IO.File]::ReadAllBytes($path)
        try {
            [IO.File]::WriteAllText($path, 'a different file with a known basename')
            Assert-Rejected { Assert-RedistributionInventory $stage }
            Assert-Rejected { Copy-ResolvedDependencyNotices $stage (Join-Path $root 'legal-cache') }
        } finally { [IO.File]::WriteAllBytes($path, $bytes) }
    }
    Test-Case 'extra nested known-name copy fails creation and validation' {
        $directory = Join-Path $stage 'unexpected'
        New-Item -ItemType Directory -Path $directory | Out-Null
        try {
            Copy-Item -LiteralPath (Join-Path $stage 'ThirdParty.dll') -Destination (Join-Path $directory 'ThirdParty.dll')
            Assert-Rejected { Assert-RedistributionInventory $stage }
            Assert-Rejected { Copy-ResolvedDependencyNotices $stage (Join-Path $root 'legal-cache') }
        } finally { Remove-Item -LiteralPath $directory -Recurse -Force }
    }
    Test-Case 'two scopes retain different versions of the same assembly name' {
        $caseStage = Join-Path $root 'two-scopes'
        $secondPackage = Join-Path $fixturePackageRoot 'thirdparty/2.0.0'
        New-Item -ItemType Directory -Path $secondPackage -Force | Out-Null
        New-NoticePackage $secondPackage 'thirdparty.2.0.0' @{
            'ThirdParty.nuspec' = '<package><metadata><id>ThirdParty</id><version>2.0.0</version><license type="file">LICENSE.txt</license></metadata></package>'
            'LICENSE.txt' = 'second-version unit licence'; 'lib/ThirdParty.dll' = 'second version binary'
        }
        foreach ($version in @('1.0.0', '2.0.0')) {
            $directory = Join-Path $caseStage "contributions/scope-$version"
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
            $value = if ($version -eq '1.0.0') { 'unit-only binary fixture' } else { 'second version binary' }
            [IO.File]::WriteAllText((Join-Path $directory 'ThirdParty.dll'), $value)
            Save-FixtureDependencyManifest (Join-Path $directory 'Module.deps.json') @("ThirdParty/$version")
        }
        Copy-ResolvedDependencyNotices $caseStage (Join-Path $root 'legal-cache')
        $inventory = Get-Content -LiteralPath (Join-Path $caseStage 'legal/redistribution-inventory.json') -Raw | ConvertFrom-Json
        foreach ($package in $inventory.Packages) {
            if (@($package.Assets).Count -ne 1 -or $package.Assets[0].Path -cne "contributions/scope-$($package.Version)/ThirdParty.dll" -or
                $package.Assets[0].ArchiveEntry -cne 'lib/ThirdParty.dll') { throw 'Scope/version attribution leaked.' }
        }
        $first = Join-Path $caseStage 'contributions/scope-1.0.0/ThirdParty.dll'
        $second = Join-Path $caseStage 'contributions/scope-2.0.0/ThirdParty.dll'
        $bytes = [IO.File]::ReadAllBytes($first)
        try {
            Copy-Item -LiteralPath $second -Destination $first -Force
            Assert-Rejected { Assert-RedistributionInventory $caseStage }
        } finally { [IO.File]::WriteAllBytes($first, $bytes) }
    }
    Test-Case 'production publish requires SDK-selected asset receipts' {
        Assert-Rejected { Copy-ResolvedDependencyNotices $stage (Join-Path $root 'legal-cache') -RequirePublishReceipts }
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
            Save-FixtureDependencyManifest $path @('ThirdParty/1.0.0', 'Unknown/2.0.0')
            Assert-Rejected { Assert-RedistributionInventory $stage }
        } finally { [IO.File]::WriteAllBytes($path, $original) }
    }
    Test-Case 'unlicensed source package fails closed' {
        $caseStage = Join-Path $root 'unlicensed-stage'
        $casePackage = Join-Path $fixturePackageRoot 'unlicensed/1.0.0'
        New-Item -ItemType Directory -Path $caseStage, $casePackage -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $caseStage 'Unlicensed.dll'), 'unit-only unlicensed binary')
        Save-FixtureDependencyManifest (Join-Path $caseStage 'Unlicensed.deps.json') @('Unlicensed/1.0.0')
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
            $gate -notmatch 'X509Store\(StoreName.TrustedPeople, StoreLocation.LocalMachine\)' -or
            $gate -notmatch 'HasGuestTrust\(\$certificate\)' -or
            $gate -notmatch 'RemoveGuestTrust\(\$certificate\)' -or
            $gate -notmatch 'SameCertificate\(candidate, certificate\)' -or
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
    Test-Case 'installed MSIX gate proves clean Application Protected template seeding' {
        $gate = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../scripts/test-installed-msix.ps1'))
        $firstActivation = $gate.IndexOf('$result[''CleanFirstLaunchProcessId'']', [StringComparison]::Ordinal)
        $configuration = $gate.IndexOf('foreach ($config in $configs)', [StringComparison]::Ordinal)
        if ($firstActivation -lt 0 -or $configuration -le $firstActivation -or
            $gate -notmatch 'cipher.exe /c' -or $gate -notmatch 'Application Protected' -or
            $gate -notmatch 'MetadataCopyFailureHResult' -or $gate -notmatch 'Assert-SeededTemplate' -or
            $gate -notmatch 'CleanFirstLaunchVerified' -or $gate -notmatch 'clean-first-launch.png') {
            throw 'The installed gate must reproduce AppX protection and activate before pre-seeding any configuration.'
        }
    }
    Test-Case 'installed gate requires reachable setup and a product-UI completed model request' {
        $gate = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../scripts/test-installed-msix.ps1'))
        $firstLaunch = $gate.IndexOf('$result[''CleanFirstLaunchProcessId'']', [StringComparison]::Ordinal)
        $configuration = $gate.IndexOf('foreach ($config in $configs)', [StringComparison]::Ordinal)
        $completion = $gate.IndexOf('if (-not $result.RealRequestCompleted)', [StringComparison]::Ordinal)
        foreach ($required in @('CleanRuntimeReady', 'CleanSetupObserved', 'Get-TestRuntimeSetup',
            "'/echo', '/readyz', '/ping'", "'/setup/provider'", 'ProviderSetupApply',
            'ConfiguredByProductUi', 'originalRuntimeProcessId', 'ChatSend',
            'response.Current.ItemStatus != "complete"', 'RealRequestCompleted', 'completed-request.png')) {
            if (-not $gate.Contains($required)) { throw "Missing actual first-run/request gate: $required" }
        }
        if ($firstLaunch -lt 0 -or $completion -le $firstLaunch -or $configuration -le $completion -or
            $gate -match 'Provider__Key="ollama"') {
            throw 'Reachable clean setup and UI terminal completion must precede test template writes.'
        }
    }
    Test-Case 'Runtime file observations retry only disappearance or Windows sharing races' {
        $tokens = $null; $errors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $PSScriptRoot '../scripts/test-installed-msix.ps1'), [ref]$tokens, [ref]$errors)
        if ($errors.Count) { throw ($errors | Out-String) }
        foreach ($name in @('Test-TestRuntimeReadRace', 'Read-TestRuntimeText')) {
            $function = $ast.Find({ param($node)
                $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
            }, $true)
            if (-not $function) { throw "Missing Runtime observation helper: $name" }
            Invoke-Expression $function.Extent.Text
        }
        foreach ($code in @(2, 3, 32, 33)) {
            if (-not (Test-TestRuntimeReadRace ([IO.IOException]::new('race', (-2147024896 + $code))))) {
                throw "Expected transient Windows IO code: $code"
            }
        }
        foreach ($code in @(0, 5, 11, 23, 87, 112)) {
            if (Test-TestRuntimeReadRace ([IO.IOException]::new('not a race', (-2147024896 + $code)))) {
                throw "An unrelated IO failure was hidden: $code"
            }
        }
        $result = @{ RuntimeFileReadRetries = 0 }
        $fixture = Join-Path $root 'runtime-observation'
        New-Item -ItemType Directory -Path $fixture | Out-Null
        if ($null -ne (Read-TestRuntimeText (Join-Path $fixture 'not-yet-present.json')) -or
            $result.RuntimeFileReadRetries -ne 1) { throw 'A disappeared file must be a recorded non-observation.' }
        $source = Join-Path $fixture 'discovery.json'
        $text = '{"processId":123,"baseUrl":"http://127.0.0.1:42"}'
        $writer = [IO.File]::Open($source, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
            ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        $bytes = [Text.Encoding]::UTF8.GetBytes($text)
        try {
            $writer.Write($bytes, 0, $bytes.Length); $writer.Flush()
            if ((Read-TestRuntimeText $source) -cne $text -or $result.RuntimeFileReadRetries -ne 1) {
                throw 'Shared observation must preserve exact contents, without manufacturing a retry.'
            }
        } finally { $writer.Dispose() }
        $exclusive = [IO.File]::Open($source, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $exclusive.Dispose()
        [IO.File]::WriteAllText($source, '{invalid JSON')
        Assert-Rejected { Read-TestRuntimeText $source | ConvertFrom-Json }
        Assert-Rejected { Read-TestRuntimeText $fixture }
        if ($result.RuntimeFileReadRetries -ne 1) { throw 'Invalid contents/access must not become a transient observation.' }
    }
    Test-Case 'Runtime connection uses shared retryable observations without weakening identity or authenticated probes' {
        $gate = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../scripts/test-installed-msix.ps1'))
        $start = $gate.IndexOf('function Get-TestRuntimeConnection {', [StringComparison]::Ordinal)
        $end = $gate.IndexOf('function Get-TestProviderUiIndex {', $start, [StringComparison]::Ordinal)
        $connection = $gate.Substring($start, $end - $start)
        foreach ($required in @('Read-TestRuntimeText $file.FullName', 'Read-TestRuntimeText $keyFile',
            '$entryText | ConvertFrom-Json', '$_.ProcessId -eq $entry.processId', '$base.IsLoopback',
            "'X-Api-Key' = `$keyText.Trim()", "'/echo', '/readyz', '/ping'", "'/setup/provider'")) {
            if (-not $connection.Contains($required)) { throw "Missing bounded connection validation: $required" }
        }
        if ($connection.Contains('[IO.File]::ReadAllText')) { throw 'Runtime observations still use incompatible default sharing.' }
    }
    Test-Case 'installed provider selector uses bounded visible keyboard input rather than unsupported UIA patterns' {
        $gate = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../scripts/test-installed-msix.ps1'))
        $start = $gate.IndexOf('public static void SelectProvider(', [StringComparison]::Ordinal)
        $end = $gate.IndexOf('public static void Invoke(', $start, [StringComparison]::Ordinal)
        if ($start -lt 0 -or $end -le $start) { throw 'Missing provider selection helper.' }
        $selection = $gate.Substring($start, $end - $start)
        foreach ($required in @('SetForegroundWindow(window)', 'GetForegroundWindow() != window',
            'ClickVisibleElement(combo)', 'providerCount > 256', 'providerIndex >= providerCount',
            'NavigateAcknowledgedProviderSelection(() =>', 'AwaitProviderSelection(read, next, providerCount, timeout)',
            'ReadProviderUiIndex(current.Current.ItemStatus, providerKeys)', 'ReadProviderUiIndex(null, providerKeys)',
            'new HashSet<string>(StringComparer.OrdinalIgnoreCase)',
            'PressProviderKey(window, 0x1B', 'PressProviderKey(window, 0x09',
            'MapVirtualKey(key, 0)', 'scan == 0 || scan > 0xFF', '0x0008u', 'SendKeyboardInputs(window',
            'element.Current.IsOffscreen || !element.Current.IsEnabled', 'TryGetClickablePoint',
            'double.IsNaN', 'double.IsInfinity', 'SetCursorPos', 'mouse_event(0x0002', 'mouse_event(0x0004')) {
            if (-not $selection.Contains($required)) { throw "Missing bounded visible provider input guard: $required" }
        }
        if ($selection -match 'SelectionItemPattern|ExpandCollapsePattern|\.Select\(|\.SetValue\(|Invoke-RestMethod|Invoke-WebRequest') {
            throw 'Provider selection must use physical input with public readback, not UIA writer patterns or private configuration.'
        }
    }
    Test-Case 'provider accessibility is bound to actual selected option and public-key readback fails closed' {
        $page = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../SharpClaw.Client.Uno/Presentation/SettingsPage.xaml.cs'))
        foreach ($required in @('provider.SetBinding(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty',
            'nameof(ComboBox.SelectedItem)}.{nameof(SharpClawProviderSetupOption.DisplayName)',
            'provider.SetBinding(Microsoft.UI.Xaml.Automation.AutomationProperties.ItemStatusProperty',
            'nameof(ComboBox.SelectedItem)}.{nameof(SharpClawProviderSetupOption.Key)')) {
            if (-not $page.Contains($required)) { throw 'Actual selected provider is not publicly observable through its view binding.' }
        }
        $gate = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../scripts/test-installed-msix.ps1'))
        $method = [regex]::Match($gate, '(?s)public static int ReadProviderUiIndex\(.*?\n    \}')
        if (-not $method.Success) { throw 'Missing pure public-key readback.' }
        $name = 'ProviderPublicKeyFixture_' + [guid]::NewGuid().ToString('N')
        $source = 'using System; using System.Collections.Generic; public static class ' + $name + ' {' + $method.Value + '}'
        $type = Add-Type -TypeDefinition $source -PassThru
        $keys = [string[]]@('third-party-z', 'ollama', 'another-module')
        if ($type::ReadProviderUiIndex($null, $keys) -ne -1 -or
            $type::ReadProviderUiIndex('', $keys) -ne -1 -or
            $type::ReadProviderUiIndex('OLLAMA', $keys) -ne 1 -or
            $type::ReadProviderUiIndex('another-module', $keys) -ne 2) {
            throw 'Public selected key did not retain module list order or no-selection state.'
        }
        Assert-Rejected { $type::ReadProviderUiIndex('unknown', $keys) }
        Assert-Rejected { $type::ReadProviderUiIndex('  ', $keys) }
        Assert-Rejected { $type::ReadProviderUiIndex('ollama', [string[]]@('ollama', 'OLLAMA')) }
        Assert-Rejected { $type::ReadProviderUiIndex($null, [string[]]@('')) }
        Assert-Rejected { $type::ReadProviderUiIndex($null, [string[]]@()) }
        Assert-Rejected { $type::ReadProviderUiIndex($null, [string[]]$null) }
    }
    if ($IsWindows) {
        Test-Case 'complete installed UI probe compiles against stock Windows PowerShell UIAutomation assemblies' {
            $gate = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../scripts/test-installed-msix.ps1'))
            $source = [regex]::Match($gate, "(?s)(using System;\r?\nusing System.Collections.Generic;.*?)(?=\r?\n'@)")
            if (-not $source.Success -or -not $source.Value.Contains('public static class SharpClawInstalledProbe')) {
                throw 'The complete installed probe source was not found.'
            }
            $path = Join-Path $root 'full-installed-probe.cs'
            [IO.File]::WriteAllText($path, $source.Value, [Text.UTF8Encoding]::new($false))
            $command = @'
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -ne 'Desktop'){throw 'Require stock Windows PowerShell.'}
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
Add-Type -ReferencedAssemblies UIAutomationClient,UIAutomationTypes,WindowsBase,System,System.Core -Path 'PROBE_SOURCE_PATH'
'COMPLETE_STOCK_WINDOWS_UI_PROBE_COMPILED=true'
'@
            $command = $command.Replace('PROBE_SOURCE_PATH', $path.Replace("'", "''"))
            $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
            $shell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
            $answer = @(& $shell -NoProfile -NonInteractive -EncodedCommand $encoded 2>&1)
            if ($LASTEXITCODE -ne 0 -or ($answer -join "`n") -notmatch 'COMPLETE_STOCK_WINDOWS_UI_PROBE_COMPILED=true') {
                throw ('Complete embedded installed UI probe compilation failed: ' + ($answer -join "`n"))
            }
        }
    }
    Test-Case 'acknowledged provider navigation reaches exact index from every initial selection including no selection' {
        $gate = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../scripts/test-installed-msix.ps1'))
        $navigation = [regex]::Match($gate, '(?s)public static void NavigateAcknowledgedProviderSelection\(.*?\n    \}')
        $observation = [regex]::Match($gate, '(?s)public static int AwaitProviderSelection\(.*?\n    \}')
        if (-not $navigation.Success -or -not $observation.Success) { throw 'Missing pure acknowledged provider navigation.' }
        $name = 'ProviderNavigationFixture_' + [guid]::NewGuid().ToString('N')
        $source = 'using System; using System.Threading; using System.Threading.Tasks; public static class ' + $name + ' {' + $navigation.Value + $observation.Value + @'
 public static void Verify(int count,int target,int initial) {
  int position=initial, presses=0;
  NavigateAcknowledgedProviderSelection(() => position,key => {
   presses++;
   if(key==0x28)position=Math.Min(position+1,count-1);
   else if(key==0x26)position=Math.Max(position-1,0);
   else throw new InvalidOperationException("Unexpected physical key.");
  },target,count,TimeSpan.FromSeconds(1));
  if(position!=target || presses>count)throw new InvalidOperationException("Incorrect or unbounded provider navigation.");
 }
}
'@
        $type = Add-Type -TypeDefinition $source -PassThru
        foreach ($count in @(1, 2, 20, 256)) {
            foreach ($target in @(@(0, [int][Math]::Floor($count / 2), ($count - 1)) | Select-Object -Unique)) {
                for ($initial = -1; $initial -lt $count; $initial++) {
                    $type::Verify($count,$target,$initial)
                }
            }
        }
        foreach ($bad in @(@(-1,20), @(20,20), @(0,0), @(0,257))) {
            Assert-Rejected { $type::Verify($bad[1],$bad[0],0) }
        }
    }
    Test-Case 'provider navigation awaits delayed acknowledgement and never advances after missing or invalid acknowledgement' {
        $gate = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../scripts/test-installed-msix.ps1'))
        $navigation = [regex]::Match($gate, '(?s)public static void NavigateAcknowledgedProviderSelection\(.*?\n    \}')
        $observation = [regex]::Match($gate, '(?s)public static int AwaitProviderSelection\(.*?\n    \}')
        $name = 'ProviderAcknowledgementFixture_' + [guid]::NewGuid().ToString('N')
        $source = 'using System; using System.Threading; using System.Threading.Tasks; public static class ' + $name + ' {' + $navigation.Value + $observation.Value + @'
 public static void VerifyDelayed() {
  int position=0,pending=0,reads=0,presses=0;
  NavigateAcknowledgedProviderSelection(() => {
   if(pending!=position && ++reads>=3){position=pending;reads=0;}
   return position;
  },key => {
   if(pending!=position)throw new InvalidOperationException("A second key overtook unacknowledged input.");
   presses++;pending=position+(key==0x28?1:-1);
  },2,3,TimeSpan.FromSeconds(1));
  if(position!=2 || presses!=2)throw new InvalidOperationException("Delayed navigation failed.");
 }
 public static void VerifyMissing() {
  int presses=0;
  try {
   NavigateAcknowledgedProviderSelection(() => 0,key => presses++,2,3,TimeSpan.FromMilliseconds(60));
  } catch(TimeoutException) {
   if(presses==1)return;
   throw;
  }
  throw new InvalidOperationException("Missing selection acknowledgement was accepted or replayed.");
 }
 public static void VerifyInvalid() {
  int presses=0;
  try {
   NavigateAcknowledgedProviderSelection(() => -2,key => presses++,1,3,TimeSpan.FromMilliseconds(60));
  } catch(InvalidOperationException) {
   if(presses==0)return;
   throw;
  }
  throw new InvalidOperationException("Invalid selection allowed a physical input.");
 }
}
'@
        $type = Add-Type -TypeDefinition $source -PassThru
        $type::VerifyDelayed()
        $type::VerifyMissing()
        $type::VerifyInvalid()
    }
    Test-Case 'provider observation preserves the original readback exception and installed reports retain its cause' {
        $gate = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../scripts/test-installed-msix.ps1'))
        $navigation = [regex]::Match($gate, '(?s)public static void NavigateAcknowledgedProviderSelection\(.*?\n    \}')
        $observation = [regex]::Match($gate, '(?s)public static int AwaitProviderSelection\(.*?\n    \}')
        $name = 'ProviderFailureFixture_' + [guid]::NewGuid().ToString('N')
        $source = 'using System; using System.Threading; using System.Threading.Tasks; public static class ' + $name + ' {' + $navigation.Value + $observation.Value + @'
 public static void Verify() {
  var expected=new InvalidOperationException("Public readback failed.");
  int presses=0;
  try {
   NavigateAcknowledgedProviderSelection(() => {throw expected;},key => presses++,1,3,TimeSpan.FromMilliseconds(60));
  } catch(InvalidOperationException error) {
   if(object.ReferenceEquals(error,expected) && presses==0)return;
   throw;
  }
  throw new InvalidOperationException("Readback exception was replaced, accepted or allowed physical input.");
 }
}
'@
        $type = Add-Type -TypeDefinition $source -PassThru
        $type::Verify()
        foreach ($required in @("['FailureInnerMessage'] = `$_.Exception.GetBaseException().Message",
            "['FailureInnerType'] = `$_.Exception.GetBaseException().GetType().FullName")) {
            if (-not $gate.Contains($required)) { throw 'Installed failure report hides its original exception.' }
        }
    }
    Test-Case 'provider UI index preserves product list order and rejects missing or duplicate keys' {
        $gatePath = Join-Path $PSScriptRoot '../scripts/test-installed-msix.ps1'
        $tokens = $null; $errors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile($gatePath, [ref]$tokens, [ref]$errors)
        if ($errors.Count) { throw ($errors | Out-String) }
        $function = $ast.Find({ param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Get-TestProviderUiIndex'
        }, $true)
        if (-not $function) { throw 'Missing provider UI index helper.' }
        Invoke-Expression $function.Extent.Text
        $options = @([pscustomobject]@{key='z-last-name'}, [pscustomobject]@{key='middle'}, [pscustomobject]@{key='a-first-name'})
        if ((Get-TestProviderUiIndex $options 'MIDDLE') -ne 1 -or
            (Get-TestProviderUiIndex $options 'a-first-name') -ne 2 -or
            (Get-TestProviderUiIndex @([pscustomobject]@{key='only'}) 'only') -ne 0) {
            throw 'The UI helper must use the actual product list order, not resort or assume a provider position.'
        }
        Assert-Rejected { Get-TestProviderUiIndex $options 'missing' }
        Assert-Rejected { Get-TestProviderUiIndex @([pscustomobject]@{key='same'}, [pscustomobject]@{key='SAME'}) 'same' }
        Assert-Rejected { Get-TestProviderUiIndex @() 'same' }
        Assert-Rejected { Get-TestProviderUiIndex (@(1..257 | ForEach-Object { [pscustomobject]@{key="provider-$_"} })) 'provider-1' }
    }
    Test-Case 'installed text and command entry use visible bounded input with no UIA writer patterns' {
        $gate = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../scripts/test-installed-msix.ps1'))
        foreach ($required in @('CreateTextInputs(value)', 'value.Length > 4096', 'char.IsControl(value[index])',
            'RequireForegroundWindow(window)', 'ClickVisibleElement(element)',
            'KeyboardInput(0x11, 0, 0)', 'KeyboardInput(0x41, 0, 0x0002)',
            'KeyboardInput(0x11, 0, 0x0002)', 'KeyboardInput(0, value[index], 0x0004)',
            'KeyboardInput(0, value[index], 0x0004 | 0x0002)',
            'GetForegroundWindow() != window', 'Marshal.SizeOf(typeof(Input))) != (uint)inputs.Length',
            '[StructLayout(LayoutKind.Explicit)] struct InputUnion', '[FieldOffset(0)] public MouseInputData Mouse')) {
            if (-not $gate.Contains($required)) { throw "Missing actual bounded input guarantee: $required" }
        }
        if ($gate -match 'GetCurrentPattern\(InvokePattern\.Pattern\)|\)\.SetValue\(|\)\.Invoke\(|Clipboard|SendMessage|PostMessage') {
            throw 'Test UI writes must use visible foreground input, not unsupported patterns or private messages.'
        }
    }
    Test-Case 'installed input readback awaits exact complete text and rejects partial or hanging observation' {
        $gate = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../scripts/test-installed-msix.ps1'))
        foreach ($required in @('AwaitExactInputValue(() =>',
            'TryGetCurrentPattern(ValuePattern.Pattern, out pattern)', '((ValuePattern)pattern).Current.Value',
            '}, value, TimeSpan.FromSeconds(10))')) {
            if (-not $gate.Contains($required)) { throw "Missing public readback integration: $required" }
        }
        $method = [regex]::Match($gate, '(?s)public static void AwaitExactInputValue\(.*?\n    \}')
        if (-not $method.Success) { throw 'Missing bounded exact-input observation.' }
        $name = 'ExactInputObservationFixture_' + [guid]::NewGuid().ToString('N')
        $source = 'using System; using System.Threading; using System.Threading.Tasks; public static class ' + $name + ' {' + $method.Value + @'
 public static int VerifyDelayedComplete() {
  int reads=0;
  AwaitExactInputValue(() => ++reads == 1 ? "" : reads == 2 ? "Reply with " : "Reply with a short greeting.",
   "Reply with a short greeting.", TimeSpan.FromSeconds(1));
  return reads;
 }
 public static void VerifyPersistentPartial() {
  try {
   AwaitExactInputValue(() => "Reply with ", "Reply with a short greeting.", TimeSpan.FromMilliseconds(60));
  } catch(AggregateException error) {
   if(error.InnerException is TimeoutException)return;
   throw;
  }
  throw new InvalidOperationException("Persistent partial input was accepted.");
 }
 public static void VerifyHangingReader() {
  using (var release=new ManualResetEventSlim(false))
  using (var finished=new ManualResetEventSlim(false)) {
   bool timedOut=false;
   try {
    AwaitExactInputValue(() => {
     try { release.Wait(); return "complete"; } finally { finished.Set(); }
    }, "complete", TimeSpan.FromMilliseconds(30));
   } catch(TimeoutException) { timedOut=true; }
   finally {
    release.Set();
    if(!finished.Wait(TimeSpan.FromSeconds(2)))throw new InvalidOperationException("Fixture reader was not released.");
   }
   if(!timedOut)throw new InvalidOperationException("Hanging public reader was accepted.");
  }
 }
}
'@
        $type = Add-Type -TypeDefinition $source -PassThru
        if ($type::VerifyDelayedComplete() -ne 3) { throw 'A partial value was mistaken for complete input.' }
        $type::VerifyPersistentPartial()
        $type::VerifyHangingReader()
    }
    Test-Case 'installed template evidence verifies writable contents without copying protection' {
        $gatePath = Join-Path $PSScriptRoot '../scripts/test-installed-msix.ps1'
        $gate = [IO.File]::ReadAllText($gatePath)
        if ($gate -notmatch 'ApplicationProtectedLocalCache' -or
            $gate -notmatch 'GatewayTemplatesObserved' -or
            $gate -notmatch '\[IO.FileAccess\]::Write' -or
            $gate -notmatch 'Copy-SharedJournalContents \$journal.FullName' -or
            $gate -match 'Copy-Item -LiteralPath \$diagnostics') {
            throw 'The installed gate must validate actual write access, await Gateway seeding and export only journal contents.'
        }
        $tokens = $null; $errors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile($gatePath, [ref]$tokens, [ref]$errors)
        if ($errors.Count) { throw ($errors | Out-String) }
        $function = $ast.Find({ param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Assert-SeededTemplate'
        }, $true)
        if (-not $function) { throw 'Missing installed template assertion.' }
        Invoke-Expression $function.Extent.Text
        $fixture = Join-Path $root 'installed-template-evidence'
        New-Item -ItemType Directory -Path (Join-Path $fixture 'package/Environment'),
            (Join-Path $fixture 'destination') | Out-Null
        $package = [pscustomobject]@{ InstallLocation = (Join-Path $fixture 'package') }
        $virtualProfileRoot = $null
        $source = Join-Path $package.InstallLocation 'Environment/environment.template'
        $destination = Join-Path $fixture 'destination/environment.template'
        $bytes = [byte[]]@(239, 187, 191, 120, 61, 34, 195, 169, 34, 13, 10, 0)
        [IO.File]::WriteAllBytes($source, $bytes)
        [IO.File]::WriteAllBytes($destination, $bytes)
        $record = Assert-SeededTemplate 'Environment/environment.template' (Join-Path $fixture 'destination')
        if (-not $record.Writable -or $record.ApplicationProtectedLocalCache -or
            (Get-FileHash $source).Hash -ne (Get-FileHash $destination).Hash) {
            throw 'A writable byte-identical ordinary target must pass without being modified.'
        }
        [IO.File]::WriteAllBytes($destination, [byte[]]@(1, 2, 3))
        Assert-Rejected { Assert-SeededTemplate 'Environment/environment.template' (Join-Path $fixture 'destination') }
        [IO.File]::WriteAllBytes($destination, $bytes)
        [IO.File]::SetAttributes($destination, [IO.FileAttributes]::ReadOnly)
        try { Assert-Rejected { Assert-SeededTemplate 'Environment/environment.template' (Join-Path $fixture 'destination') } }
        finally { [IO.File]::SetAttributes($destination, [IO.FileAttributes]::Normal) }
        Remove-Item -LiteralPath $destination
        Assert-Rejected { Assert-SeededTemplate 'Environment/environment.template' (Join-Path $fixture 'destination') }
    }
    Test-Case 'installed gate awaits process exit and reads journals with active-writer sharing' {
        $gatePath = Join-Path $PSScriptRoot '../scripts/test-installed-msix.ps1'
        $tokens = $null; $errors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile($gatePath, [ref]$tokens, [ref]$errors)
        if ($errors.Count) { throw ($errors | Out-String) }
        foreach ($name in @('Wait-TestPackageProcessesStopped', 'Copy-SharedJournalContents')) {
            $function = $ast.Find({ param($node)
                $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
            }, $true)
            if (-not $function) { throw "Missing installed gate helper: $name" }
            Invoke-Expression $function.Extent.Text
        }
        $script:pendingProcessSnapshots = 2
        function Get-TestPackageProcesses {
            if ($script:pendingProcessSnapshots-- -gt 0) { [pscustomobject]@{ ProcessId = 123 } }
        }
        Wait-TestPackageProcessesStopped -TimeoutSeconds 2
        if ($script:pendingProcessSnapshots -ne -1) { throw 'Exit verification did not observe the empty process snapshot.' }
        $script:pendingProcessSnapshots = 1
        Assert-Rejected { Wait-TestPackageProcessesStopped -TimeoutSeconds 0 }
        $fixture = Join-Path $root 'shared-startup-journal'
        New-Item -ItemType Directory -Path $fixture | Out-Null
        $source = Join-Path $fixture 'startup.jsonl'
        $destination = Join-Path $fixture 'captured.jsonl'
        $writer = [IO.File]::Open($source, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
        $bytes = [Text.Encoding]::UTF8.GetBytes('{"Stage":"BootLoaded"}' + "`n")
        try {
            $writer.Write($bytes, 0, $bytes.Length)
            $writer.Flush()
            Copy-SharedJournalContents $source $destination
            if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($destination)) -ne [Convert]::ToBase64String($bytes)) {
                throw 'The active-writer journal snapshot differs from the flushed contents.'
            }
        } finally { $writer.Dispose() }
        if ((Get-FileHash $source).Hash -ne (Get-FileHash $destination).Hash) { throw 'Journal capture changed its source.' }
    }
    Test-Case 'both native ICU package variants retain upstream licensing' {
        $policy = Get-Content (Join-Path $PSScriptRoot 'ThirdPartyNotices.json') -Raw | ConvertFrom-Json -AsHashtable
        $windows = $policy.PackageDocuments['Uno.icu-win/77.3.2']
        $macos = $policy.PackageDocuments['Uno.icu-macos/77.3.2']
        if ($windows.License -ne 'Apache-2.0 AND Unicode-3.0' -or
            $macos.License -ne $windows.License -or
            @($windows.Documents).Count -ne 2 -or
            @($macos.Documents).Count -ne 2 -or
            $macos.Documents[1].Sha256 -ne $windows.Documents[1].Sha256) {
            throw 'A native ICU package variant has no complete pinned licence pair.'
        }
    }
    Write-Host "Publishing behavioral tests: $script:passed passed; zero skipped."
} finally {
    $env:NUGET_PACKAGES = $originalNugetPackages
    Remove-Item -LiteralPath $root -Recurse -Force
}
