---
title: roms/ps5 holds one copy of a game per type; only a copy of the same type makes it already there
author: Claude
date: 2026-11-14
branch: nas/phase-a-qbit-import
cost: { minutes: 11, tokens: 34694 }
id: l-d3385b1f
scope: repo
status: active
replaces: l-5eca8640
src: agent
touches:
  - BACKLOG.md
  - src/RetroArr.Core.Test/Download/ArchiveWaitTest.cs
  - src/RetroArr.Core.Test/Download/WatchFolderClientTest.cs
  - src/RetroArr.Core/Download/TrackedDownloads/CompletedDownloadService.cs
  - src/RetroArr.Core/Download/WatchFolderClient.cs
  - src/RetroArr.Core/Download/PlayStationImporter.cs
  - src/RetroArr.Core.Test/Download/PlayStationImporterTest.cs
  - src/RetroArr.Core.Test/Download/PostDownloadSourceCleanupTest.cs
---

## Decided
The owner: there may be several copies of a game in roms/ps5 as long as they are different types, not the same thing. The types are PKG, uncompressed folder, LZ4 folder, zstd folder, .exfat image and .ffpfsc image.
- An import is skipped only when the same type is there in the same or a newer version, or in a version that cannot be told.
- An older copy of the same type is replaced (it goes to the trash). A compressed folder is never replaced.
- Before unpacking, an archive's table of contents tells the type. A compressed folder in it may be LZ4 or zstd, so either counts.
- roms/ps5 still alone decides. Copies in the LZ4 test library or the zstd library do not count.

## Watch out
- A zstd folder is told by its ampr_zstd files, or by its name ending in -zstd.
- An LZ4 folder is told by its ampr_assets files, or by -lz4 in its name.

In the code: PlayStationImporter.CopyType/TypeOf/SameTypeAsInside, ImportOneAsync, AlreadyThere.
