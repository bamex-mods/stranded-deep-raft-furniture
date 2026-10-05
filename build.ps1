param(
    [switch]$Deploy,
    [string]$GameDir = $env:STRANDED_DEEP_GAME_DIR,
    [string]$Csc = $null
)

$ErrorActionPreference = "Stop"

$ProjectName = "StrandedDeepRaftFurniture"
$SourceFiles = @(
    (Join-Path $PSScriptRoot "RaftFurniture.cs"),
    (Join-Path $PSScriptRoot "SDK\ModSettingsClient.cs")
)
$BuildDir = Join-Path $PSScriptRoot "build"
$OutputDll = Join-Path $BuildDir "$ProjectName.dll"

if ([string]::IsNullOrWhiteSpace($GameDir)) {
    throw "GameDir is required. Pass -GameDir or set STRANDED_DEEP_GAME_DIR."
}

$GameDir = [IO.Path]::GetFullPath($GameDir)
$ManagedDir = Join-Path $GameDir "Stranded_Deep_Data\Managed"
$BepInExCore = Join-Path $GameDir "BepInEx\core"
$PluginDir = Join-Path $GameDir "BepInEx\plugins\StrandedDeepRaftFurniture"

if ([string]::IsNullOrWhiteSpace($Csc)) {
    $Csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
}

$References = @(
    (Join-Path $ManagedDir "Assembly-CSharp.dll"),
    (Join-Path $ManagedDir "Rewired_Core.dll"),
    (Join-Path $ManagedDir "UnityEngine.dll"),
    (Join-Path $ManagedDir "UnityEngine.CoreModule.dll"),
    (Join-Path $ManagedDir "UnityEngine.PhysicsModule.dll"),
    (Join-Path $ManagedDir "UnityEngine.InputLegacyModule.dll"),
    (Join-Path $ManagedDir "bolt.dll"),
    (Join-Path $ManagedDir "bolt.user.dll"),
    (Join-Path $ManagedDir "Sirenix.Serialization.dll"),
    (Join-Path $BepInExCore "BepInEx.dll"),
    (Join-Path $BepInExCore "0Harmony.dll")
)

Write-Host "Game: $GameDir"
Write-Host "Compiler: $Csc"

foreach ($SourceFile in $SourceFiles) {
    if (-not (Test-Path $SourceFile)) {
        throw "Source not found: $SourceFile"
    }
}

if (-not (Test-Path $Csc)) {
    throw "Compiler not found: $Csc"
}

foreach ($Reference in $References) {
    if (-not (Test-Path $Reference)) {
        throw "Reference not found: $Reference"
    }
}

New-Item -ItemType Directory -Force -Path $BuildDir | Out-Null

$Args = @(
    "/nologo",
    "/target:library",
    "/optimize+",
    "/out:$OutputDll"
)

foreach ($Reference in $References) {
    $Args += "/reference:$Reference"
}

$Args += $SourceFiles

Write-Host "Building Raft Furniture v0.2.14..."

& $Csc @Args

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path $OutputDll)) {
    throw "Build succeeded but DLL is missing: $OutputDll"
}

Write-Host "Built: $OutputDll"

if ($Deploy) {
    $SmokerDiagnosticDir = Join-Path $GameDir "BepInEx\plugins\BamEx.RaftSmokerValidatorDiagnostic"
    if (Test-Path $SmokerDiagnosticDir) {
        Remove-Item -LiteralPath $SmokerDiagnosticDir -Recurse -Force
        Write-Host "Removed smoker validator diagnostic: $SmokerDiagnosticDir"
    }

    $PersistenceDiagnosticDir = Join-Path $GameDir "BepInEx\plugins\BamEx.RaftAttachmentPersistenceDiagnostic"
    if (Test-Path $PersistenceDiagnosticDir) {
        Remove-Item -LiteralPath $PersistenceDiagnosticDir -Recurse -Force
        Write-Host "Removed raft attachment persistence diagnostic: $PersistenceDiagnosticDir"
    }

    New-Item -ItemType Directory -Force -Path $PluginDir | Out-Null
    Copy-Item -LiteralPath $OutputDll -Destination (Join-Path $PluginDir "$ProjectName.dll") -Force

    Write-Host "Deployed to: $PluginDir"
    Write-Host ""
    Write-Host "v0.2.14 deployed:"
    Write-Host "  persistent v0.2.0c attachment/follower baseline retained"
    Write-Host "  tables/chairs: vanilla drag + magnetic raft deck plane"
    Write-Host "  Water Still / Light Hook / Camp Fire structure / Container Shelf: shared surface reposition mode"
    Write-Host "  invalid deck aim keeps the last valid raft-local pose instead of freezing in world space"
    Write-Host "  Container Shelf is temporarily isolated from raft compound physics while repositioning"
    Write-Host "  shelf snapping preserves the pre-isolation pivot-to-bottom deck offset"
    Write-Host "  r5 Rewired suppression mirrors Split Map: owning joystick ButtonMaps + raw elementIndex 4 for L1/LB"
    Write-Host "  r5 empty-shelf acquisition adds a small 0.16m SphereCast fallback around the crosshair"
    Write-Host "  split-screen controls: hold L2/LT drag, release drop, L1/LB rotate +15 degrees"
    Write-Host "  r5 STRUCTURE_SMALL drop reuses the proven post-load deck-pose repair before follower capture"
    Write-Host "  Mod Settings soft integration: five toggles + emergency recovery button under Settings -> MODS"
    Write-Host "  toggles: Water Still / Camp Fire+Smoker / Light Hook / Container Shelf reposition / manual reposition"
    Write-Host "  action: Emergency upright levels the nearest raft"
    Write-Host "  existing placed objects remain attached and persistent when a toggle is disabled"
    Write-Host "  client config remains source of truth; menu host is optional"
    Write-Host "  attachment persistence: BepInEx\config\StrandedDeepRaftFurniture\Attachments.json"
    Write-Host "  attachment log: BepInEx\config\StrandedDeepRaftFurniture\AttachmentRuntime.log"
    Write-Host "  reposition log: BepInEx\config\StrandedDeepRaftFurniture\RepositionRuntime.log"
    Write-Host "  explicit furniture DROP_OFF_RAFT semantics retained"
    Write-Host "  Container Shelf keeps its native crate/socket components; only root pose is moved"
    Write-Host "  v0.2.12: Water Still + STRUCTURE_SMALL reposition temporarily isolate all target-vs-owning-raft collider pairs"
    Write-Host "  v0.2.12: non-raft-body pair states are restored exactly; dynamic raft-body pairs remain ignored after drop"
    Write-Host "  v0.2.13: solid Water Still / STRUCTURE_SMALL colliders are disabled only while custom reposition is active"
    Write-Host "  v0.2.13: trigger colliders remain enabled; exact solid-collider enabled states are restored on confirm/cancel"
}
