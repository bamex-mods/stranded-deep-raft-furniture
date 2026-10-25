param(
    [string]$GameDir = $env:STRANDED_DEEP_GAME_DIR
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($GameDir)) {
    throw 'GameDir is required. Pass -GameDir or set STRANDED_DEEP_GAME_DIR.'
}
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$distFile = Join-Path $root 'dist\distribution.json'
$manifest = Get-Content -LiteralPath $distFile -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.id -ne 'raft-furniture' -or $manifest.version -ne '0.2.16') { throw 'Unexpected distribution manifest.' }
$want = @('BepInEx/plugins/StrandedDeepRaftFurniture', 'BepInEx/plugins/StrandedDeepRaftBuildingBridgePrototype')
foreach ($path in $want) {
    if (@($manifest.packagePaths) -cnotcontains $path -or @($manifest.ownedPaths) -cnotcontains $path) {
        throw "Distribution manifest is missing explicit ownership for $path"
    }
}
if ((@($manifest.ownedPaths) | Where-Object { $_ -eq 'BepInEx/plugins' }).Count -gt 0) {
    throw 'Broad plugin folder ownership is forbidden.'
}

$gameProcesses = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { ($_.ProcessName -replace '[ _-]', '') -ieq 'StrandedDeep' })
if ($gameProcesses.Count -gt 0) { throw 'Quit Stranded Deep before packaging verified runtime binaries.' }

$files = @(
    @{ Name='StrandedDeepRaftFurniture.dll'; Folder='StrandedDeepRaftFurniture'; Expected='EDB9A5760DABBC3D03C5CE723E91C7C817885F54C052BC1EB93BB92DF030524C' },
    @{ Name='StrandedDeepRaftBuildingBridgePrototype.dll'; Folder='StrandedDeepRaftBuildingBridgePrototype'; Expected='79FEAC6DE62FC581F32B0AFB2B2ADA2587BA07AA9D9ABFCCD1BAE89685D00080' }
)
foreach ($entry in $files) {
    $entry['Source'] = Join-Path $GameDir ("BepInEx\plugins\{0}\{1}" -f $entry.Folder, $entry.Name)
    if (-not (Test-Path -LiteralPath $entry.Source -PathType Leaf)) { throw "Installed known-good DLL missing: $($entry.Source)" }
    $actual = (Get-FileHash -LiteralPath $entry.Source -Algorithm SHA256).Hash
    if ($actual -ne $entry.Expected) { throw "Installed runtime DLL differs from audited known-good: $($entry.Name) SHA256=$actual" }
}

$out = Join-Path $root 'dist\out'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$archivePath = Join-Path $out 'StrandedDeepRaftFurniture-0.2.16-local-rc.zip'
if (Test-Path -LiteralPath $archivePath) { throw 'Local RC archive already exists; manually remove the old RC after archiving it before creating a new one.' }

# This is an install-smoke-test ZIP, NOT the canonical Distribution Tool release.
# Two exact audited DLLs only. No saves, configs, diagnostics, or personal files.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$fs = [System.IO.File]::Open($archivePath, [System.IO.FileMode]::CreateNew)
try {
    $zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        $stamp = [DateTimeOffset]::Parse('2020-01-01T00:00:00+00:00')
        foreach ($item in ($files | Sort-Object Folder)) {
            $entryName = 'BepInEx/plugins/' + $item.Folder + '/' + $item.Name
            $entry = $zip.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $stamp
            $srcStream = [System.IO.File]::OpenRead($item.Source)
            try {
                $dstStream = $entry.Open()
                try { $srcStream.CopyTo($dstStream) } finally { $dstStream.Dispose() }
            } finally { $srcStream.Dispose() }
        }
    } finally { $zip.Dispose() }
} finally { $fs.Dispose() }

$sha = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
"$sha  $([IO.Path]::GetFileName($archivePath))" | Set-Content -LiteralPath ($archivePath + '.sha256') -Encoding ASCII
Write-Host 'LOCAL INTEGRATION RC STAGED (NOT PUBLISHED):'
Write-Host $archivePath
Write-Host "SHA256: $sha"
Write-Host 'Package contains exact audited runtime DLLs; game and saves unchanged.'
Write-Host 'Do NOT upload as an official GitHub release. Use canonical Distribution Tool after acceptance.'
