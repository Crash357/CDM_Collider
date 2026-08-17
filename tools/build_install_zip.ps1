# Build cdm_collider_vX.Y.Z.zip for Blender Extensions (Install from Disk).
# Excludes samples/ and C# sources. Ships published GeoEngine CLI under geo_engine_cs/cli/.
# Ships published GeoEngine CLI under geo_engine_cs/cli/.
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot

$manifestPath = Join-Path $Root "blender_manifest.toml"
if (-not (Test-Path $manifestPath)) { throw "blender_manifest.toml not found" }
$version = (Select-String -Path $manifestPath -Pattern '^\s*version\s*=\s*"([^"]+)"' | ForEach-Object { $_.Matches[0].Groups[1].Value })
if (-not $version) { throw "Could not parse version from blender_manifest.toml" }

$distDir = Join-Path $Root "dist"
$staging = Join-Path $distDir "staging\cdm_collider"
$cliTmp = Join-Path $distDir "_cli_publish_tmp"
$zipName = "cdm_collider_v$version.zip"
$zipPath = Join-Path $distDir $zipName
$cliProj = Join-Path $Root "geo_engine_cs\src\Cdm.GeoEngine.Cli\Cdm.GeoEngine.Cli.csproj"

Write-Host "Version: $version"
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

if (Test-Path $cliTmp) { Remove-Item $cliTmp -Recurse -Force }
Write-Host "Publishing C# GeoEngine (Release) -> dist/_cli_publish_tmp ..."
dotnet publish $cliProj -c Release -o $cliTmp --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
$cliDll = Join-Path $cliTmp "Cdm.GeoEngine.Cli.dll"
if (-not (Test-Path $cliDll)) { throw "Published CLI dll missing: $cliDll" }

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null

$robocopyArgs = @(
    $Root, $staging, "/E",
    "/XD", ".git", ".cursor", "__pycache__", "build", "dist", "Test", "tools",
    "samples", "geo_engine_cs",
    "agent-transcripts", "terminals", ".vscode",
    "/XF", "*.zip",
    "/NFL", "/NDL", "/NJH", "/NJS", "/nc", "/ns", "/np"
)
robocopy @robocopyArgs | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit $LASTEXITCODE" }

if (Test-Path (Join-Path $staging "samples")) {
    throw "samples/ leaked into staging — ZIP abort"
}

$cliDest = Join-Path $staging "geo_engine_cs\cli"
New-Item -ItemType Directory -Path (Split-Path $cliDest) -Force | Out-Null
Move-Item $cliTmp $cliDest
if (-not (Test-Path (Join-Path $cliDest "Cdm.GeoEngine.Cli.dll"))) {
    throw "CLI dll missing in staging: $cliDest"
}
Write-Host "Bundled: geo_engine_cs/cli (Release publish, no sources)"

Get-ChildItem $staging -Recurse -Filter "__pycache__" -Directory -ErrorAction SilentlyContinue |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

if (-not (Test-Path (Join-Path $staging "TestVHACD.exe"))) {
    Write-Warning "TestVHACD.exe fehlt im Staging"
} else {
    Write-Host "Bundled: TestVHACD.exe"
}
$whl = Get-ChildItem (Join-Path $staging "bundled") -Filter "coacd-*.whl" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($whl) { Write-Host "Bundled: $($whl.Name)" } else { Write-Warning "CoACD wheel fehlt" }

if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Compress-Archive -Path $staging -DestinationPath $zipPath -CompressionLevel Optimal

$sizeMb = (Get-Item $zipPath).Length / 1MB
Write-Host ""
Write-Host "Fertig: $zipPath"
Write-Host "Groesse: $([math]::Round($sizeMb, 2)) MB"
Write-Host "Blender: Edit -> Preferences -> Get Extensions -> Install from Disk"
