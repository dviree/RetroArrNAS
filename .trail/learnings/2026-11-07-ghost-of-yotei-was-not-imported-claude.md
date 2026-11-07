---
title: "Ghost of Yotei was not imported: part01.rar was taken for a later volume, and a one-off failed import was never tried again"
author: Claude
date: 2026-11-07
branch: nas/phase-a-qbit-import
cost: { minutes: 10, tokens: 64183 }
id: l-5fdca485
scope: repo
status: active
src: agent
---

## Decided
- Confirmed on the NAS: the unpacked release failed once with "Source file not found" for a file that exists (another program, likely the LZ4 converter, held it for a moment); it stayed ImportFailed for good. The rar set failed with "no first volume found" although part01.rar is there.
- The rule for later rar volumes read "part01" as a two-digit later part. It now takes part2, part02, part10 as later and part1, part01, part001 as the first.
- A watch folder's failed import is tried again after an hour, at most 3 times, while its source is still there. One that failed before the time was kept counts as long ago.

## Watch out
- Torrent and Usenet clients' failed imports are not retried this way; only watch folders.

In the code: PlayStationImporter.LaterRarPart; TrackedDownload.FailedAt, FailedImports, MarkFailed; DownloadMonitorService.RetryFailedWatchFolderImport, ProcessTrackedDownloadsAsync.
