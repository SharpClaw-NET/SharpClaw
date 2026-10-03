# Exact, scoped publish/restore coordinates. Basenames are never searched across
# the stage or across a package archive to invent an asset origin.
function Test-RedistributableAsset {
    param([string]$Path)
    $name = [IO.Path]::GetFileName($Path)
    return $Path -notmatch '^(legal|provenance)/' -and
        $name -match '(?i)\.(dll|exe|so|dylib|a|dat|ttf|otf|woff2?)$' -and $name -notmatch '^SharpClaw\.'
}

function Join-ScopedAssetPath {
    param([string]$Scope, [string]$Path)
    if ($Scope -eq '.') { return $Path.Replace('\', '/') }
    return "$Scope/$Path".Replace('\', '/')
}

function Read-PublishReceiptRows {
    param([string]$Path, [string[]]$Fields)
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $parts = $line.Split('|')
        if ($parts.Count -ne $Fields.Count) { throw "Invalid publish receipt row in '$Path'." }
        $row = [ordered]@{}
        for ($index = 0; $index -lt $Fields.Count; $index++) { $row[$Fields[$index]] = $parts[$index] }
        [pscustomobject]$row
    }
}

function Get-PublishSourceCoordinate {
    param([string]$Path, [string]$PackageRoot)
    # Build paths are lexical evidence, not paths to open on the packaging host.
    # A Linux publish must still be verifiable by the Windows MSIX builder.
    $source = $Path.Replace('\', '/')
    $prefix = $PackageRoot.Replace('\', '/').TrimEnd('/') + '/'
    $comparison = if ($prefix -match '^[A-Za-z]:/') { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if ($prefix -notmatch '^(/|[A-Za-z]:/)' -or -not $source.StartsWith($prefix, $comparison)) {
        throw "Publish input is not inside its captured NuGet root: '$Path'."
    }
    $relative = $source.Substring($prefix.Length)
    if ($relative -notmatch '^([A-Za-z0-9][A-Za-z0-9_.+-]*)/([A-Za-z0-9][A-Za-z0-9.+-]*)/(.+)$') {
        throw "Publish input is not inside its scoped NuGet package root: '$Path'."
    }
    return [pscustomobject]@{ Package = "$($Matches[1])/$($Matches[2])"; Entry = $Matches[3] }
}

function Get-StageDependencyScopes {
    param([string]$StageRoot)
    foreach ($file in Get-ChildItem -LiteralPath $StageRoot -File -Recurse -Filter '*.deps.json') {
        $relative = [IO.Path]::GetRelativePath($StageRoot, $file.FullName).Replace('\', '/')
        if ($relative -match '^(legal|provenance)/') { continue }
        $deps = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -AsHashtable
        if (-not $deps.ContainsKey('libraries') -or -not $deps.ContainsKey('targets') -or
            -not $deps.ContainsKey('runtimeTarget') -or -not $deps.targets.ContainsKey($deps.runtimeTarget.name)) {
            throw "Dependency manifest '$relative' has no selected runtime target."
        }
        [pscustomobject]@{
            Path = $relative; Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            Scope = [IO.Path]::GetRelativePath($StageRoot, $file.DirectoryName).Replace('\', '/')
            Libraries = $deps.libraries; TargetName = $deps.runtimeTarget.name
            Target = $deps.targets[$deps.runtimeTarget.name]
        }
    }
}

function Add-ScopedAssetRequest {
    param([object]$Requests, [object]$Request)
    if ($Requests.ContainsKey($Request.Path)) {
        $existing = $Requests[$Request.Path]
        if ($existing.Package -ine $Request.Package -or $existing.ArchiveEntry -cne $Request.ArchiveEntry -or
            $existing.InputSha256 -ine $Request.InputSha256 -or $existing.OutputSha256 -ine $Request.OutputSha256) {
            throw "Conflicting scoped asset origins for '$($Request.Path)'."
        }
        return
    }
    $Requests.Add($Request.Path, $Request)
}

function Get-StageAssetProvenance {
    param([string]$StageRoot, [object[]]$Scopes, [object]$Policy, [switch]$RequirePublishReceipts)
    $requests = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    $receipts = [Collections.Generic.List[object]]::new()
    $receiptScopes = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in Get-ChildItem -LiteralPath $StageRoot -Recurse -File -Filter 'context.tsv') {
        $receiptDir = $file.Directory
        $scopeDir = $receiptDir.Parent.Parent.Parent
        $scope = [IO.Path]::GetRelativePath($StageRoot, $scopeDir.FullName).Replace('\', '/')
        $receiptPath = [IO.Path]::GetRelativePath($StageRoot, $receiptDir.FullName).Replace('\', '/')
        if ($receiptPath -notmatch '(^|/)provenance/publish-assets/[^/]+$') { continue }
        $contexts = @(Read-PublishReceiptRows $file.FullName @('PackageRoot', 'SdkVersion', 'Framework', 'Rid', 'Project'))
        if ($contexts.Count -ne 1 -or $contexts[0].Project.Replace('\', '/') -notmatch '^(/|[A-Za-z]:/)') { throw 'Invalid publish context.' }
        $context = $contexts[0]
        $depsPath = Join-ScopedAssetPath $scope "$($receiptDir.Name).deps.json"
        $model = @($Scopes | Where-Object Path -CEQ $depsPath)
        if ($model.Count -ne 1) { throw "No exact dependency scope for '$receiptPath'." }
        [void]$receiptScopes.Add($scope)
        $documents = @(Get-ChildItem -LiteralPath $receiptDir.FullName -File | ForEach-Object {
            [pscustomobject]@{ Path = [IO.Path]::GetRelativePath($StageRoot, $_.FullName).Replace('\', '/'); Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        } | Sort-Object Path)
        $inputs = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($input in Read-PublishReceiptRows (Join-Path $receiptDir.FullName 'inputs.tsv') @('Source', 'Sha256', 'Path', 'Id', 'Version')) {
            $null = Resolve-PayloadPath $scopeDir.FullName $input.Path.Replace('\', '/')
            if (-not $inputs.TryAdd($input.Path.Replace('\', '/'), $input)) { throw 'Duplicate publish input destination.' }
        }
        $r2r = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
        $r2rPath = Join-Path $receiptDir.FullName 'ready-to-run.tsv'
        if (Test-Path -LiteralPath $r2rPath) {
            foreach ($item in Read-PublishReceiptRows $r2rPath @('Source', 'Sha256', 'Output', 'Path')) {
                if (-not $r2r.TryAdd($item.Source, $item)) { throw 'Duplicate ReadyToRun input.' }
            }
        }
        $compiler = $null
        $compilerPath = Join-Path $receiptDir.FullName 'compiler.tsv'
        if (Test-Path -LiteralPath $compilerPath) {
            $tools = @(Read-PublishReceiptRows $compilerPath @('Source', 'Sha256', 'TargetOS', 'TargetArch'))
            if ($tools.Count -ne 1) { throw 'ReadyToRun needs one captured compiler.' }
            $coordinate = Get-PublishSourceCoordinate $tools[0].Source $context.PackageRoot
            if ($coordinate.Package -notmatch '^microsoft\.netcore\.app\.crossgen2\.(win|linux|osx)-(x64|arm64)/[^/]+$' -or
                $coordinate.Entry -notmatch '^tools/crossgen2(?:\.exe)?$') { throw 'Unsupported ReadyToRun compiler identity.' }
            $compiler = [pscustomobject]@{
                Package = $coordinate.Package; ArchiveEntry = $coordinate.Entry; Sha256 = $tools[0].Sha256
                TargetOS = $tools[0].TargetOS; TargetArch = $tools[0].TargetArch
            }
        }
        $receipts.Add([pscustomobject]@{
            Path = $receiptPath; Scope = $scope; DependencyManifest = $depsPath; SdkVersion = $context.SdkVersion
            Framework = $context.Framework; Rid = $context.Rid; Documents = $documents; Compiler = $compiler
        })
        foreach ($output in Read-PublishReceiptRows (Join-Path $receiptDir.FullName 'outputs.tsv') @('Source', 'Sha256', 'Path')) {
            $localPath = $output.Path.Replace('\', '/')
            $path = Join-ScopedAssetPath $scope $localPath
            if (-not (Test-RedistributableAsset $path) -or $path -match '(^|/)contributions/') { continue }
            Assert-FileDigest (Resolve-PayloadPath $StageRoot $path) $output.Sha256
            if (-not $inputs.ContainsKey($localPath)) { throw "No original publish input for '$path'." }
            $input = $inputs[$localPath]
            $coordinate = Get-PublishSourceCoordinate $input.Source $context.PackageRoot
            $libraryKey = @($model[0].Libraries.Keys | Where-Object { ($_ -replace '^runtimepack\.', '') -ieq $coordinate.Package })
            if ($libraryKey.Count -ne 1) { throw "Input package '$($coordinate.Package)' is outside '$depsPath'." }
            if ((-not [string]::IsNullOrEmpty($input.Id) -and $input.Id -ine $coordinate.Package.Split('/')[0]) -or
                (-not [string]::IsNullOrEmpty($input.Version) -and $input.Version -ine $coordinate.Package.Split('/')[1])) {
                throw "Restore metadata disagrees with '$($coordinate.Package)'."
            }
            $transformation = 'copy'
            if ($input.Sha256 -ine $output.Sha256) {
                if ($null -eq $compiler -or -not $r2r.ContainsKey($input.Source)) { throw "Unrecorded publish transformation of '$path'." }
                $mapping = $r2r[$input.Source]
                $mappedOutput = $mapping.Output.Replace('\', '/')
                if ($mappedOutput -notmatch '^(/|[A-Za-z]:/)') {
                    if ($mappedOutput -match '(^|/)(\.|\.\.)(/|$)') { throw 'Unsafe relative ReadyToRun build path.' }
                    $project = $context.Project.Replace('\', '/')
                    $mappedOutput = $project.Substring(0, $project.LastIndexOf('/') + 1) + $mappedOutput
                }
                if ($mapping.Sha256 -ine $input.Sha256 -or $mappedOutput -cne $output.Source.Replace('\', '/') -or
                    $mapping.Path.Replace('\', '/') -cne $localPath) { throw "ReadyToRun input/output mismatch for '$path'." }
                $transformation = 'ready-to-run'
            }
            Add-ScopedAssetRequest $requests ([pscustomobject]@{
                Path = $path; Package = $coordinate.Package; ArchiveEntry = $coordinate.Entry
                InputSha256 = $input.Sha256; OutputSha256 = $output.Sha256; Transformation = $transformation
                Scope = $scope; DependencyManifest = $depsPath; PublishReceipt = $receiptPath
            })
        }
    }
    if ($RequirePublishReceipts -and $receipts.Count -eq 0) { throw 'Publish must capture scoped SDK input/output receipts.' }
    foreach ($model in $Scopes) {
        if ($receiptScopes.Contains($model.Scope)) { continue }
        if ($RequirePublishReceipts -and $model.Scope -notmatch '(^|/)contributions/[A-Za-z0-9_.-]+$') {
            throw "Unregistered dependency scope '$($model.Scope)'."
        }
        foreach ($key in $model.Target.Keys) {
            if (-not $model.Libraries.ContainsKey($key) -or $model.Libraries[$key].type -notin @('package', 'runtimepack') -or $key -like 'SharpClaw.*') { continue }
            $package = $key -replace '^runtimepack\.', ''
            $target = $model.Target[$key]
            foreach ($kind in @('runtime', 'native', 'resources', 'runtimeTargets')) {
                if (-not $target.ContainsKey($kind)) { continue }
                foreach ($entry in $target[$kind].Keys) {
                    if ($kind -eq 'runtimeTargets') { $destination = $entry }
                    elseif ($kind -eq 'resources') { $destination = "$($target[$kind][$entry].locale)/$([IO.Path]::GetFileName($entry))" }
                    else { $destination = [IO.Path]::GetFileName($entry) }
                    $path = Join-ScopedAssetPath $model.Scope $destination
                    if (-not (Test-RedistributableAsset $path) -or -not (Test-Path -LiteralPath (Resolve-PayloadPath $StageRoot $path) -PathType Leaf)) { continue }
                    $hash = (Get-FileHash -LiteralPath (Resolve-PayloadPath $StageRoot $path) -Algorithm SHA256).Hash
                    Add-ScopedAssetRequest $requests ([pscustomobject]@{
                        Path = $path; Package = $package; ArchiveEntry = $entry; InputSha256 = $hash; OutputSha256 = $hash
                        Transformation = 'copy'; Scope = $model.Scope; DependencyManifest = $model.Path; PublishReceipt = ''
                    })
                }
            }
        }
    }
    foreach ($rule in @($Policy.BundledAssets)) {
        $paths = if ($rule.ContainsKey('ContributionPaths')) { @($rule.ContributionPaths) } else { @($rule.ContributionPath) }
        foreach ($contributionPath in $paths) {
            if ($contributionPath -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or
                $rule.Package -notmatch '^[A-Za-z0-9][A-Za-z0-9_.+-]*/[A-Za-z0-9][A-Za-z0-9.+-]*$' -or
                -not $rule.ContainsKey('ArchiveEntry')) { throw 'Invalid exact bundled asset pin.' }
            foreach ($prefix in @('contributions', 'backend/contributions')) {
                $scope = "$prefix/$($contributionPath.Split('/')[0])"
                if (-not (Test-Path -LiteralPath (Resolve-PayloadPath $StageRoot $scope) -PathType Container)) { continue }
                $path = "$prefix/$contributionPath"
                Assert-FileDigest (Resolve-PayloadPath $StageRoot $path) $rule.Sha256
                Add-ScopedAssetRequest $requests ([pscustomobject]@{
                    Path = $path; Package = $rule.Package; ArchiveEntry = $rule.ArchiveEntry
                    InputSha256 = $rule.Sha256; OutputSha256 = $rule.Sha256; Transformation = 'copy'
                    Scope = $scope; DependencyManifest = ''; PublishReceipt = ''
                })
            }
        }
    }
    return [pscustomobject]@{ Assets = @($requests.Values | Sort-Object Path); PublishReceipts = @($receipts | Sort-Object Path) }
}

function Assert-ArchiveAssetDigest {
    param([IO.Compression.ZipArchive]$Archive, [string]$Entry, [string]$Sha256)
    $null = Resolve-PayloadPath ([IO.Path]::GetTempPath()) $Entry
    $matches = @($Archive.Entries | Where-Object FullName -CEQ $Entry)
    if ($matches.Count -ne 1 -or [string]::IsNullOrEmpty($matches[0].Name)) { throw "Missing unique source archive asset '$Entry'." }
    $stream = $matches[0].Open()
    try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
    finally { $stream.Dispose() }
    if ($Sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $hash -ine $Sha256) { throw "Source archive bytes disagree for '$Entry'." }
}

function Assert-ReadyToRunManagedIdentity {
    param([IO.Compression.ZipArchive]$Archive, [string]$Entry, [string]$OutputPath)
    if (-not ('SharpClaw.Build.ManagedPublishIdentity' -as [type])) {
        Add-Type -Path (Join-Path $PSScriptRoot 'ManagedPublishIdentity.cs')
    }
    $source = $Archive.GetEntry($Entry).Open()
    $memory = [IO.MemoryStream]::new()
    $output = [IO.File]::OpenRead($OutputPath)
    try {
        $source.CopyTo($memory)
        $memory.Position = 0
        $inputIdentity = [SharpClaw.Build.ManagedPublishIdentity]::Fingerprint($memory)
        $outputIdentity = [SharpClaw.Build.ManagedPublishIdentity]::Fingerprint($output)
        if ($inputIdentity -cne $outputIdentity) { throw "ReadyToRun substituted managed metadata or IL in '$OutputPath'." }
    } finally { $source.Dispose(); $memory.Dispose(); $output.Dispose() }
}
