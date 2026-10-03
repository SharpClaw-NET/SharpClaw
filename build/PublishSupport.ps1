# Shared, fail-closed verification for deployment bundles and installers.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PublishAssetProvenance.ps1')

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
    Assert-RedistributionInventory $rootPath
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
            $declaredReadmes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($nuspec in $zip.Entries | Where-Object { $_.FullName -match '^[^/]+\.nuspec$' }) {
                $settings = [Xml.XmlReaderSettings]::new()
                $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
                $settings.XmlResolver = $null
                $stream = $nuspec.Open()
                $reader = $null
                try {
                    $reader = [Xml.XmlReader]::Create($stream, $settings)
                    $document = [Xml.Linq.XDocument]::Load($reader)
                    foreach ($readme in $document.Root.Elements() | Where-Object { $_.Name.LocalName -eq 'metadata' } |
                        ForEach-Object { $_.Elements() } | Where-Object { $_.Name.LocalName -eq 'readme' }) {
                        $path = $readme.Value
                        $null = Resolve-PayloadPath $directory $path
                        if ($null -eq $zip.GetEntry($path)) { throw "Missing declared package readme '$path'." }
                        $null = $declaredReadmes.Add($path)
                    }
                } finally {
                    if ($null -ne $reader) { $reader.Dispose() }
                    $stream.Dispose()
                }
            }
            foreach ($entry in $zip.Entries | Where-Object {
                $_.FullName -match '(^|/)(LICENSE[^/]*|THIRD-PARTY-NOTICES[^/]*|README[^/]*)$|^[^/]+\.nuspec$' -or
                $declaredReadmes.Contains($_.FullName)
            }) {
                $destination = Resolve-PayloadPath $directory $entry.FullName
                New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $false)
            }
        } finally { $zip.Dispose() }
    }
}

function Get-VerifiedLegalDocument {
    param([string]$Url, [string]$Sha256, [string]$CacheRoot)
    if ($Url -notmatch '^https://(raw\.githubusercontent\.com|www\.apache\.org|www\.gnu\.org)/' -or
        $Sha256 -notmatch '^[a-fA-F0-9]{64}$') {
        throw "Unapproved legal-document source '$Url'."
    }
    New-Item -ItemType Directory -Path $CacheRoot -Force | Out-Null
    $path = Join-Path $CacheRoot "$($Sha256.ToLowerInvariant()).txt"
    if (-not (Test-Path -LiteralPath $path)) {
        $temporary = "$path.partial"
        try {
            Invoke-WebRequest -Uri $Url -OutFile $temporary -MaximumRedirection 5
            Assert-FileDigest $temporary $Sha256
            Move-Item -LiteralPath $temporary -Destination $path
        } finally {
            if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
        }
    }
    Assert-FileDigest $path $Sha256
    return $path
}

function Get-ResolvedPackageArchive {
    param([string]$Id, [string]$Version, [string]$CacheRoot)
    $name = "$($Id.ToLowerInvariant()).$($Version.ToLowerInvariant()).nupkg"
    $globalCache = $env:NUGET_PACKAGES
    if (-not [string]::IsNullOrWhiteSpace($globalCache)) {
        $restored = Join-Path $globalCache "$($Id.ToLowerInvariant())/$($Version.ToLowerInvariant())/$name"
        if (Test-Path -LiteralPath $restored -PathType Leaf) { return $restored }
    }
    New-Item -ItemType Directory -Path $CacheRoot -Force | Out-Null
    $path = Join-Path $CacheRoot $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $temporary = "$path.partial"
        $url = "https://api.nuget.org/v3-flatcontainer/$($Id.ToLowerInvariant())/$($Version.ToLowerInvariant())/$name"
        try {
            Invoke-WebRequest -Uri $url -OutFile $temporary -MaximumRedirection 5
            Move-Item -LiteralPath $temporary -Destination $path
        } finally {
            if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
        }
    }
    return $path
}

function Get-NuspecMetadata {
    param([IO.Compression.ZipArchive]$Archive, [string]$ExpectedId, [string]$ExpectedVersion)
    $nuspecs = @($Archive.Entries | Where-Object { $_.FullName -match '^[^/\\]+\.nuspec$' })
    if ($nuspecs.Count -ne 1) { throw "Package $ExpectedId/$ExpectedVersion needs exactly one root nuspec." }
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $stream = $nuspecs[0].Open()
    $reader = $null
    try {
        $reader = [Xml.XmlReader]::Create($stream, $settings)
        $document = [Xml.Linq.XDocument]::Load($reader)
    } finally {
        if ($null -ne $reader) { $reader.Dispose() }
        $stream.Dispose()
    }
    $metadata = @($document.Root.Elements() | Where-Object { $_.Name.LocalName -eq 'metadata' })
    if ($metadata.Count -ne 1) { throw "Package $ExpectedId/$ExpectedVersion has no unique nuspec metadata." }
    $values = @{}
    foreach ($element in $metadata[0].Elements()) { $values[$element.Name.LocalName] = $element }
    if (-not $values.ContainsKey('id') -or -not $values.ContainsKey('version') -or
        $values['id'].Value -ine $ExpectedId -or $values['version'].Value -ine $ExpectedVersion) {
        throw "Package archive identity does not match $ExpectedId/$ExpectedVersion."
    }
    $license = if ($values.ContainsKey('license')) { $values['license'].Value.Trim() } else { '' }
    $licenseType = if ($values.ContainsKey('license') -and $null -ne $values['license'].Attribute('type')) {
        $values['license'].Attribute('type').Value
    } else { '' }
    $repository = if ($values.ContainsKey('repository')) { $values['repository'] } else { $null }
    return [pscustomobject]@{
        Nuspec = $nuspecs[0]
        License = $license
        LicenseType = $licenseType
        Copyright = if ($values.ContainsKey('copyright')) { $values['copyright'].Value.Trim() } else { '' }
        Readme = if ($values.ContainsKey('readme')) { $values['readme'].Value.Trim() } else { '' }
        RepositoryUrl = if ($null -ne $repository -and $null -ne $repository.Attribute('url')) {
            $repository.Attribute('url').Value
        } else { '' }
        RepositoryCommit = if ($null -ne $repository -and $null -ne $repository.Attribute('commit')) {
            $repository.Attribute('commit').Value
        } else { '' }
    }
}

function Copy-ResolvedDependencyNotices {
    param([string]$StageRoot, [string]$CacheRoot,
        [string]$PolicyPath = (Join-Path $PSScriptRoot 'ThirdPartyNotices.json'), [switch]$RequirePublishReceipts)
    $policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json -AsHashtable
    if ($policy.SchemaVersion -ne 1) { throw 'Unknown third-party-notice policy version.' }
    $scopes = @(Get-StageDependencyScopes $StageRoot)
    if ($scopes.Count -eq 0) { throw 'A published stage needs resolved dependency manifests.' }
    $provenance = Get-StageAssetProvenance $StageRoot $scopes $policy -RequirePublishReceipts:$RequirePublishReceipts
    $resolved = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($scope in $scopes) {
        foreach ($key in $scope.Libraries.Keys) {
            $type = $scope.Libraries[$key].type
            if ($type -notin @('package', 'runtimepack')) { continue }
            if ($key -notmatch '^(?:runtimepack\.)?([A-Za-z0-9][A-Za-z0-9_.+-]*)/([A-Za-z0-9][A-Za-z0-9.+-]*)$') {
                throw "Unsafe dependency identity '$key'."
            }
            $id = $Matches[1]; $version = $Matches[2]
            if ($id.StartsWith('SharpClaw.', [StringComparison]::OrdinalIgnoreCase)) { continue }
            $identity = "$id/$version"
            if (-not $resolved.ContainsKey($identity)) {
                $resolved.Add($identity, [pscustomobject]@{ Id = $id; Version = $version })
            }
        }
    }
    if ($resolved.Count -eq 0) { throw 'No resolved third-party dependencies were found.' }

    foreach ($asset in $provenance.Assets) {
        if (-not $resolved.ContainsKey($asset.Package)) {
            $parts = $asset.Package.Split('/')
            $resolved.Add($asset.Package, [pscustomobject]@{ Id = $parts[0]; Version = $parts[1] })
        }
    }
    $covered = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($asset in $provenance.Assets) { [void]$covered.Add($asset.Path) }
    foreach ($file in Get-ChildItem -LiteralPath $StageRoot -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($StageRoot, $file.FullName).Replace('\', '/')
        if ((Test-RedistributableAsset $relative) -and -not $covered.Contains($relative)) {
            throw "Unattributed redistributed binary, native asset, or font '$relative'."
        }
    }

    $records = [Collections.Generic.List[object]]::new()
    $noticeCache = Join-Path $CacheRoot 'documents'
    foreach ($identity in @($resolved.Keys | Sort-Object)) {
        $item = $resolved[$identity]
        $archivePath = Get-ResolvedPackageArchive $item.Id $item.Version (Join-Path $CacheRoot 'packages')
        $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
        $zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            $metadata = Get-NuspecMetadata $zip $item.Id $item.Version
            $packageAssets = @($provenance.Assets | Where-Object Package -IEQ $identity)
            foreach ($asset in $packageAssets) {
                Assert-ArchiveAssetDigest $zip $asset.ArchiveEntry $asset.InputSha256
                if ($asset.Transformation -eq 'ready-to-run') {
                    Assert-ReadyToRunManagedIdentity $zip $asset.ArchiveEntry (Resolve-PayloadPath $StageRoot $asset.Path)
                }
            }
            $override = if ($policy.PackageDocuments.ContainsKey($identity)) { $policy.PackageDocuments[$identity] } else { $null }
            $license = $metadata.License
            if ($metadata.LicenseType -eq 'file') {
                if ([string]::IsNullOrWhiteSpace($license)) { throw "Missing declared license file for $identity." }
                $licenseExpression = "file:$license"
            } elseif ($metadata.LicenseType -eq 'expression') {
                $licenseExpression = $license
            } elseif ($null -ne $override) {
                $licenseExpression = $override.License
            } else {
                throw "No approved license expression or exact override for $identity."
            }
            if ($null -ne $override -and $metadata.LicenseType -eq 'expression' -and
                -not $override.License.StartsWith($licenseExpression, [StringComparison]::Ordinal)) {
                throw "Curated license identity disagrees with the nuspec for $identity."
            }
            if ($null -ne $override -and $metadata.LicenseType -eq 'expression') {
                $licenseExpression = $override.License
            }
            if ([string]::IsNullOrWhiteSpace($licenseExpression)) { throw "Blank license for $identity." }
            if ($licenseExpression -notmatch '^(file:|MIT$|Apache-2\.0(?: AND .+)?$|PostgreSQL$|AGPL-3\.0-(?:only|or-later)$|LGPL-2\.1-or-later$|MS-PL$)') {
                throw "Unreviewed license expression '$licenseExpression' in $identity."
            }
            $directory = Join-Path $StageRoot "legal/third-party/$($item.Id).$($item.Version)"
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
            $documents = [Collections.Generic.List[object]]::new()
            $seenEntries = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            $declaredLicenseFound = $metadata.LicenseType -ne 'file'
            $declaredReadmeFound = [string]::IsNullOrWhiteSpace($metadata.Readme)
            foreach ($entry in $zip.Entries) {
                if ([string]::IsNullOrEmpty($entry.Name)) { continue }
                $null = Resolve-PayloadPath $directory $entry.FullName
                if (-not $seenEntries.Add($entry.FullName)) { throw "Case-colliding archive entry in $identity." }
                $isDeclaredLicense = $metadata.LicenseType -eq 'file' -and $entry.FullName -ceq $metadata.License
                $isDeclaredReadme = -not [string]::IsNullOrWhiteSpace($metadata.Readme) -and $entry.FullName -ceq $metadata.Readme
                if ($isDeclaredLicense) { $declaredLicenseFound = $true }
                if ($isDeclaredReadme) { $declaredReadmeFound = $true }
                if ($entry.FullName -cne $metadata.Nuspec.FullName -and -not $isDeclaredLicense -and
                    -not $isDeclaredReadme -and
                    $entry.Name -notmatch '(?i)^(LICENSE[^/]*|COPYING[^/]*|NOTICE[^/]*|THIRD[-_.]?PARTY[-_.]?NOTICES?[^/]*|README[^/]*)$') {
                    continue
                }
                $destination = Resolve-PayloadPath $directory $entry.FullName
                New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $false)
                $documents.Add([pscustomobject]@{
                    Path = [IO.Path]::GetRelativePath($StageRoot, $destination).Replace('\', '/')
                    Sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
                    Source = 'package-archive'
                })
            }
            if (-not $declaredLicenseFound -or -not $declaredReadmeFound) {
                throw "Declared license/readme was absent from $identity."
            }
            $baseLicense = $licenseExpression.Split(' ')[0]
            $embeddedLicenseCount = @($documents | Where-Object {
                $_.Path -match '(?i)(^|/)(LICENSE|COPYING)[^/]*$'
            }).Count
            if ($baseLicense -eq 'MIT' -and [string]::IsNullOrWhiteSpace($metadata.Copyright) -and
                $embeddedLicenseCount -eq 0) {
                throw "MIT package $identity has no retained copyright notice."
            }
            if ($policy.StandardLicenses.ContainsKey($baseLicense) -and
                -not ($baseLicense -eq 'MIT' -and $embeddedLicenseCount -gt 0)) {
                $source = $policy.StandardLicenses[$baseLicense]
                $sourcePath = Get-VerifiedLegalDocument $source.Url $source.Sha256 $noticeCache
                $destination = Join-Path $directory "SPDX-$baseLicense.txt"
                if ($baseLicense -eq 'MIT') {
                    $template = [IO.File]::ReadAllText($sourcePath)
                    if (-not $template.Contains('Copyright (c) <year> <copyright holders>')) {
                        throw 'The pinned MIT license template changed unexpectedly.'
                    }
                    [IO.File]::WriteAllText($destination,
                        $template.Replace('Copyright (c) <year> <copyright holders>', $metadata.Copyright),
                        [Text.UTF8Encoding]::new($false))
                } else {
                    Copy-Item -LiteralPath $sourcePath -Destination $destination
                }
                $documents.Add([pscustomobject]@{
                    Path = [IO.Path]::GetRelativePath($StageRoot, $destination).Replace('\', '/')
                    Sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
                    Source = $source.Url
                })
            }
            $extra = [Collections.Generic.List[object]]::new()
            if ($null -ne $override) {
                foreach ($doc in @($override.Documents)) { $extra.Add($doc) }
            }
            $repositoryUrl = $metadata.RepositoryUrl -replace '\.git$', ''
            foreach ($rule in @($policy.RepositoryDocuments)) {
                if ($repositoryUrl -ieq $rule.Url -and $metadata.RepositoryCommit -ceq $rule.Commit) {
                    foreach ($doc in @($rule.Documents)) { $extra.Add($doc) }
                }
            }
            foreach ($doc in $extra) {
                if ($doc.Name -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]*$') {
                    throw "Unsafe curated document name for $identity."
                }
                $sourcePath = Get-VerifiedLegalDocument $doc.Url $doc.Sha256 $noticeCache
                $destination = Join-Path $directory $doc.Name
                if (Test-Path -LiteralPath $destination) { throw "Duplicate curated document in $identity." }
                Copy-Item -LiteralPath $sourcePath -Destination $destination
                $documents.Add([pscustomobject]@{
                    Path = [IO.Path]::GetRelativePath($StageRoot, $destination).Replace('\', '/')
                    Sha256 = $doc.Sha256.ToUpperInvariant()
                    Source = $doc.Url
                })
            }
            if (@($documents | Where-Object { $_.Path -match '(?i)(LICENSE|COPYING|^legal/third-party/.+/SPDX-)' }).Count -eq 0) {
                throw "No distributable license text was collected for $identity."
            }
            $records.Add([pscustomobject]@{
                Id = $item.Id; Version = $item.Version; License = $licenseExpression
                Copyright = $metadata.Copyright; RepositoryUrl = $metadata.RepositoryUrl
                RepositoryCommit = $metadata.RepositoryCommit; ArchiveSha256 = $archiveHash
                Documents = @($documents); Assets = $packageAssets; PayloadFiles = @($packageAssets | ForEach-Object Path | Sort-Object)
            })
        } finally { $zip.Dispose() }
    }
    foreach ($receipt in $provenance.PublishReceipts) {
        if ($null -eq $receipt.Compiler) { continue }
        $parts = $receipt.Compiler.Package.Split('/')
        $archive = Get-ResolvedPackageArchive $parts[0] $parts[1] (Join-Path $CacheRoot 'packages')
        $zip = [IO.Compression.ZipFile]::OpenRead($archive)
        try {
            $null = Get-NuspecMetadata $zip $parts[0] $parts[1]
            Assert-ArchiveAssetDigest $zip $receipt.Compiler.ArchiveEntry $receipt.Compiler.Sha256
        } finally { $zip.Dispose() }
        $receipt.Compiler | Add-Member -NotePropertyName ArchiveSha256 -NotePropertyValue (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    }
    $policyDestination = Join-Path $StageRoot 'legal/notice-policy.json'
    Copy-Item -LiteralPath $PolicyPath -Destination $policyDestination
    $inventory = [pscustomobject]@{
        SchemaVersion = 2
        PolicySha256 = (Get-FileHash -LiteralPath $PolicyPath -Algorithm SHA256).Hash
        PolicyPath = 'legal/notice-policy.json'; RequirePublishReceipts = [bool]$RequirePublishReceipts
        DependencyManifests = @($scopes.Path | Sort-Object)
        PublishReceipts = $provenance.PublishReceipts
        BundledDependencies = @($provenance.Assets | Where-Object { $_.DependencyManifest -eq '' })
        Packages = @($records)
    }
    $inventoryPath = Join-Path $StageRoot 'legal/redistribution-inventory.json'
    $inventory | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $inventoryPath -Encoding utf8
    Assert-RedistributionInventory $StageRoot $CacheRoot
}

function Assert-RedistributionInventory {
    param([string]$StageRoot, [string]$CacheRoot = (Join-Path ([IO.Path]::GetTempPath()) 'sharpclaw-legal-verification'))
    $path = Join-Path $StageRoot 'legal/redistribution-inventory.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Missing redistribution inventory.' }
    $inventory = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($inventory.SchemaVersion -ne 2 -or @($inventory.Packages).Count -eq 0 -or
        $inventory.PolicySha256 -notmatch '^[a-fA-F0-9]{64}$' -or
        $inventory.RequirePublishReceipts -isnot [bool] -or $inventory.PolicyPath -cne 'legal/notice-policy.json') {
        throw 'Invalid redistribution inventory.'
    }
    $policyPath = Resolve-PayloadPath $StageRoot $inventory.PolicyPath
    Assert-FileDigest $policyPath $inventory.PolicySha256
    $policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json -AsHashtable
    $scopes = @(Get-StageDependencyScopes $StageRoot)
    if ((@($scopes.Path | Sort-Object) -join '|') -cne (@($inventory.DependencyManifests | Sort-Object) -join '|')) {
        throw 'Redistribution inventory has an incorrect dependency-scope set.'
    }
    $actual = Get-StageAssetProvenance $StageRoot $scopes $policy -RequirePublishReceipts:$inventory.RequirePublishReceipts
    $actualAssets = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($asset in $actual.Assets) { $actualAssets.Add($asset.Path, $asset) }
    if (@($actual.PublishReceipts).Count -ne @($inventory.PublishReceipts).Count) { throw 'Incorrect publish-receipt set.' }
    foreach ($receipt in @($inventory.PublishReceipts)) {
        $captured = @($actual.PublishReceipts | Where-Object Path -CEQ $receipt.Path)
        if ($captured.Count -ne 1 -or $receipt.Scope -cne $captured[0].Scope -or
            $receipt.DependencyManifest -cne $captured[0].DependencyManifest -or
            $receipt.SdkVersion -cne $captured[0].SdkVersion -or $receipt.Framework -cne $captured[0].Framework -or
            $receipt.Rid -cne $captured[0].Rid -or
            ($receipt.Documents | ConvertTo-Json -Compress) -cne ($captured[0].Documents | ConvertTo-Json -Compress)) {
            throw 'Publish input/output receipt changed.'
        }
        foreach ($document in @($receipt.Documents)) { Assert-FileDigest (Resolve-PayloadPath $StageRoot $document.Path) $document.Sha256 }
        if ($null -eq $receipt.Compiler) {
            if ($null -ne $captured[0].Compiler) { throw 'Missing ReadyToRun compiler provenance.' }
            continue
        }
        $compiler = $receipt.Compiler
        $tool = $captured[0].Compiler
        if ($null -eq $tool -or $compiler.Package -cne $tool.Package -or $compiler.ArchiveEntry -cne $tool.ArchiveEntry -or
            $compiler.Sha256 -ine $tool.Sha256 -or $compiler.TargetOS -cne $tool.TargetOS -or $compiler.TargetArch -cne $tool.TargetArch) {
            throw 'Incorrect ReadyToRun compiler provenance.'
        }
        $parts = $compiler.Package.Split('/')
        $archive = Get-ResolvedPackageArchive $parts[0] $parts[1] (Join-Path $CacheRoot 'packages')
        Assert-FileDigest $archive $compiler.ArchiveSha256
        $zip = [IO.Compression.ZipFile]::OpenRead($archive)
        try {
            $null = Get-NuspecMetadata $zip $parts[0] $parts[1]
            Assert-ArchiveAssetDigest $zip $compiler.ArchiveEntry $compiler.Sha256
        } finally { $zip.Dispose() }
    }
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $covered = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($scope in $scopes) {
        foreach ($key in $scope.Libraries.Keys) {
            if ($scope.Libraries[$key].type -notin @('package', 'runtimepack')) { continue }
            $normalized = $key -replace '^runtimepack\.', ''
            if ($normalized -notlike 'SharpClaw.*') { $null = $expected.Add($normalized) }
        }
    }
    foreach ($asset in @($inventory.BundledDependencies)) {
        if ($asset.Path -notmatch '(^|/)contributions/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or
            $asset.Package -notmatch '^[A-Za-z0-9][A-Za-z0-9_.+-]*/[A-Za-z0-9][A-Za-z0-9.+-]*$') {
            throw 'Invalid bundled dependency provenance.'
        }
        if ($asset.DependencyManifest -cne '' -or -not $actualAssets.ContainsKey($asset.Path) -or
            ($asset | ConvertTo-Json -Compress) -cne ($actualAssets[$asset.Path] | ConvertTo-Json -Compress)) {
            throw 'Incorrect bundled dependency source coordinate.'
        }
        Assert-FileDigest (Resolve-PayloadPath $StageRoot $asset.Path) $asset.OutputSha256
        $null = $expected.Add($asset.Package)
    }
    $represented = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($package in @($inventory.Packages)) {
        $identity = "$($package.Id)/$($package.Version)"
        if (-not $represented.Add($identity) -or -not $expected.Contains($identity) -or
            $package.ArchiveSha256 -notmatch '^[a-fA-F0-9]{64}$' -or
            @($package.Documents).Count -eq 0) { throw "Invalid legal package identity '$identity'." }
        foreach ($document in @($package.Documents)) {
            if ($document.Path -notmatch '^legal/third-party/' -or [string]::IsNullOrWhiteSpace($document.Source)) {
                throw "Invalid legal document path for $identity."
            }
            Assert-FileDigest (Resolve-PayloadPath $StageRoot $document.Path) $document.Sha256
        }
        $assets = @($package.Assets)
        if ((@($assets | ForEach-Object Path | Sort-Object) -join '|') -cne (@($package.PayloadFiles | Sort-Object) -join '|')) {
            throw "Incorrect payload-file set for $identity."
        }
        if ($assets.Count -eq 0) { continue }
        $archive = Get-ResolvedPackageArchive $package.Id $package.Version (Join-Path $CacheRoot 'packages')
        Assert-FileDigest $archive $package.ArchiveSha256
        $zip = [IO.Compression.ZipFile]::OpenRead($archive)
        try {
            $null = Get-NuspecMetadata $zip $package.Id $package.Version
            foreach ($asset in $assets) {
                if ($asset.Package -ine $identity -or -not $actualAssets.ContainsKey($asset.Path) -or
                    ($asset | ConvertTo-Json -Compress) -cne ($actualAssets[$asset.Path] | ConvertTo-Json -Compress) -or
                    -not $covered.Add($asset.Path)) { throw "Incorrect or duplicate source attribution for '$($asset.Path)'." }
                Assert-ArchiveAssetDigest $zip $asset.ArchiveEntry $asset.InputSha256
                Assert-FileDigest (Resolve-PayloadPath $StageRoot $asset.Path) $asset.OutputSha256
                if ($asset.Transformation -eq 'ready-to-run') {
                    Assert-ReadyToRunManagedIdentity $zip $asset.ArchiveEntry (Resolve-PayloadPath $StageRoot $asset.Path)
                }
                if ($asset.Transformation -eq 'copy' -and $asset.InputSha256 -ine $asset.OutputSha256) {
                    throw "Unchanged-copy digest mismatch for '$($asset.Path)'."
                }
            }
        } finally { $zip.Dispose() }
    }
    if ($represented.Count -ne $expected.Count) { throw 'Redistribution inventory misses a resolved dependency.' }
    foreach ($file in Get-ChildItem -LiteralPath $StageRoot -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($StageRoot, $file.FullName).Replace('\', '/')
        if (-not (Test-RedistributableAsset $relative)) { continue }
        if (-not $covered.Contains($relative)) { throw "Redistribution inventory misses '$relative'." }
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
                # MH_MAGIC_64: an untyped 0xfeedfacf PowerShell literal is signed.
                if ([BitConverter]::ToUInt32($header, 0) -ne [uint32]4277009103 -or [BitConverter]::ToUInt32($header, 4) -ne $cpu) {
                    throw "macOS launcher does not match $Rid Mach-O architecture."
                }
            }
            default { throw "Unsupported launcher RID '$Rid'." }
        }
    } finally { $stream.Dispose() }
}
