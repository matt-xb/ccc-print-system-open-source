param(
    [string]$Configuration = "Release",
    [ValidateSet("Public", "Company")][string]$Edition = "Public",
    [string]$CompanyConfig,
    [string]$OutputRoot
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

if (-not $OutputRoot) { $OutputRoot = Join-Path $projectRoot "dist\$Edition\0.6.29" }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
& $publishScript -Configuration $Configuration -Edition $Edition -CompanyConfig $CompanyConfig -OutputRoot $OutputRoot
if ($LASTEXITCODE -ne 0) {
    throw "Publishing the applications failed with exit code $LASTEXITCODE."
}

$compilerArgs = @("/DPublishRoot=$OutputRoot")
if ($Edition -eq "Company") { $compilerArgs += "/DCompanyEdition" }
& $compiler @compilerArgs $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Building the client installer failed with exit code $LASTEXITCODE."
}

Write-Output (
    "Installer: " +
    (Join-Path $OutputRoot "installer")
)
