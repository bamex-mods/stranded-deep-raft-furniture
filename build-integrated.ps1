param(
    [string]$GameDir = $env:STRANDED_DEEP_GAME_DIR
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($GameDir)) {
    throw 'GameDir is required. Pass -GameDir or set STRANDED_DEEP_GAME_DIR.'
}
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$gameProcesses = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { ($_.ProcessName -replace '[ _-]', '') -ieq 'StrandedDeep' })
if ($gameProcesses.Count -gt 0) {
    throw 'Close Stranded Deep before compiling integrated components.'
}

$furnitureBuild = Join-Path $root 'build.ps1'
$bridgeBuild = Join-Path $root 'BuildingBridge\build.ps1'
foreach ($p in @($furnitureBuild, $bridgeBuild)) {
    if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { throw "Build script missing: $p" }
}

Write-Host 'Compiling Raft Furniture v0.2.15 (no deploy)...'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $furnitureBuild -GameDir $GameDir
if ($LASTEXITCODE -ne 0) { throw 'Furniture build failed.' }

Write-Host 'Compiling Building Bridge v0.1.21 (no deploy)...'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $bridgeBuild -GameDir $GameDir
if ($LASTEXITCODE -ne 0) { throw 'Building Bridge build failed.' }

$parts = @(
    (Join-Path $root 'build\StrandedDeepRaftFurniture.dll'),
    (Join-Path $root 'BuildingBridge\build\StrandedDeepRaftBuildingBridgePrototype.dll')
)
foreach ($dll in $parts) {
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw "Build output missing: $dll" }
    Write-Host "Built: $dll"
    Write-Host "SHA256: $((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash)"
}
Write-Host 'BOTH BUILDS FINISHED. No DLLs deployed. No save data touched.'
