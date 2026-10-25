param(
    [string]$GameDir = $env:STRANDED_DEEP_GAME_DIR,
    [switch]$Deploy
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($GameDir)) {
    throw 'GameDir is required. Pass -GameDir or set STRANDED_DEEP_GAME_DIR.'
}

# Deployment must never bypass the verified backup/rollback procedure.
if ($Deploy) {
    throw 'Direct -Deploy is disabled in v0.1.21. Use .\deploy-safe.ps1 instead.'
}

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Managed = Join-Path $GameDir 'Stranded_Deep_Data\Managed'
$BepInExCore = Join-Path $GameDir 'BepInEx\core'
$Compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$BuildDir = Join-Path $Root 'build'
$OutDll = Join-Path $BuildDir 'StrandedDeepRaftBuildingBridgePrototype.dll'
$Source = Join-Path $Root 'RaftBuildingBridgePrototype.cs'
$DeployDir = Join-Path $GameDir 'BepInEx\plugins\StrandedDeepRaftBuildingBridgePrototype'

foreach ($P in @($Managed,$BepInExCore,$Compiler,$Source)) {
    if (-not (Test-Path -LiteralPath $P)) {
        throw "Required path missing: $P"
    }
}

New-Item -ItemType Directory -Force -Path $BuildDir | Out-Null

$Refs = @(
    (Join-Path $BepInExCore 'BepInEx.dll'),
    (Join-Path $BepInExCore '0Harmony.dll'),
    (Join-Path $Managed 'Assembly-CSharp.dll')
)

$Refs += Get-ChildItem -LiteralPath $Managed -Filter 'UnityEngine*.dll' |
    Sort-Object Name |
    ForEach-Object { $_.FullName }

foreach ($R in $Refs) {
    if (-not (Test-Path -LiteralPath $R)) {
        throw "Reference missing: $R"
    }
}

$Args = @(
    '/nologo',
    '/target:library',
    '/optimize+',
    '/debug-',
    "/out:$OutDll"
)

foreach ($R in $Refs) {
    $Args += "/reference:$R"
}

$Args += $Source

Write-Host "Building Raft Building Bridge Prototype v0.1.21..."
Write-Host "References: $($Refs.Count)"

& $Compiler $Args

if ($LASTEXITCODE -ne 0) {
    throw "csc.exe failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path -LiteralPath $OutDll)) {
    throw "Build output missing: $OutDll"
}

Write-Host "Built: $OutDll"
