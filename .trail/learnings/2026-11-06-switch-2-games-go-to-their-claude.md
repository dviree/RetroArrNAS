---
title: "Switch 2 games go to their own roms folder, known by NSW2 or Switch 2 in a name, never by a guessed title ID"
author: Claude
date: 2026-11-06
branch: nas/phase-a-qbit-import
cost: { minutes: 5, tokens: 1275289 }
id: l-e8a5bdbc
scope: repo
status: active
src: agent
---

## Decided
- A Switch 2 game is one whose release, folder or file name says NSW2 or Switch 2, but not "Switch 2 compatible" (a Switch game that also runs on a Switch 2) and not "Switch 2.0 Update". Imports put it in roms/switch2; the watch folder now also reads the names of the files inside a folder.
- The Switch service makes roms/switch2 beside roms/switch and moves a Switch 2 game found in roms/switch there before any merge, so it is never merged as a Switch game. Never over another file; a same-size copy goes to the trash.

## Tried and ruled out
- Telling Switch 2 games by their title ID prefix: the prefix could not be confirmed (the reference wiki was not reachable), so no guessed rule was built.

In the code: SwitchMergeService.Switch2Name, Switch2Folder, MoveSwitch2Games, RunOnceAsync; WatchFolderClient.DetectPlatform.
