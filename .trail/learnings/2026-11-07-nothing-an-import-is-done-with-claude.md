---
title: "Nothing an import is done with is deleted: sources, unpacked archives and unwanted files go to the trash whole, and every file action is in files.log"
author: Claude
date: 2026-11-07
branch: nas/phase-a-qbit-import
cost: { minutes: 14, tokens: 149974 }
id: l-2e09bf53
scope: repo
status: active
src: agent
touches:
  - src/RetroArr.Core/IO/FileLog.cs
  - src/RetroArr.Core/Logging/AppLoggerService.cs
  - src/RetroArr.Core/Download/PostDownloadProcessor.cs
  - src/RetroArr.Core/Games/LeftoverCleaner.cs
  - src/RetroArr.Core/Download/PlayStationImporter.cs
  - src/RetroArr.Core.Test/Games/LeftoverCleanerTest.cs
  - src/RetroArr.Core.Test/Download/PostDownloadSourceCleanupTest.cs
  - src/RetroArr.Core/Games/TrashService.cs
  - src/RetroArr.Core/IO/FileMoverService.cs
  - src/RetroArr.Core/Games/SwitchMergeService.cs
---

## Decided
The owner found a game folder gone and asked why RetroArr deletes for good. Usenet-style downloads used to be deleted after import (DeleteSource), their archives after unpacking, and deep-clean files (.nfo, .txt) on the spot. Now:
- a finished source always goes to RetroArr's trash whole (PostDownloadProcessor always has a TrashService),
- unpacked archives are set aside in <download>/.retroarr-unpacked and go to the trash with the download,
- deep-clean files are only left out of the import.
Only RetroArr's own staging, work and partial files are still deleted, and each one is logged.
Every move, copy, trash, restore, purge and delete is logged through IO.FileLog (the "RetroArr.Files" logger), always at Info, into files.log and app.log.

## Watch out
- The trash empties itself after TrashRetentionDays (default 14, Settings > Media). Those purges are permanent and are now logged one by one with the original path.
- Imports hard-link when they can, so a source left in downloads is the same file as the library copy: writing to it changes the library file.
- LeftoverCleaner aged an entry by its files' dates only. A copy or an unpack keeps the files' old dates, so a running import or a fresh unpack looked old. Folder dates now count too.

In the code: src/RetroArr.Core/IO/FileLog.cs, PostDownloadProcessor.DeleteSource/SetAside/GetImportFiles, LeftoverCleaner.Old, AppLoggerService.Configure (files target).
