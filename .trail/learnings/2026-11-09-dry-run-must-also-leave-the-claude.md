---
title: "Dry run must also leave the library list, empty folders and the done-log alone, and locked archives are tested with real files"
author: Claude
date: 2026-11-09
branch: nas/phase-a-qbit-import
cost: { minutes: 9, tokens: 524648 }
id: l-2b8adf7d
scope: repo
status: active
src: agent
touches:
  - src/RetroArr.Core/Games/PlayStationLibraryNamer.cs
  - src/RetroArr.Core/Download/DownloadMonitorService.cs
  - src/RetroArr.Core.Test/RetroArr.Core.Test.csproj
  - src/RetroArr.Core.Test/IO/LockedArchiveTest.cs
  - src/RetroArr.Core/IO/ArchiveService.cs
  - BACKLOG.md
---

## Decided
- A review of the dry run found three leaks in the PlayStation namer. In a dry run it still (1) pointed the library list at the new zstd path, though the game had not moved, (2) removed folders that were already empty, and (3) logged "Renamed/Moved/Sorted" as if done. All three now go through Dry, Did() and RemoveIfEmpty(). The download monitor also re-checked and logged a planned import every minute; a planned download is now skipped.
- Every hosted job was checked. Only imports, cleanup, trash purge, Switch merge, PS4 merge, PS naming and the BIOS sorter change files on their own. The library re-sort, deletes and removing a download from a client only run from the UI.
- Locked archives are now tested with real fixtures made with rar and 7z (src/RetroArr.Core.Test/Fixtures/Archives). That found a bug: a solid rar with only its data locked never found its password, because the smallest file of a solid archive cannot be read on its own. Now the first file is read for a solid archive. When the headers are locked, listing the files proves the password and no data is read.

## Watch out
New code in a job that changes files must check the job's dry run before anything that changes state, also the database and log lines that claim something was done.

In the code: PlayStationLibraryNamer.Dry/Did/RemoveIfEmpty/MoveZstdLibrariesAsync, DownloadMonitorService.ProcessTrackedDownloadsAsync, ArchiveService.FindPassword/Opens, LockedArchiveTest.
