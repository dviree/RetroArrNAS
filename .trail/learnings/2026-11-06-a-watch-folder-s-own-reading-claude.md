---
title: "A watch folder's own reading of the platform wins over an older run's, and a failed import is tried again when it changes"
author: Claude
date: 2026-11-06
branch: nas/phase-a-qbit-import
cost: { minutes: 10, tokens: 45690 }
id: l-cb3a7805
scope: repo
status: active
src: agent
---

## Decided
- A tracked download kept the platform an older RetroArr stored, and an ImportFailed one was never looked at again. So after RetroArr learned a new format (Switch 2 .nup, zstd PS5 folders), files it had already seen stayed in jdownloader forever.
- Now, when the client itself reports a platform (a watch folder reads the files) that differs from the stored one, and no grab set the platform, the client's wins; an ImportFailed download goes back to ImportPending and is imported again. Downloads already Imported are left alone.

## Watch out
- A platform from a grab (a search the owner made) is never overridden this way.
- A zstd game an older run already imported into roms/ps5 stays there; this does not move it.

In the code: TrackedDownloadService.TrackDownload(download, clientId, clientName, grabs); test AFailedImport_IsTriedAgain_WhenTheWatchFolderNowReadsAnotherPlatform.
