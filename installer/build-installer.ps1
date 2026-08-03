# Builds the Multi-SSH Windows installer.
# 1) Publishes a self-contained single-file exe (no .NET needed on the target).
# 2) Compiles installer\Multi-SSH.iss with Inno Setup into installer\Output.
#
# Usage:  pwsh installer\build-installer.ps1
# Requires: .NET SDK and Inno Setup 6 (ISCC.exe).

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot   # repo root
Push-Location $root
try {
    Write-Host "Publishing self-contained exe..." -ForegroundColor Cyan
    dotnet publish -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true | Out-Host

    $iscc = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw "Inno Setup (ISCC.exe) not found. Install: winget install JRSoftware.InnoSetup" }

    Write-Host "Compiling installer with $iscc ..." -ForegroundColor Cyan
    & $iscc "installer\Multi-SSH.iss" | Out-Host
    Write-Host "Installer written to installer\Output" -ForegroundColor Green
}
finally {
    Pop-Location
}
