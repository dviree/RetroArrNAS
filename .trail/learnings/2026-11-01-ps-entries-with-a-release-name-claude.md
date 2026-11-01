---
title: "PS entries with a release name get the game's name, and two PS4 folders of one game merge only when PS4_MERGE is on"
author: Claude
date: 2026-11-01
branch: nas/phase-a-qbit-import
cost: { minutes: 12, tokens: 1038928 }
id: l-a1d105f4
scope: repo
status: active
src: agent
---

## Decided
- A PS name that has a title ID but is not one of ours (a date prefix like "261028 - ", a source tag like [VIKINGFILE] or [1FICHIER], or the ID outside brackets as in a dotted/underscore release name) is renamed like a title-ID-only name. Our own tags are [ID], [vN], [Update] and [DLC ...]. Backports keep their release name.
- The title comes from the package, then a well-named library entry for that ID, then the AI, then the title in the release name.
- A PS4 game folder is named "<Game> [CUSA01234]" with no version, because it holds every PKG of the game.
- Two roms/ps4 folders for one title ID are merged into the best-named one, never over a file of the same name. This moves files, so it is a dry run (logged only) unless PS4_MERGE=true, per the owner's rule that moving features start off.
- "BP-10.xx" / "BP_7.xx" in a name means backport, as "backport" does.
- Update and DLC PKGs at any depth under roms/ps5/pkg (the old pkg/dlc/<Game> [DLC]/ layout) move to the backport-updates-dlc folder, and the folders that empties go.

## Watch out
- Looking up the library's title for an ID must skip release-named entries, or "261028 - Stray" becomes the title.

In the code: PlayStationLibraryNamer.NeedsName, LibraryTitle, MergePs4Duplicates, MoveDlcIntoTheirFolder; PlayStationImporter.IsBackport, Ps4GameFolder, CleanReleaseName.
