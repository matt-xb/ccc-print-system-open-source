param(
    [string]$Configuration = "Release",
    [ValidateSet("Public", "Company")][string]$Edition = "Public",
    [string]$CompanyConfig,
    [string]$OutputRoot
)
$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputRoot) { $OutputRoot = Join-Path $projectRoot "dist\$Edition\0.6.29" }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) { throw "Output already exists: $OutputRoot" }
if ($Edition -eq "Company") {
    if (-not $CompanyConfig) { throw "Company builds require -CompanyConfig." }
    foreach ($component in @("admin", "client")) {
        $file = Join-Path $CompanyConfig "$component.json"
        if (-not (Test-Path -LiteralPath $file)) { throw "Missing company configuration: $component" }
        $null = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    }
} elseif ($CompanyConfig) { throw "Public builds cannot use CompanyConfig." }
foreach ($component in @("admin", "client")) {
    $project = if ($component -eq "admin") { "App" } else { "Client" }
    $output = Join-Path $OutputRoot $component
    dotnet publish (Join-Path $projectRoot "src\CccPrint.$project\CccPrint.$project.csproj") --configuration $Configuration --runtime win-x64 --self-contained true "-p:Edition=$Edition" --output $output
    if ($LASTEXITCODE -ne 0) { throw "Publishing $component failed: $LASTEXITCODE" }
    if ($Edition -eq "Company") {
        Copy-Item -LiteralPath (Join-Path $CompanyConfig "$component.json") -Destination (Join-Path $output "appsettings.json")
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot "LICENSE"), (Join-Path $projectRoot "README.md") -Destination $output
    Copy-Item -LiteralPath (Join-Path $projectRoot "licenses") -Destination $output -Recurse
}
Write-Output "Published $Edition to $OutputRoot"
