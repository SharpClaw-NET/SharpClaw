[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$OutputPath = "artifacts\nuget-modules",
    [string]$ApiKeyPath,
    [string]$Source = "https://api.nuget.org/v3/index.json",
    [switch]$SkipPush,
    [switch]$ForcePush
)

$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$output = Join-Path $root $OutputPath
New-Item -ItemType Directory -Force -Path $output | Out-Null
Get-ChildItem -Path $output -Filter "SharpClaw.Modules*.nupkg" |
    Remove-Item -Force

$projects = Get-ChildItem -Path (Join-Path $root "DefaultModules") `
    -Recurse `
    -Filter "SharpClaw.Modules*.csproj" |
    Sort-Object FullName

if ($projects.Count -eq 0) {
    throw "No SharpClaw module projects were found under DefaultModules."
}

foreach ($project in $projects) {
    dotnet pack $project.FullName `
        --configuration $Configuration `
        --no-restore `
        --output $output
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$packages = Get-ChildItem -Path $output -Filter "SharpClaw.Modules*.nupkg" |
    Sort-Object FullName

function Get-PackageMetadata {
    param([System.IO.FileInfo]$Package)

    $zip = [System.IO.Compression.ZipFile]::OpenRead($Package.FullName)
    try {
        $nuspecEntry = $zip.Entries |
            Where-Object { $_.FullName.EndsWith(".nuspec", [System.StringComparison]::OrdinalIgnoreCase) } |
            Select-Object -First 1
        if ($null -eq $nuspecEntry) {
            throw "Package '$($Package.Name)' does not contain a nuspec."
        }

        $reader = [System.IO.StreamReader]::new($nuspecEntry.Open())
        try {
            [xml]$nuspec = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        [pscustomobject]@{
            Id = [string]$nuspec.package.metadata.id
            Version = [string]$nuspec.package.metadata.version
        }
    }
    finally {
        $zip.Dispose()
    }
}

function Resolve-FlatContainerSource {
    param([string]$PackageSource)

    if ($PackageSource -eq "https://api.nuget.org/v3-flatcontainer/") {
        return $PackageSource
    }

    if ($PackageSource.EndsWith("/v3-flatcontainer", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "$PackageSource/"
    }

    if ($PackageSource.EndsWith("/v3-flatcontainer/", [System.StringComparison]::OrdinalIgnoreCase)) {
        return $PackageSource
    }

    if ($PackageSource.EndsWith("/v3/index.json", [System.StringComparison]::OrdinalIgnoreCase)) {
        try {
            $serviceIndex = Invoke-RestMethod -Uri $PackageSource -Method Get
            foreach ($resource in $serviceIndex.resources) {
                if ([string]::Equals(
                        [string]$resource.'@type',
                        "PackageBaseAddress/3.0.0",
                        [System.StringComparison]::OrdinalIgnoreCase)) {
                    return [string]$resource.'@id'
                }
            }
        }
        catch {
            throw "Could not read NuGet service index '$PackageSource': $($_.Exception.Message)"
        }
    }

    return $null
}

function Test-PackageVersionExists {
    param(
        [string]$PackageId,
        [string]$PackageVersion,
        [string]$PackageSource
    )

    $flatContainer = Resolve-FlatContainerSource $PackageSource
    if ($null -eq $flatContainer) {
        return $false
    }

    $base = $flatContainer.TrimEnd("/")
    $id = $PackageId.ToLowerInvariant()
    $indexUri = "$base/$id/index.json"

    try {
        $response = Invoke-RestMethod -Uri $indexUri -Method Get
    }
    catch {
        $statusCode = $null
        if ($_.Exception.Response) {
            try {
                $statusCode = [int]$_.Exception.Response.StatusCode
            }
            catch {
                $statusCode = $null
            }
        }

        if ($statusCode -eq 404) {
            return $false
        }

        throw
    }

    foreach ($version in $response.versions) {
        if ([string]::Equals(
                [string]$version,
                $PackageVersion,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

$validatedPackages = @()
foreach ($package in $packages) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $manifestEntry = $zip.GetEntry("sharpclaw/module.json")
        if ($null -eq $manifestEntry) {
            throw "Package '$($package.Name)' does not contain sharpclaw/module.json."
        }

        $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
        try {
            $manifest = $reader.ReadToEnd() | ConvertFrom-Json
        }
        finally {
            $reader.Dispose()
        }

        if ([string]::IsNullOrWhiteSpace($manifest.entryAssembly)) {
            throw "Package '$($package.Name)' has no entryAssembly in sharpclaw/module.json."
        }

        $entryAssembly = "sharpclaw/$($manifest.entryAssembly)"
        if ($null -eq $zip.GetEntry($entryAssembly)) {
            throw "Package '$($package.Name)' does not contain $entryAssembly."
        }
    }
    finally {
        $zip.Dispose()
    }

    $metadata = Get-PackageMetadata $package
    $validatedPackages += [pscustomobject]@{
        Package = $package
        Id = $metadata.Id
        Version = $metadata.Version
    }
}

if ($SkipPush) {
    Write-Host "Packed and validated $($validatedPackages.Count) module package(s) in $output."
    return
}

if ([string]::IsNullOrWhiteSpace($ApiKeyPath)) {
    throw "ApiKeyPath is required unless SkipPush is set."
}

$apiKey = (Get-Content -Raw -LiteralPath $ApiKeyPath).Trim()
if ([string]::IsNullOrWhiteSpace($apiKey)) {
    throw "NuGet API key file is empty."
}

foreach ($item in $validatedPackages) {
    if (!$ForcePush -and (Test-PackageVersionExists $item.Id $item.Version $Source)) {
        Write-Host "Skipping $($item.Id) $($item.Version); that package version already exists on $Source."
        continue
    }

    dotnet nuget push $item.Package.FullName `
        --api-key $apiKey `
        --source $Source `
        --skip-duplicate
}
