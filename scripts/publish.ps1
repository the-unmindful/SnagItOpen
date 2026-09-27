<#
.SYNOPSIS
  Builds, tests and publishes a self-contained portable SnagItOpen package for win-x64.
.DESCRIPTION
  Output: artifacts\win-x64 (folder), artifacts\SnagItOpen-<version>-win-x64.zip and a .sha256 file.
  Use -SkipTests to publish without running the test suites.
#>
param(
    [string]$Runtime = "win-x64",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# Prefer a per-user SDK install when present.
$userDotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet"
if (Test-Path (Join-Path $userDotnet "dotnet.exe")) { $env:PATH = "$userDotnet;$env:PATH" }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$version = $props.Project.PropertyGroup.Version
if (-not $version) { $version = "0.0.0" }

Write-Host "Building SnagItOpen $version ($Runtime)"
dotnet build .\SnagItOpen.slnx -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

if (-not $SkipTests) {
    dotnet test .\tests\SnagItOpen.Core.Tests\SnagItOpen.Core.Tests.csproj -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw "Core tests failed." }
    dotnet test .\tests\SnagItOpen.Windows.Tests\SnagItOpen.Windows.Tests.csproj -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw "Windows tests failed." }
}

$out = Join-Path $root "artifacts\$Runtime"
if (Test-Path $out) { Remove-Item -LiteralPath $out -Recurse -Force }
dotnet publish .\src\SnagItOpen.App\SnagItOpen.App.csproj -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=false -p:PublishTrimmed=false -o $out
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

Copy-Item -LiteralPath (Join-Path $root "README.md") -Destination $out
$notices = Join-Path $root "THIRD-PARTY-NOTICES.md"
if (Test-Path $notices) { Copy-Item -LiteralPath $notices -Destination $out }

$zip = Join-Path $root "artifacts\SnagItOpen-$version-$Runtime.zip"
if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$zip.sha256" -Value "$hash  $(Split-Path -Leaf $zip)" -Encoding ascii

Write-Host "Package: $zip"
Write-Host "SHA-256: $hash"
