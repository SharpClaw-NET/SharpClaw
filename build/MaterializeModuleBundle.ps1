param(
    [Parameter(Mandatory = $true)]
    [string] $Feed,

    [Parameter(Mandatory = $true)]
    [string] $Output
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$feedPath = [System.IO.Path]::GetFullPath($Feed)
$outputPath = [System.IO.Path]::GetFullPath($Output)
$modulesPath = Join-Path $outputPath "modules"

if (-not (Test-Path -LiteralPath $feedPath -PathType Container))
{
    throw "The frozen feed does not exist."
}

if (Test-Path -LiteralPath $outputPath)
{
    throw "The module bundle output must not exist."
}

New-Item -ItemType Directory -Path $modulesPath | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

$packages = @{}
foreach ($archive in Get-ChildItem -LiteralPath $feedPath -Filter "*.nupkg" -File)
{
    if ($archive.Name.EndsWith(".snupkg", [System.StringComparison]::OrdinalIgnoreCase))
    {
        continue
    }

    $zip = [System.IO.Compression.ZipFile]::OpenRead($archive.FullName)
    try
    {
        $nuspecEntries = @(
            $zip.Entries |
                Where-Object { $_.FullName.EndsWith(".nuspec", [System.StringComparison]::OrdinalIgnoreCase) }
        )
        if ($nuspecEntries.Count -ne 1)
        {
            throw "Package '$($archive.Name)' must contain one nuspec."
        }

        $nuspecEntry = $nuspecEntries[0]
        $reader = [System.IO.StreamReader]::new($nuspecEntry.Open())
        try
        {
            [xml] $nuspec = $reader.ReadToEnd()
        }
        finally
        {
            $reader.Dispose()
        }

        $id = [string] $nuspec.package.metadata.id
        $version = [string] $nuspec.package.metadata.version
        if ($packages.ContainsKey($id))
        {
            throw "The frozen feed contains duplicate package id '$id'."
        }

        $dependencyNodes = @(
            $nuspec.SelectNodes(
                "//*[local-name()='dependencies']//*[local-name()='dependency']"))
        $dependencies = @(
            $dependencyNodes |
                ForEach-Object {
                    [pscustomobject]@{
                        Id = [string] $_.id
                        Version = [string] $_.version
                    }
                }
        )
        $packages[$id] = [pscustomobject]@{
            Id = $id
            Version = $version
            Path = $archive.FullName
            Dependencies = $dependencies
        }
    }
    finally
    {
        $zip.Dispose()
    }
}

function Copy-ZipEntry
{
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.Compression.ZipArchiveEntry] $Entry,

        [Parameter(Mandatory = $true)]
        [string] $Destination
    )

    $parent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    if (Test-Path -LiteralPath $Destination)
    {
        $existing = (Get-FileHash -Algorithm SHA256 -LiteralPath $Destination).Hash
        $entryStream = $Entry.Open()
        try
        {
            $incoming = [System.Convert]::ToHexString(
                [System.Security.Cryptography.SHA256]::HashData($entryStream))
        }
        finally
        {
            $entryStream.Dispose()
        }

        if ($existing -ne $incoming)
        {
            throw "Module bundle payload collision at '$Destination'."
        }

        return
    }

    [System.IO.Compression.ZipFileExtensions]::ExtractToFile(
        $Entry,
        $Destination,
        $false)
}

function Copy-OwnedDependencyAssemblies
{
    param(
        [Parameter(Mandatory = $true)]
        [object] $Package,

        [Parameter(Mandatory = $true)]
        [string] $Destination,

        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.HashSet[string]] $Visited
    )

    foreach ($dependency in $Package.Dependencies)
    {
        if (-not $dependency.Id.StartsWith("SharpClaw.", [System.StringComparison]::Ordinal))
        {
            continue
        }

        if (-not $packages.ContainsKey($dependency.Id))
        {
            throw "The frozen feed does not contain dependency '$($dependency.Id)'."
        }

        if (-not $Visited.Add($dependency.Id))
        {
            continue
        }

        $owned = $packages[$dependency.Id]
        $zip = [System.IO.Compression.ZipFile]::OpenRead($owned.Path)
        try
        {
            foreach ($entry in $zip.Entries |
                         Where-Object { $_.FullName -like "lib/net10.0/*.dll" })
            {
                Copy-ZipEntry -Entry $entry -Destination (Join-Path $Destination $entry.Name)
            }
        }
        finally
        {
            $zip.Dispose()
        }

        Copy-OwnedDependencyAssemblies -Package $owned -Destination $Destination -Visited $Visited
    }
}

$moduleIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($package in $packages.Values | Sort-Object Id)
{
    $zip = [System.IO.Compression.ZipFile]::OpenRead($package.Path)
    try
    {
        $manifests = @(
            $zip.Entries |
                Where-Object {
                    $_.Name -eq "module.json" -and
                    ($_.FullName -eq "sharpclaw/module.json" -or
                     $_.FullName -like "contentFiles/any/net10.0/modules/*/module.json")
                }
        )

        foreach ($manifestEntry in $manifests)
        {
            $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
            try
            {
                $manifest = $reader.ReadToEnd() | ConvertFrom-Json
            }
            finally
            {
                $reader.Dispose()
            }

            $moduleId = [string] $manifest.id
            if ([string]::IsNullOrWhiteSpace($moduleId) -or -not $moduleIds.Add($moduleId))
            {
                throw "The module bundle contains an invalid or duplicate module id."
            }

            $prefix = $manifestEntry.FullName.Substring(
                0,
                $manifestEntry.FullName.Length - "module.json".Length)
            $destination = Join-Path $modulesPath $moduleId
            foreach ($entry in $zip.Entries |
                         Where-Object {
                             -not [string]::IsNullOrEmpty($_.Name) -and
                             $_.FullName.StartsWith($prefix, [System.StringComparison]::Ordinal)
                         })
            {
                $relative = $entry.FullName.Substring($prefix.Length).Replace('/', '\')
                Copy-ZipEntry -Entry $entry -Destination (Join-Path $destination $relative)
            }

            $visited = [System.Collections.Generic.HashSet[string]]::new(
                [System.StringComparer]::OrdinalIgnoreCase)
            $null = $visited.Add($package.Id)
            Copy-OwnedDependencyAssemblies -Package $package -Destination $destination -Visited $visited
        }
    }
    finally
    {
        $zip.Dispose()
    }
}

if ($moduleIds.Count -eq 0)
{
    throw "The frozen feed does not contain module manifests."
}

$manifest = [pscustomobject]@{
    ModuleCount = $moduleIds.Count
    Modules = @(
        $moduleIds |
            Sort-Object |
            ForEach-Object {
                $modulePath = Join-Path $modulesPath $_
                [pscustomobject]@{
                    Id = $_
                    Files = @(
                        Get-ChildItem -LiteralPath $modulePath -Recurse -File |
                            Sort-Object FullName |
                            ForEach-Object {
                                [pscustomobject]@{
                                    Path = [System.IO.Path]::GetRelativePath($modulePath, $_.FullName)
                                    Length = $_.Length
                                    Sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
                                }
                            }
                    )
                }
            }
    )
}
$manifest | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath (Join-Path $outputPath "module-bundle-manifest.json") -Encoding utf8

Write-Host "Module bundle count: $($moduleIds.Count)"
