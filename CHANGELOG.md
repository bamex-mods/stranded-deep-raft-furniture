# Changelog

## 0.2.16 — integration release candidate (NOT PUBLISHED)

- Bundles the field-tested Raft Furniture v0.2.15 and Building Bridge v0.1.21 sources and runtime DLLs under the existing Raft Furniture repository.
- Adds the Bridge runtime path to explicit `packagePaths` and `ownedPaths` for the Mod Manager.
- Preserves both BepInEx plugin GUIDs, both separate assemblies, and both persistence models.
- Includes a safe build-only orchestration script and a local release-candidate staging tool that verifies the exact installed runtime DLL hashes.
- No gameplay code changes to the Furniture or Bridge components. The merged package still requires a combined field-test before public release.
- Known Bridge limitation: F9 is still a manual development command for setting up a new raft; F10 must not be used on a deployed saved bridge.


## 0.2.15 вЂ” 2026-10-24

### Added
- Keyboard and mouse support for custom Manual Reposition.
- Bidirectional rotation through the game's existing `Rotate Object` bindings.
- User-remapped keyboard/mouse controls are respected by reading the existing Rewired mappings.

### Changed
- Manual Reposition input is now split-screen aware across keyboard/mouse P1 and controller players.
- Controller support keeps the proven raw L2/LT drag and L1/LB / R1/RB rotation fallback.

### Fixed
- Fixed keyboard rotation failing outside vanilla placement mode because Stranded Deep keeps the relevant Rewired keyboard/mouse `ControllerMap` disabled.
- The mod now reads the physical elements referenced by the player's existing mappings without enabling the vanilla placement map.
- Temporary KB/M diagnostic code used during development is not part of the release source.

### Compatibility
- No changes to raft attachment persistence, save format, Framework item integration, follower logic, or raft collision architecture.

## 0.2.14 - 2026-10-22

- Adds optional soft integration with Stranded Deep Item Framework API v1.
- Adds support for Framework item types declaring the `RaftSurface` capability.
- Keeps the new Framework path strictly parallel to the existing Raft Furniture systems; legacy `RaftAttachmentRegistry`, `Attachments.json`, tables/chairs, Water Still, Light Hook, `STRUCTURE_SMALL`, Container Shelf and custom reposition behavior are not migrated.
- Uses reflection-only Framework binding, so Raft Furniture continues to load normally when Item Framework is absent.
- Adds per-instance Framework raft agents with cached references; there is no global Framework-item world scan.
- Captures full raft-local position and quaternion rotation for attached Framework items.
- Applies targeted item-to-owning-raft collision ignores while attached and restores prior collision states on detach.
- Preserves the proven pickup safety rule: old own-raft collision is not restored prematurely while an item is still leaving the deck.
- Reattaches Framework items when dropped back onto the same or another raft and returns normal world behavior when dropped off-raft.
- Adds bounded cold-load rebind for active, non-held Framework items restored kinematic on a raft deck.
- Keeps native Stranded Deep + Item Framework save state authoritative for Framework item existence/world pose; no second Framework-item attachment sidecar is introduced.
- Adds `ItemApiRuntime.log` for Framework bridge diagnostics.
- Focused field test passed with two independent `RaftSurface` test items, including P2 pickup/drop, raft movement, Save -> full exit -> restart -> Load, automatic rebind, and raft movement after load.
- Framework bridge safety switch is enabled by default and remains isolated from all legacy Raft Furniture paths.

## 0.2.13 - 2026-10-04

- Fixes violent raft linear/angular impulses while repositioning `Water Still` and the `Camp Fire -> Fire Pit -> Smoker` `STRUCTURE_SMALL` chain.
- Temporarily isolates every target-collider x owning-raft-hierarchy collider pair during custom reposition.
- Restores non-raft-body collision-pair states exactly after confirm/cancel while keeping dynamic own-raft body pairs ignored, matching the mod's persistent raft collision policy.
- Temporarily disables solid target colliders while `Water Still` or `STRUCTURE_SMALL` is being repositioned so moving kinematic geometry cannot push P1/P2 or unrelated world rigidbodies.
- Keeps trigger colliders enabled to preserve interaction/cooking behavior.
- Captures the pre-isolation pivot-to-bottom distance so deck snapping and `STRUCTURE_DROP_NORMALIZE` remain stable while solid colliders are disabled.
- Restores every target collider to its exact original enabled state on confirm/cancel.
- Retains the existing Container Shelf isolation path, Light Hook behavior, furniture vanilla drag lifecycle, attachment persistence and save format.
- Field-tested in two-player local split-screen, including P1/P2 reposition, rotate, confirm/cancel, Save -> full restart -> Load, and repeated Water Still / Smoker movement.

## 0.2.11 - 2026-10-04

- Canonicalized the current Raft Furniture runtime line for public distribution.
- Retains persistent raft attachment/follower behavior and load-time deck repair.
- Supports tables, chairs, Water Still, Light Hook, Camp Fire / Fire Pit / Smoker, and Container Shelf repositioning.
- Retains split-screen-aware gamepad reposition controls.
- Retains optional Mod Settings integration.
- Adds/retains manual reposition configuration and emergency raft-upright recovery.
- No persistence format migration is introduced by this public-source canonicalization.


