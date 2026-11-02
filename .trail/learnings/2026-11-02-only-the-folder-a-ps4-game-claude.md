---
title: "Only the folder a PS4 game is merged into gets the game's name"
author: Claude
date: 2026-11-02
branch: nas/phase-a-qbit-import
cost: { minutes: 3, tokens: 302631 }
id: l-ac16e0e2
scope: repo
status: active
src: agent
touches:
  - CHANGELOG.md
  - package.json
  - src/RetroArr.Core.Test/Download/CompletedDownloadServiceTest.cs
  - src/RetroArr.Core.Test/Download/DownloadHistoryRepositoryTest.cs
  - src/RetroArr.Core.Test/Download/NzbgetClientTest.cs
  - src/RetroArr.Core.Test/Download/TrackedDownloadServiceTest.cs
  - src/RetroArr.Core/Download/History/DownloadHistoryRepository.cs
  - src/RetroArr.Core/Download/NzbgetClient.cs
  - src/RetroArr.Core/Download/TrackedDownloads/TrackedDownloadService.cs
---

## Decided
- When two roms/ps4 folders hold one game, only the folder they merge into is renamed to "<Game> [CUSA01234]". A folder left beside it (in a dry run, or holding a file of the same name that was not moved) keeps its own name.
- The namer goes through a folder in name order.

## Tried and ruled out
- Renaming every folder that needs a name, in the order the file system lists them. Whichever came first took the game's name; on GitHub's runner that was the leftover folder, so the merged one kept its release name. Passed locally, failed on CI.

In the code: PlayStationLibraryNamer.RunOnceAsync (OrderBy, _otherCopies), MergePs4Duplicates; test TwoPs4FoldersOfOneGame_BecomeOne_OnlyWhenMergeIsOn.
