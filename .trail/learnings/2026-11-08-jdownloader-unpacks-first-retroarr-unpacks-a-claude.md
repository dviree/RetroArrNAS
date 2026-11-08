---
title: "JDownloader unpacks first: RetroArr unpacks a rar set itself only after three hours and only if JDownloader has not"
author: Claude
date: 2026-11-08
branch: nas/phase-a-qbit-import
cost: { minutes: 4, tokens: 24980 }
id: l-18772ac8
scope: repo
status: active
src: agent
touches:
  - BACKLOG.md
  - README.md
  - SNAPSHOT.md
  - src/RetroArr.Core.Test/IO/FileActionsTest.cs
  - src/RetroArr.Core/Download/DownloadMonitorService.cs
  - src/RetroArr.Core/Games/BiosSorter.cs
  - src/RetroArr.Core/Games/LeftoverCleaner.cs
  - src/RetroArr.Core/Games/PlayStationLibraryNamer.cs
  - src/RetroArr.Core/Games/Ps4PkgMergeService.cs
  - src/RetroArr.Core/Games/SwitchMergeService.cs
---

## Decided
The owner wants JDownloader to unpack its downloads. A watch folder download that is a rar/7z/zip set (archives make up 90% or more of its bytes) is held back as ImportBlocked, with a message, until its newest file is ARCHIVE_WAIT_HOURS (default 3) old. After that it still waits while JDownloader's copy is there. That copy is a folder of the same name elsewhere under the watch root, such as extracted/<name>, holding at least one non-empty file. The copy is imported first. The set is then found in the library and goes to the trash without being unpacked.
An import the owner starts by hand does not wait (CheckAsync byHand).

## Watch out
An empty folder left by a stopped JDownloader unpack does not count as a copy. That happened to Ghost of Yotei on Nov 8: an empty PPSA26344-app0 was left behind at 10:08.

In the code: CompletedDownloadService.WaitBeforeUnpacking/ArchiveWait, CheckAsync(byHand), DownloadClientController.ManualImport.
