param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$publishScript = Join-Path $PSScriptRoot "publish.ps1"
$installerScript = Join-Path $projectRoot "installer\client.iss"
$compilerCandidates = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 7\ISCC.exe"),
    "C:\Program Files\Inno Setup 7\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 7\ISCC.exe"
)
$compiler = $compilerCandidates |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1

if (-not $compiler) {
    throw "Inno Setup 7 was not found. Install JRSoftware.InnoSetup.7 with winget."
}

& powershell.exe `
    -NoProfile `
    -ExecutionPolicy Bypass `
    -File $publishScript `
    -Configuration $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "Publishing the applications failed with exit code $LASTEXITCODE."
}

& $compiler $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Building the client installer failed with exit code $LASTEXITCODE."
}

Write-Output (
    "Installer: " +
    (Join-Path $projectRoot "dist\0.6.29\installer")
)
