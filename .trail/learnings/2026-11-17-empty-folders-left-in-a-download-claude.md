---
title: "Empty folders left in a download folder are removed by the cleanup; they hold nothing, so they skip the trash"
author: Claude
date: 2026-11-17
branch: nas/phase-a-qbit-import
cost: { minutes: 7, tokens: 1874235 }
id: l-a28c022c
scope: repo
status: active
src: agent
touches:
  - BACKLOG.md
  - SNAPSHOT.md
  - src/RetroArr.Core.Test/Download/PlayStationImporterTest.cs
  - src/RetroArr.Core.Test/Download/PostDownloadSourceCleanupTest.cs
  - src/RetroArr.Core/Download/PlayStationImporter.cs
  - src/RetroArr.Core/Games/LeftoverCleaner.cs
  - src/RetroArr.Core.Test/Games/LeftoverCleanerTest.cs
---

## Decided
The owner saw empty folders piling up in the downloads. Some are a release folder at the top of the watch folder that its game was moved out of: RetroArr's leftover rule never takes a top-level folder, to spare the sorting folders. Others are left by JDownloader itself: stopped unpacks, downloads that never came.
The cleanup now removes folders with no file anywhere below them, untouched for CLEANUP_DAYS (default 2), in watch folders in move mode. It removes them deepest first, each only while still empty. It never touches:
- the watch folder itself,
- the sorting folders extracted/, _extracted/ and usenet/,
- hidden folders,
- a watch folder RetroArr only copies from (qBittorrent's, whose files are never changed).

Each folder is logged in files.log as DELETE. In a dry run it is logged as WOULD DELETE.
They are removed, not trashed: there is nothing in them to bring back, and a bin full of empty folders is noise.

## Watch out
List the folders before removing any. Removing a folder dates its parent anew, so the parent would look fresh and be kept.

In the code: LeftoverCleaner.EmptyDownloadFolders/DownloadRoots/SortingFolders.
