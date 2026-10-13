param(
    [ValidateSet("Public", "Company")][string]$Edition = "Public",
    [string]$CompanyConfig,
    [string]$OutputRoot
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputRoot) { $OutputRoot = Join-Path $root "dist\$Edition\0.6.29" }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
& (Join-Path $PSScriptRoot "build-client-installer.ps1") -Edition $Edition -CompanyConfig $CompanyConfig -OutputRoot $OutputRoot
$suffix = if ($Edition -eq "Public") { "公开版" } else { "" }
foreach ($component in @("admin", "client")) {
    $output = Join-Path $OutputRoot $component
    $files = @((Join-Path $output "*$suffix.exe"), (Join-Path $output "appsettings.json"), (Join-Path $output "LICENSE"), (Join-Path $output "README.md"), (Join-Path $output "licenses"))
    if ($component -eq "client") { $files += Join-Path $output "x64" }
    Compress-Archive -Path $files -DestinationPath (Join-Path $OutputRoot "ccc-$component-v0.6.29-$Edition-windows-x64.zip") -CompressionLevel Optimal
}
