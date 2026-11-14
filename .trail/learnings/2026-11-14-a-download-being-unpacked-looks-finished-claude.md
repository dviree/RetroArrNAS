---
title: A download being unpacked looks finished by its file dates; folder dates and a missing first rar part tell it is not
author: Claude
date: 2026-11-14
branch: nas/phase-a-qbit-import
cost: { minutes: 30, tokens: 2662541 }
id: l-07612c78
scope: repo
status: active
src: agent
touches:
  - README.md
  - CHANGELOG.md
  - package.json
  - frontend/src/pages/Status.tsx
  - src/RetroArr.Api.V3/DownloadClients/DownloadClientController.cs
  - src/RetroArr.Core.Test/Download/DownloadClientAddTest.cs
  - src/RetroArr.Core.Test/Download/DownloadPlatformTrackerTest.cs
  - src/RetroArr.Core.Test/Download/TrackedDownloadServiceTest.cs
  - src/RetroArr.Core.Test/Download/TransmissionClientTest.cs
  - src/RetroArr.Core.Test/Search/MonitoredGameSearchServiceTest.cs
---

## Decided
A God of War release failed on the NAS with "no first volume found". JDownloader was still unpacking it: it removes each rar part once it is through it, so part01..05 were gone and part06..11 were left, and it was unpacking into the same folder. Two fixes:
- A watch folder's download counts as finished only when its newest file and its newest folder are both 15 minutes old. unrar and JDownloader give each file the date it has in the archive, often weeks old, so a half-unpacked game looked finished by its files alone. The folders the unpack creates and fills are new. This is probably also why Ghost of Yotei failed earlier with "Source file not found" on a file that was there.
- A rar set whose first part is missing waits ("JDownloader is unpacking it"), whatever else lies beside it. Once all its parts are gone, the unpacked game is imported like any download.

## Watch out
Tests that make files look old have to age their folders too, or the watch folder takes them for downloads still coming in.

In the code: WatchFolderClient.NewestChange (Describe and DescribePlayStation), CompletedDownloadService.WaitBeforeUnpacking (FirstVolume), ArchiveWaitTest, WatchFolderClientTest.Write.
