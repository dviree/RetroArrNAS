---
title: roms/ps5 alone decides whether a PS5 game is in the library; zstd and LZ4 copies elsewhere do not count
author: Claude
date: 2026-11-07
branch: nas/phase-a-qbit-import
cost: { minutes: 6, tokens: 19274 }
id: l-5eca8640
scope: repo
status: active
replaces: l-05048722
src: agent
---

## Decided
- The owner: "zstd or LZ4, it does not matter, roms/ps5 is what we go by." A PS5 game in roms/ps5 in any form (image, folder, LZ4 folder, PKG for a PKG) is in the library: the JDownloader release is not imported again and goes to the trash in move mode. A copy only in the zstd library or the ps5-lz4 test folder does not count: the release is imported into roms/ps5, then goes to the trash.
- This replaces the same day's rule that kept the download when a compressed copy existed; that keep-source code was taken out again.
- Kept: a compressed copy (LZ4 or zstd) in roms/ps5 is never replaced as an older version.

## Watch out
- "Goes to the trash" means the bin, never a delete, and only for watch folders in move mode; torrent files are never touched.

In the code: PlayStationImporter.ImportOneAsync (IsCompressedCopy guard), ImportAsync; PostDownloadProcessor.ImportPlayStationAsync; test WatchFolder_Ps5Download_RomsPs5Decides_AndTheReleaseGoesToTheTrash.
