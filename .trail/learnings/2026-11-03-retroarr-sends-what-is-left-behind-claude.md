---
title: "RetroArr sends what is left behind to the trash on its own: its work folders, half-written files, and folders the owner names"
author: Claude
date: 2026-11-03
branch: nas/phase-a-qbit-import
cost: { minutes: 8, tokens: 628585 }
id: l-a6cd497a
scope: repo
status: active
src: agent
touches:
  - src/RetroArr.Core.Test/Games/PlayStationLibraryNamerTest.cs
  - src/RetroArr.Core/Games/PlayStationLibraryNamer.cs
  - src/RetroArr.Core/Games/LeftoverCleaner.cs
---

## Decided
- The owner expects leftovers to go without asking: an old tool's work copies and test folders were taking space. A background job (every 6 h) sends to the trash, never deletes: everything in RetroArr's own work folders (PS staging, Switch merge work), RetroArr's half-written files in roms (".retroarr-ps-*", ".retroarr-merge-*", ".retroarr-extract-*" at roms/<platform>/ and one level below), and the contents of folders the owner lists in CLEANUP_FOLDERS.
- Only what nothing has written to for CLEANUP_DAYS (2 by default) goes, so work in progress stays; the folders themselves stay.
- On by default for RetroArr's own leftovers, because it made them; other folders only when the owner names them, which is their go-ahead. CLEANUP=dryrun writes cleanup-plan.log, CLEANUP=false turns it off.

## Watch out
- The old tool's folder name is the owner's and stays out of the repo: it goes in their compose file as CLEANUP_FOLDERS, never as a default.

In the code: Games/LeftoverCleaner.cs, registered in RetroArr.Host Program.cs; README env table; test LeftoverCleanerTest.
