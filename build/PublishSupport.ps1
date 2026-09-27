# Shared, fail-closed verification for deployment bundles and installers.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-PayloadPath {
    param([string]$Root, [string]$RelativePath)
    $relative = $RelativePath.Replace('\', '/')
    if ([string]::IsNullOrWhiteSpace($relative) -or $relative -match '(^/|:|[\x00-\x1f])' -or
        @($relative.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0) {
        throw "Unsafe payload path '$RelativePath'."
    }
    $resolved = [IO.Path]::GetFullPath((Join-Path $Root $relative))
    $parent = [IO.Path]::GetFullPath($Root)
    foreach ($part in $relative.Split('/')) {
        $parent = Join-Path $parent $part
        if ((Test-Path -LiteralPath $parent) -and
            ((Get-Item -LiteralPath $parent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Payload links are not allowed: '$RelativePath'."
        }
    }
    return $resolved
}

function Assert-FileDigest {
    param([string]$Path, [string]$Sha256, [long]$Length = -1)
    if ($Sha256 -notmatch '^[a-fA-F0-9]{64}$' -or
        -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Missing file or invalid SHA-256 for '$Path'."
    }
    $file = Get-Item -LiteralPath $Path -Force
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        ($Length -ge 0 -and $file.Length -ne $Length) -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Sha256) {
        throw "Payload integrity check failed for '$Path'."
    }
}

function Assert-FileInventory {
    param([string]$Root, [object[]]$Files, [string[]]$Excluded = @())
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $Files) {
        $path = Resolve-PayloadPath $Root $file.Path
        if (-not $expected.Add($file.Path.Replace('\', '/'))) {
            throw "Duplicate payload path '$($file.Path)'."
        }
        Assert-FileDigest $path $file.Sha256 $file.Length
    }
    foreach ($entry in Get-ChildItem -LiteralPath $Root -Recurse -Force) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Payload links are not allowed: '$($entry.FullName)'."
        }
        if (-not $entry.PSIsContainer) {
            $relative = [IO.Path]::GetRelativePath($Root, $entry.FullName).Replace('\', '/')
            if ($relative -notin $Excluded -and -not $expected.Contains($relative)) {
                throw "Unlisted payload file '$relative'."
            }
        }
    }
}

function Assert-PublishBom {
    param([string]$Root, [string]$ManifestSha256)
    $rootPath = [IO.Path]::GetFullPath($Root)
    $manifestPath = Join-Path $rootPath 'bom-manifest.json'
    Assert-FileDigest $manifestPath $ManifestSha256
    $bom = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if (@($bom.Packages).Count -eq 0) { throw 'The BOM must contain packages.' }
    $packageFiles = @($bom.Packages | ForEach-Object {
        if ($_.Name -notmatch '^[A-Za-z0-9][A-Za-z0-9_.+-]*\.nupkg$') { throw 'Invalid package filename.' }
        [pscustomobject]@{ Path = $_.Name; Length = $_.Length; Sha256 = $_.Sha256 }
    })
    Assert-FileInventory (Join-Path $rootPath 'feed') $packageFiles
    $bundlePath = Join-Path $rootPath 'bundle'
    $bundleManifestPath = Join-Path $bundlePath 'contribution-bundle-manifest.json'
    Assert-FileDigest $bundleManifestPath $bom.ContributionBundleManifestSha256
    $bundle = Get-Content -LiteralPath $bundleManifestPath -Raw | ConvertFrom-Json
    if ($bundle.ContributionCount -le 0 -or
        $bundle.ContributionCount -ne @($bundle.Contributions).Count) {
        throw 'Invalid or empty contribution bundle.'
    }
    $bundleFiles = @($bundle.Contributions | ForEach-Object {
        $contribution = $_
        if ($contribution.Id -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]*$' -or
            @($contribution.Files | Where-Object { $_.Path -eq 'package.json' }).Count -ne 1) {
            throw 'Every contribution needs a safe identity and one package.json.'
        }
        foreach ($file in $contribution.Files) {
            [pscustomobject]@{
                Path = "contributions/$($contribution.Id)/$($file.Path.Replace('\', '/'))"
                Length = $file.Length
                Sha256 = $file.Sha256
            }
        }
    })
    Assert-FileInventory $bundlePath $bundleFiles @('contribution-bundle-manifest.json')
    return [pscustomobject]@{
        Root = $rootPath; BundleRoot = $bundlePath; Manifest = $bom
        ManifestSha256 = $ManifestSha256; BundleFiles = $bundleFiles
    }
}

function Get-PayloadInventory {
    param([string]$Root)
    return @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{
            Path = [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
            Length = $_.Length
            Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
}

function Assert-PublishedStage {
    param([string]$Root, [string]$ManifestSha256)
    $rootPath = [IO.Path]::GetFullPath($Root)
    $path = Join-Path $rootPath 'publish-manifest.json'
    Assert-FileDigest $path $ManifestSha256
    $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($manifest.SourceCommit -notmatch '^[a-f0-9]{40}$' -or
        $manifest.Version -notmatch '^0\.5\.0-preview\.[1-9][0-9]*$' -or
        $manifest.InstallerVersion -notmatch '^0\.5\.0\.[1-9][0-9]*$' -or
        [version]$manifest.InstallerVersion -gt [version]'0.5.0.65535' -or
        $manifest.InstallerVersion -ne $manifest.Version.Replace('-preview.', '.') -or
        $manifest.BomManifestSha256 -notmatch '^[a-fA-F0-9]{64}$') {
        throw 'Invalid SharpClaw 0.5.0 installer identity.'
    }
    Assert-FileInventory $rootPath @($manifest.Files) @('publish-manifest.json')
    return $manifest
}

function Assert-InstallerSource {
    param([string]$RepositoryRoot, [string]$SourceCommit)
    $current = (& git -C $RepositoryRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $current -ne $SourceCommit -or
        (& git -C $RepositoryRoot status --porcelain)) {
        throw 'Installer templates and scripts must be from the clean, exact published source commit.'
    }
}

function Copy-PackageNotices {
    param([string]$FeedRoot, [string]$StageRoot)
    foreach ($archive in Get-ChildItem -LiteralPath $FeedRoot -File -Filter '*.nupkg') {
        $directory = Join-Path $StageRoot "legal/$($archive.BaseName)"
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        $zip = [IO.Compression.ZipFile]::OpenRead($archive.FullName)
        try {
            foreach ($entry in $zip.Entries | Where-Object {
                $_.FullName -match '(^|/)(LICENSE[^/]*|THIRD-PARTY-NOTICES[^/]*)$|^[^/]+\.nuspec$'
            }) {
                $destination = Resolve-PayloadPath $directory $entry.FullName
                New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $false)
            }
        } finally { $zip.Dispose() }
    }
}

function Assert-NoActiveSecrets {
    param([string]$Root)
    $secret = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force | Where-Object {
        $_.Name -in @('.env', '.dev.env', '.api-key', '.gateway-token', '.encryption-key') -or $_.Extension -eq '.pfx'
    })
    if ($secret.Count -ne 0) { throw 'Active configuration, credentials, or signing keys must never be published.' }
}

function Assert-NativeLauncher {
    param([string]$Path, [string]$Rid)
    $stream = [IO.File]::OpenRead($Path)
    try {
        $header = [byte[]]::new(64)
        $stream.ReadExactly($header)
        switch ($Rid) {
            'win-x64' {
                if ($header[0] -ne 0x4d -or $header[1] -ne 0x5a) { throw 'Windows launcher must be PE.' }
                $stream.Position = [BitConverter]::ToUInt32($header, 60)
                $pe = [byte[]]::new(6)
                $stream.ReadExactly($pe)
                if ([BitConverter]::ToUInt32($pe, 0) -ne 0x4550 -or [BitConverter]::ToUInt16($pe, 4) -ne 0x8664) {
                    throw 'Windows launcher must be x64 PE.'
                }
            }
            { $_ -in @('linux-x64', 'linux-arm64') } {
                $machine = if ($Rid -eq 'linux-x64') { 62 } else { 183 }
                if ([BitConverter]::ToUInt32($header, 0) -ne 0x464c457f -or $header[4] -ne 2 -or
                    $header[5] -ne 1 -or [BitConverter]::ToUInt16($header, 18) -ne $machine) {
                    throw "Linux launcher does not match $Rid ELF architecture."
                }
            }
            { $_ -in @('osx-x64', 'osx-arm64') } {
                $cpu = if ($Rid -eq 'osx-x64') { 0x1000007 } else { 0x100000c }
                if ([BitConverter]::ToUInt32($header, 0) -ne 0xfeedfacf -or [BitConverter]::ToUInt32($header, 4) -ne $cpu) {
                    throw "macOS launcher does not match $Rid Mach-O architecture."
                }
            }
            default { throw "Unsupported launcher RID '$Rid'." }
        }
    } finally { $stream.Dispose() }
}
