# Building Bridge component — v0.1.21

This source was copied byte-for-byte from the user-tested `SDMODES/stranded-deep-raft-building-bridge-prototype-v0.1.21` package (2026-10-25 audit). Do not substitute the separate older canonical Bridge source.

## Architecture

- One shared vanilla `Construction_STRUCTURE` with ten hidden `WOOD_FOUNDATION` (prefabId 81) instances is anchored to a ten-floor raft in the proven forensic test.
- Hidden foundations retain WALL, HALF_WALL and ROOF snap connectors; solid foundation colliders are disabled. Independent own-raft collision isolation and a transform follower keep visible buildings on the moving raft.
- Building Bridge native objects live in Stranded Deep `data_COOP.json`. On cold load the Bridge matches its saved construction group and reconstructs foundation-to-floor snap targets.
- Construction recognition in v0.1.21 uses an exact type-and-prefabId catalog, supporting recovery of previously omitted PLANK WINDOW 126 and BRICK building definitions. Catalog coverage is **not** a guarantee that every building recipe can be manually placed on a raft.
- `F9` creates a Bridge on a selected raft if none exists. It is a **development-only manual** flow, not an automatic raft-building feature. `F10` refuses to remove deployed/recovered bridges.
- The Furniture plugin continues to own `BepInEx/config/StrandedDeepRaftFurniture/Attachments.json`; Building Bridge does not migrate the sidecar or create a new Furniture persistence format.

## Field evidence known at 2026-10-25

- Building Bridge v0.1.20: both P1 and P2 could build; raft and structures moved together; cold load rebuilt ten hidden foundations, native Save/Load recovered five visible building parts.
- Building Bridge v0.1.21: tested PLANK WINDOW 126, clay/brick roof and their save/reload; a door placed *properly into a frame* opened, closed and followed the raft. Save/Load for that correctly framed door was not separately confirmed.
- A diagnostic spawner once auto-placed a loose door into the ground below the raft, causing severe collision impulses. This is not evidence that properly frame-snapped doors are unsupported. Diagnostic Spawner is not included in this integration package.

## Runtime identities (do not change)

- Furniture plugin GUID: `bamex.strandeddeep.raftfurniture`, DLL: `StrandedDeepRaftFurniture.dll`.
- Building Bridge plugin GUID: `bamex.strandeddeep.raftbuildingbridgeprototype`, DLL: `StrandedDeepRaftBuildingBridgePrototype.dll`.
- Current aggregate distribution metadata `bepInExGuid` remains the Furniture GUID (primary product identity), while the second DLL is explicitly covered by `packagePaths` and `ownedPaths`.

## Release and safety constraints

1. Preserve both runtime DLLs and their GUIDs in the first integrated release.
2. Never change or delete `Attachments.json` or a live game save as part of compilation/packaging.
3. `build.ps1 -Deploy` in the legacy Furniture project may remove unrelated diagnostics. Use `build-integrated.ps1` (build only) instead.
4. The integration's runtime RC ZIP uses audited installed DLL hashes; it does not silently package newly compiled binaries.
5. Full combined field-testing, deterministic Distribution Tool verification, public-safety scan and Mod Manager install/uninstall/upgrade acceptance are required before publishing GitHub v0.2.16.
