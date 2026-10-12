$ErrorActionPreference = "Stop"
& (Join-Path $PSScriptRoot "build-client-installer.ps1")
$root = Split-Path -Parent $PSScriptRoot
$versionRoot = Join-Path $root "dist\0.6.29"
foreach ($component in @("admin", "client")) {
    $output = Join-Path $versionRoot $component
    $files = @(
        (Join-Path $output "*公开版.exe"),
        (Join-Path $output "appsettings.json"),
        (Join-Path $output "LICENSE"),
        (Join-Path $output "README.md"),
        (Join-Path $output "licenses")
    )
    if ($component -eq "client") { $files += Join-Path $output "x64" }
    Compress-Archive -Path $files -DestinationPath (Join-Path $versionRoot "ccc-$component-v0.6.29-public-windows-x64.zip") -CompressionLevel Optimal
}
