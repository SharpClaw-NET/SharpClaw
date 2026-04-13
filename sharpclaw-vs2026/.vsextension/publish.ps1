# Publish script for SharpClaw VS2026 Extension
# This extension is excluded from regular solution builds

param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

Write-Host "Building SharpClaw VS2026 Extension..." -ForegroundColor Cyan
Write-Host "Configuration: $Configuration" -ForegroundColor Gray

# Build with publish flag
dotnet build "$PSScriptRoot\..\SharpClaw.VS2026Extension.csproj" `
    -c $Configuration `
    /p:PublishVSExtension=true

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

$vsixPath = "$PSScriptRoot\..\bin\$Configuration\SharpClaw.VS2026Extension.vsix"

if (Test-Path $vsixPath) {
    Write-Host "`nVSIX package created successfully:" -ForegroundColor Green
    Write-Host $vsixPath -ForegroundColor White
    
    Write-Host "`nTo install:" -ForegroundColor Yellow
    Write-Host "  1. Close all Visual Studio instances" -ForegroundColor Gray
    Write-Host "  2. Double-click the .vsix file" -ForegroundColor Gray
    Write-Host "  3. Restart Visual Studio 2026" -ForegroundColor Gray
} else {
    Write-Error "VSIX package was not created at expected path: $vsixPath"
}
