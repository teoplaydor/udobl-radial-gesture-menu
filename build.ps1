<#
  Builds Udobl into a single portable .exe and drops it in .\dist
  Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
#>
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$proj = Join-Path $root "Udobl.csproj"
$cfg  = "Release"

Write-Host "==> Building Udobl ($cfg)..." -ForegroundColor Cyan
dotnet build $proj -c $cfg -v minimal --nologo
if ($LASTEXITCODE -ne 0) { Write-Host "Build FAILED." -ForegroundColor Red; exit 1 }

$out = Join-Path $root "bin\$cfg\net48\Udobl.exe"
if (-not (Test-Path $out)) {
  # AppendTargetFrameworkToOutputPath could differ; search for it.
  $found = Get-ChildItem (Join-Path $root "bin\$cfg") -Recurse -Filter "Udobl.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($found) { $out = $found.FullName }
}
if (-not (Test-Path $out)) { Write-Host "Could not locate Udobl.exe" -ForegroundColor Red; exit 1 }

$dist = Join-Path $root "dist"
$srcDir = Split-Path $out
New-Item -ItemType Directory -Force -Path $dist | Out-Null

# Refresh the runtime files (keep any user config.json/usage.json/udobl.portable in dist).
Remove-Item (Join-Path $dist "Udobl.exe"), (Join-Path $dist "Udobl.exe.config") -Force -ErrorAction SilentlyContinue
Get-ChildItem $dist -Filter "*.dll" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

Copy-Item $out (Join-Path $dist "Udobl.exe") -Force
$cfgFile = "$out.config"
if (Test-Path $cfgFile) { Copy-Item $cfgFile (Join-Path $dist "Udobl.exe.config") -Force }
# Direct3D (SharpDX) managed DLLs must ship next to the exe.
Get-ChildItem $srcDir -Filter "SharpDX*.dll" | ForEach-Object { Copy-Item $_.FullName $dist -Force }

$size = [math]::Round((Get-Item (Join-Path $dist "Udobl.exe")).Length / 1KB, 0)
$dllCount = (Get-ChildItem $dist -Filter "SharpDX*.dll").Count
Write-Host ""
Write-Host "==> Done. Run: $(Join-Path $dist 'Udobl.exe')  (exe $size KB + $dllCount SharpDX dll)" -ForegroundColor Green
Write-Host "    Keep the SharpDX*.dll next to the exe. Config: %APPDATA%\Udobl (or portable via a udobl.portable marker)." -ForegroundColor DarkGray
