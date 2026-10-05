# Changelog

## 0.2.14 - 2026-10-05

- Added Russian/English Mod Settings localization.
- Synced the vendored Mod Settings reflection bridge with the authoritative bilingual SDK.
- No gameplay, controller, attachment or persistence behavior changed.
- Russian/English Mod Settings and local split-screen gameplay were field-tested before publication.

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
