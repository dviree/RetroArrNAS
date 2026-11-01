---
title: "The PS5 extras folder has three folders: backport, updates and dlc"
author: Claude
date: 2026-11-01
branch: nas/phase-a-qbit-import
cost: { minutes: 26, tokens: 84288 }
id: l-7ff05588
scope: repo
status: active
src: agent
touches:
  - CLAUDE.md
---

## Decided
- The owner wants backports, updates and DLC apart inside the backport-updates-dlc folder: backport/<release>/, updates/<PKG>, dlc/<PKG>. New imports and PKGs moved out of roms/ps5 go straight to the right one, chosen by the [Update]/[DLC] tag in the name or else by the package itself.
- What lay loose in the extras folder from before is the owner's existing files, so sorting it follows the owner's moving rule: a dry run that writes extras-sort-plan.log unless PS_EXTRAS_SORT=true. Nothing is moved over a file of the same name.
- The scanner looks in the root and all three folders, so a game's updates and DLC still count as its copies.

## Watch out
- trail blast marks Folders and ImportOneAsync as not reached by a test because the graph does not follow RunOnceAsync/ImportAsync; the namer and importer tests do run them.

In the code: PlayStationImporter.ExtrasFolders, ExtrasFolderFor, SaveBackports; PlayStationLibraryNamer.SortExtras, MoveDlcIntoTheirFolder, MovePkgsIntoTheirFolder, Folders; MediaScannerService.PlayStationCopies.
