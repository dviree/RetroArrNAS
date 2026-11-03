---
title: "The PS5 extras folder has backport, updates and dlc, each with a folder per game, and loose files are sorted in right away"
author: Claude
date: 2026-11-03
branch: nas/phase-a-qbit-import
cost: { minutes: 10, tokens: 39577 }
id: l-cc025a72
scope: repo
status: active
replaces: l-31e503c7
src: agent
---

## Decided
- Layout of backport-updates-dlc: updates/<Game>/<PKG>, dlc/<Game>/<PKG>, backport/<Game>/<release>/. The owner asked for the three folders, then for every file under its game.
- The game comes from our own name ("Sackboy A Big Adventure [PPSA01289] [DLC ...]"). A file not named yet waits in its kind folder until the namer names it, then moves on the next run. A backport release names its game by the title ID in its name (library title, else the release name cleaned); a folder in backport/ without an ID is taken to be a game's folder already.
- On by default, as the owner asked for it; PS_EXTRAS_SORT=false keeps everything a dry run. Never over a file of the same name; a same-size copy goes to the trash.
- The importer writes an update or DLC straight into updates/<Game>/ or dlc/<Game>/. The scanner looks into the game folders of updates/ and dlc/ too.

## Watch out
- Files written in the last six hours wait (QuietFor), so a fresh download is sorted on a run after that.

In the code: PlayStationImporter.GameOfName, ExtrasTargetFor, ImportOneAsync; PlayStationLibraryNamer.SortExtras, SortIntoGameFoldersAsync, Folders; MediaScannerService.PlayStationCopies.
