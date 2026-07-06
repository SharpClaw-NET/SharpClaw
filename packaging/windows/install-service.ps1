<#
.SYNOPSIS
    Installs SharpClaw API (and optionally Gateway) as Windows Services.

.DESCRIPTION
    Registers the staged API and Gateway executables produced by
    `scripts\publish.ps1 -Include Server` (or -Include Runtime) as Windows Services
    using sc.exe. Run from an elevated PowerShell prompt.

.PARAMETER InstallDir
    Directory containing the staged 'api\' (and optionally 'gateway\')
    folder. Defaults to the script's parent directory.

.PARAMETER IncludeGateway
    Also register the Gateway service. Required when installing a Server
    profile build; omit for Core builds.

.PARAMETER ApiServiceName
    Name for the API service. Default: SharpClawApi.

.PARAMETER GatewayServiceName
    Name for the Gateway service. Default: SharpClawGateway.

.EXAMPLE
    .\install-service.ps1 -InstallDir 'C:\Program Files\SharpClaw' -IncludeGateway
#>
param(
    [string]$InstallDir          = $PSScriptRoot,
    [switch]$IncludeGateway,
    [string]$ApiServiceName      = "SharpClawApi",
    [string]$GatewayServiceName  = "SharpClawGateway"
)

$ErrorActionPreference = "Stop"

function Assert-Elevated {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p  = [Security.Principal.WindowsPrincipal]::new($id)
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Error "This script must be run from an elevated PowerShell prompt."
        exit 1
    }
}

function Register-Service {
    param([string]$Name, [string]$BinPath, [string]$Description)
    if (Get-Service -Name $Name -ErrorAction SilentlyContinue) {
        Write-Host "Service '$Name' already exists. Stopping and removing..." -ForegroundColor Yellow
        sc.exe stop $Name | Out-Null
        Start-Sleep -Seconds 1
        sc.exe delete $Name | Out-Null
        Start-Sleep -Seconds 1
    }
    & sc.exe create $Name binPath= "`"$BinPath`"" start= auto DisplayName= $Name | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Error "sc create failed for $Name (exit $LASTEXITCODE)"; exit 1 }
    & sc.exe description $Name $Description | Out-Null
    & sc.exe failure $Name reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
    Write-Host "[OK] Registered $Name" -ForegroundColor Green
}

Assert-Elevated

$apiExe = Join-Path $InstallDir "api\SharpClaw.Runtime.Host.exe"
if (-not (Test-Path $apiExe)) {
    Write-Error "API executable not found at $apiExe"
    exit 1
}
Register-Service $ApiServiceName $apiExe "SharpClaw API service"

if ($IncludeGateway) {
    $gwExe = Join-Path $InstallDir "gateway\SharpClaw.Gateway.exe"
    if (-not (Test-Path $gwExe)) {
        Write-Error "Gateway executable not found at $gwExe"
        exit 1
    }
    Register-Service $GatewayServiceName $gwExe "SharpClaw public Gateway service"
}

Write-Host ""
Write-Host "Start with: Start-Service $ApiServiceName" -ForegroundColor Cyan
if ($IncludeGateway) {
    Write-Host "            Start-Service $GatewayServiceName" -ForegroundColor Cyan
}
