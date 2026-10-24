# Stranded Deep Raft Furniture

## Current release

**0.2.15**

Manual Reposition supports both controller and keyboard/mouse input in local split-screen. Keyboard/mouse rotation follows the player's existing Stranded Deep `Rotate Object` bindings, including user remapping, while controller input retains the proven raw-input fallback.

Stranded Deep Raft Furniture turns the raft into a practical mobile base by allowing selected furniture, survival structures, and compatible Item Framework items to remain stable on a moving raft. The mod is designed for two-player local split-screen and preserves the existing vanilla construction chains and interaction lifecycles wherever possible.

Current stable release: `0.2.14`

## 0.2.14 вЂ” Item Framework RaftSurface integration

`0.2.14` adds a parallel soft integration with **Stranded Deep Item Framework API v1**. A content mod can declare `ItemCapabilities.RaftSurface`; Raft Furniture then detects the real runtime item instance and gives it raft-deck behavior automatically. Content mods do not call Raft Furniture directly and do not contain raft-specific physics code.

The integration is deliberately isolated from the known-good legacy attachment systems. Existing tables/chairs, Water Still, Light Hook, `STRUCTURE_SMALL` (`Camp Fire -> Fire Pit -> Smoker`) and Container Shelf continue to use their existing Raft Furniture paths. `RaftAttachmentRegistry` and `Attachments.json` are unchanged.

For Framework items, the native Stranded Deep save remains authoritative for the item and its world pose. On cold load, Item Framework restores the native host and Raft Furniture performs a bounded local deck rebind for `RaftSurface` items. No second Framework-item raft sidecar is used. This behavior was field-tested with two restored Framework test items: both reattached after Save -> full exit -> restart -> Load and then followed the raft; pickup/drop after load also reattached correctly.

## Features

- Tables and chairs can be used as persistent raft furniture.
- Water Still can be placed and repositioned on the raft.
- Light Hook can be placed and repositioned on the raft.
- Camp Fire, Fire Pit and Smoker preserve the vanilla `STRUCTURE_SMALL` construction chain while working on the raft.
- Food attached to raft cooking stations follows the station while the raft moves.
- Native Container Shelf can be freely repositioned without replacing its crate sockets.
- Local split-screen reposition input is resolved per player.
- Framework items with `RaftSurface` can attach to raft decks through optional Item Framework API v1 integration.
- Framework-item attachment uses full raft-local position and rotation and targeted own-raft collision isolation.
- Framework items automatically rebind after native cold restore when they are still spatially on the deck.
- Existing legacy attachment persistence and recovery behavior remains unchanged.
- Emergency raft-upright action remains available through optional Mod Settings integration.

## Controls

For supported construction reposition operations:

- Hold `L2 / LT` to drag.
- Release `L2 / LT` to drop.
- Press `L1 / LB` to rotate by 15 degrees.

Framework inventory items continue to use their normal Item Framework / native pickup and drop lifecycle; they do not use the custom L2/LT construction reposition controller.

## Settings

The mod owns its BepInEx configuration. If Stranded Deep Mod Settings is installed, the existing user-facing construction settings remain available under `Settings -> MODS`.

Existing settings include Water Still, Camp Fire/Fire Pit/Smoker, Light Hook, Container Shelf reposition, manual custom reposition, and Emergency Upright.

Advanced BepInEx-only safety switch:

```ini
[Framework Item API]
EnableFrameworkRaftItems = true
```

The Framework bridge is enabled by default. If Item Framework is absent or its API is incompatible, the bridge disables itself and all legacy Raft Furniture systems continue to operate.

## Persistence

Legacy supported furniture continues to use:

`BepInEx/config/StrandedDeepRaftFurniture/Attachments.json`

Framework `RaftSurface` items do **not** use this file and do not have a second Raft Furniture sidecar. Their item existence/world pose is saved by the normal Stranded Deep + Item Framework native lifecycle; raft association is reconstructed at runtime after load.

Framework integration diagnostics are written to:

`BepInEx/config/StrandedDeepRaftFurniture/ItemApiRuntime.log`

## Compatibility

- Requires BepInEx.
- Optional: Stranded Deep Mod Settings `0.2.0` or newer.
- Optional: Stranded Deep Item Framework `0.1.16` or newer for `RaftSurface` items.
- Compatible with the established Table Storage ownership split: Raft Furniture owns raft-managed root transform/physics while Table Storage owns storage behavior.

## 0.2.14 validation

The Framework path passed the focused vertical slice for registration/capability discovery, two independent runtime instances, raft attach/follow, pickup, off-raft detach, same-raft reattach, P2 pickup/drop, native Save -> full exit -> restart -> Load, automatic cold-load rebind, and raft movement after load.

The 0.2.14 regression pass also covered Water Still reposition, Light Hook reposition, Container Shelf reposition, STRUCTURE_SMALL / Smoker reposition, cookable re-anchoring, Save/Load, and Framework cold-load rebind. The legacy table/chair path and Emergency Upright were not re-exercised in this 0.2.14 validation cycle; their implementation remains unchanged from the established 0.2.13 path.

## Building

```powershell
powershell.exe `
  -NoProfile `
  -ExecutionPolicy Bypass `
  -File .\build.ps1 `
  -GameDir '<Stranded Deep game directory>'
```

Add `-Deploy` to copy the compiled DLL into the game's BepInEx plugin directory. Item Framework is a soft runtime dependency; `build.ps1` does not reference `StrandedDeepItemFramework.dll`.

