# Stranded Deep Raft Furniture

Stranded Deep Raft Furniture turns the raft into a practical mobile base by allowing selected furniture and survival structures to be placed or repositioned on deck while preserving raft movement, physics, local split-screen controls, and persistence.

Current stable version: `0.2.13`

## 0.2.13 physics-safety update

`0.2.13` hardens custom repositioning for the two physics-sensitive families: `Water Still` and `STRUCTURE_SMALL` (`Camp Fire` / `Fire Pit` / `Smoker`). During active reposition, the target is fully isolated from its owning raft and its solid colliders are temporarily disabled so moving kinematic geometry cannot impart unwanted impulses to the raft or local players. Trigger colliders remain enabled. Exact collider states are restored on confirm/cancel, and deck height is preserved from the pre-isolation geometry.


## Features

- Supports raft use for tables and chairs.
- Supports Water Still placement and deck repositioning.
- Supports Light Hook placement and deck repositioning.
- Supports the Camp Fire -> Fire Pit -> Smoker construction chain on the raft.
- Keeps food attached to raft cooking stations while the raft moves and after load-time repair.
- Adds free repositioning for the native Container Shelf without replacing its native crate sockets.
- Supports local split-screen player-specific gamepad input.
- Uses a persistent raft-local attachment model for supported furniture.
- Includes safe migration/recovery behavior for older placed raft furniture.
- Provides an emergency raft-upright action through optional Mod Settings integration.

## Controls

For supported reposition operations:

- Hold `L2 / LT` to drag.
- Release `L2 / LT` to drop.
- Press `L1 / LB` to rotate by 15 degrees.

Input handling is resolved for the owning local player rather than treating the split-screen session as a single camera/input context.

## Settings

The mod owns its own BepInEx configuration. If Stranded Deep Mod Settings is installed, the same settings are exposed under `Settings -> MODS`.

User-facing settings:

- Water Still on raft: enabled by default.
- Camp Fire / Fire Pit / Smoker on raft: enabled by default.
- Light Hook on raft: enabled by default.
- Container Shelf free reposition: enabled by default.
- Manual custom reposition: disabled by default.
- Emergency upright: action button that levels the nearest raft.

Stranded Deep Mod Settings is optional. Raft Furniture continues to load and use its BepInEx config without the menu host.

## Persistence

Raft Furniture uses both the normal BepInEx config and a sidecar attachment file:

`BepInEx/config/StrandedDeepRaftFurniture/Attachments.json`

The sidecar stores supported object-to-raft association and raft-local pose information. Update/uninstall workflows should preserve or back up persistent data.

## Local split-screen

The mod is designed for the project's two-player local split-screen setup. Reposition input is resolved per local player through the game's player/Rewired context.

## Compatibility

- Requires BepInEx.
- Optional: Stranded Deep Mod Settings `0.2.0` or newer.
- Compatible with Stranded Deep Table Storage ownership rules: raft-managed tables are left to Raft Furniture, while land-table storage behavior remains owned by Table Storage.

## Installation

Use the release ZIP and extract it into the Stranded Deep game directory so the package's `BepInEx` folder merges with the game's existing `BepInEx` folder.

Restart the game after install, update, or uninstall.

## Building

Set the Stranded Deep game directory explicitly:

```powershell
powershell.exe `
  -NoProfile `
  -ExecutionPolicy Bypass `
  -File .\build.ps1 `
  -GameDir '<Stranded Deep game directory>'
```

Add `-Deploy` to copy the compiled DLL into the game's BepInEx plugin directory.

The compiler defaults to the .NET Framework compiler under `%WINDIR%`, or can be overridden with `-Csc`.
