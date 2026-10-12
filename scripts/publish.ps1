param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$adminVersionRoot = Join-Path $projectRoot "dist\0.6.29"
$clientVersionRoot = Join-Path $projectRoot "dist\0.6.29"

dotnet publish `
    (Join-Path $projectRoot "src\CccPrint.App\CccPrint.App.csproj") `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --output (Join-Path $adminVersionRoot "admin")
if ($LASTEXITCODE -ne 0) {
    throw "Publishing the admin application failed with exit code $LASTEXITCODE."
}

dotnet publish `
    (Join-Path $projectRoot "src\CccPrint.Client\CccPrint.Client.csproj") `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --output (Join-Path $clientVersionRoot "client")
if ($LASTEXITCODE -ne 0) {
    throw "Publishing the client application failed with exit code $LASTEXITCODE."
}

Write-Output "Published admin: $adminVersionRoot"
Write-Output "Published client: $clientVersionRoot"
