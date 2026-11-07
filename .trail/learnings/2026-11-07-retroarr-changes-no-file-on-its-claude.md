---
title: "RetroArr changes no file on its own until FILE_ACTIONS=on: until then each job only writes what it would do to files.log"
author: Claude
date: 2026-11-07
branch: nas/phase-a-qbit-import
cost: { minutes: 8, tokens: 79018 }
id: l-f457e6e4
scope: repo
status: active
src: agent
touches:
  - BACKLOG.md
  - SNAPSHOT.md
  - src/RetroArr.Core.Test/Download/PostDownloadSourceCleanupTest.cs
  - src/RetroArr.Core.Test/Games/LeftoverCleanerTest.cs
  - src/RetroArr.Core/Download/PlayStationImporter.cs
  - src/RetroArr.Core/Download/PostDownloadProcessor.cs
  - src/RetroArr.Core/Games/BiosSorter.cs
  - src/RetroArr.Core/Games/LeftoverCleaner.cs
  - src/RetroArr.Core/Games/LibraryResortService.cs
  - src/RetroArr.Core/Games/PlayStationLibraryNamer.cs
---

## Decided
The owner turned off every file change RetroArr makes on its own until they have read the plan. FILE_ACTIONS is a dry run unless it is "on". The jobs it covers:
- imports from the download monitor,
- the leftover cleanup,
- the trash's own emptying,
- the Switch merge and Switch 2 sort,
- the PS4 merge,
- the PlayStation naming, sorting and extras sort,
- the BIOS sorter.

Each planned step is written once (per run of RetroArr) as a "WOULD <ACTION>" line in files.log. All plans and all real file actions go to that one file (owner: "one log file, not several"). There are no more cleanup-plan.log, bios-sort-plan.log or extras-sort-plan.log files, and files.log lines no longer go to app.log.
A manual import or delete from the UI is the owner acting, and still runs.

## Watch out
The test suite turns file actions on in a SetUpFixture (FileActions.Override). A test of the dry run sets it to false and back.

In the code: src/RetroArr.Core/IO/FileActions.cs, FileLog.Planned, DownloadMonitorService.ProcessTrackedDownloadsAsync, PlayStationLibraryNamer.RunOnceAsync (Rename swapped for a planner), SwitchMergeService.RunOnceAsync/MoveSwitch2Games, Ps4PkgMergeService.RunOnceAsync, TrashService.PurgeExpired, LeftoverCleaner.DryRun, BiosSorter.DryRun.
