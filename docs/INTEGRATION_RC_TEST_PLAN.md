# Raft Furniture 0.2.16 — local integration acceptance checklist

**Status:** NOT RUN. The integration does not itself prove the two DLLs as a single releasable product. Start with a closed game and copy of the *current* full Slot0 save; verify its data_COOP.json SHA256 and restore it afterwards.

1. Verify both installed DLL hashes against the audit (`EDB9A576...` Furniture and `79FEAC6D...` Bridge). Never replace runtime files from the code integration script.
2. Activate an established forensic test save via the existing, separately audited procedure; avoid F9 and F10 while testing a recovered existing bridge.
3. Load from a complete game exit. Confirm furniture (including Container Shelf and Water Still) is attached correctly and the existing Bridge performs `AUTO_RECOVER_DONE` with ten snap-grid repairs.
4. P1 and P2 independently snap and build supported walls, PLANK WINDOW 126 and a clay roof on the same raft; ensure ghosts do not remain red. Test input ownership under local split-screen.
5. Properly snap a door into a doorframe, verify opening/closing and following; do not confuse it with Diagnostic Spawner's instant ground placement. Check Save/Load separately before marking its persistence proven.
6. Sail/move/turn the raft; look for violent impulses, misaligned walls, shelves, cooking structures or player interaction loss.
7. Save the forensic test copy, completely quit, relaunch and load; verify all placed structures and furniture, then try snapping a new item to a recovered foundation.
8. Separately verify the v0.1.21 recovery edge case: a saved bridge with foundations but only **BRICK** visible building parts (no wooden/window/roof members already supported in 0.1.20). Do not claim this specific fix field-proven until logged.
9. Validate a local manager upgrade/install/uninstall using both explicit owned plugin directories and a backup of `BepInEx/config/StrandedDeepRaftFurniture/Attachments.json`. Uninstalling while saves contain Bridge-backed constructions is high risk; test on a disposable save.
10. Restore the original live Slot0 using the proven procedure; compare original SHA256. Keep milestone and integration backups. Only then consider public release pipeline.

Use the canonical Distribution Tool for the final publication artifact, then verify runtime DLL SHA and deterministic ZIP reproducibility. Do not upload audit archives or private save/config files.
