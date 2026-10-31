---
title: A folder in the Switch library with no game left goes to the trash
author: owner
date: 2026-10-31
branch: nas/phase-a-qbit-import
cost: { minutes: 5, tokens: 24655 }
id: l-a7e84a50
scope: repo
status: active
src: agent
touches:
  - "src/RetroArr.Core/Games/SwitchMergeService.cs#ClearLeftoversAsync"
---

## Decided
After the tidy, a folder in roms/switch holding no NSP/NSZ/XCI/XCZ any more goes to the trash, because an update whose archive could not be unpacked at import was copied into the library as a 31 GB .rar and stayed there after the unpacked NSP had been merged; the owner wants one file per game in roms/switch.

## Tried and ruled out
Leaving every archive out of the import once something was unpacked beside it: ruled out because the import deliberately keeps an archive whose unpacking clashed with the download's own files, and tests hold that.

## Watch out
Files written to in the last 10 minutes are left for a later run, so an import still copying in is never taken.

In the code:
- SwitchMergeService.ClearLeftoversAsync, called after Tidy in RunOnceAsync; PartSettle
- test AFolderWithNoGameLeft_GoesToTheTrash_GamesStay
