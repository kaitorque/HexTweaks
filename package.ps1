# Builds the plugin and zips the package folder into an r2modman-importable zip next to this repo.
$ErrorActionPreference = "Stop"
$name = "HexTweaks"
$dir = $PSScriptRoot

dotnet build (Join-Path $dir "$name.csproj") -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$package = Join-Path $dir "package"
Copy-Item (Join-Path $dir "bin\Release\netstandard2.1\$name.dll") $package -Force

$version = (Get-Content (Join-Path $package "manifest.json") -Raw | ConvertFrom-Json).version_number
$zip = Join-Path (Split-Path $dir -Parent) "Kaitorque-$name-$version.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $package "*") -DestinationPath $zip
Write-Host "Packaged $zip"
