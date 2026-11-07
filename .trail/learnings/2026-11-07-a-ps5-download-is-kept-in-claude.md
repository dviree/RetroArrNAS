---
title: A PS5 download is kept in JDownloader when the title already has a compressed copy (zstd library or LZ4 in roms/ps5)
author: Claude
date: 2026-11-07
branch: nas/phase-a-qbit-import
cost: { minutes: 11, tokens: 1622638 }
id: l-05048722
scope: repo
status: active
src: agent
touches:
  - CHANGELOG.md
  - package.json
  - src/RetroArr.Core/Download/NzbFile.cs
  - src/RetroArr.Core.Test/Games/PlayStationLibraryNamerTest.cs
  - src/RetroArr.Core/Games/PlayStationLibraryNamer.cs
  - Dockerfile
  - README.md
  - scripts/ps4-pkg-smoke.py
  - src/RetroArr.Core.Test/Games/Ps4PkgMergeServiceTest.cs
  - src/RetroArr.Core/Games/Ps4PkgMergeService.cs
---

## Decided
- The owner's rule: when the same PS5 title already exists as zstd in PS_ZSTD_LIBRARY or as LZ4 in roms/ps5, the JDownloader download stays where it is, even in watch-folder move mode and even when the import is skipped as already there. It applies to archives and unpacked games alike.
- A compressed PS5 copy (LZ4 or zstd) is never replaced as an older version when a newer release is imported.
- A copy only in the ps5-lz4 test folder (PS_LIBRARY_EXTRA) blocks nothing: the release is imported into roms/ps5 and its verified source goes to the trash as usual.

## Watch out
- Ghost of Yotei (PPSA26344) started this, but why that one import did not happen is not confirmed.

In the code: PlayStationImporter.Outcome.KeepSource, HasCompressedPs5Copy, IsCompressedCopy, ImportOneAsync, ImportArchiveAsync; PostDownloadProcessor.ImportPlayStationAsync; tests WatchFolder_KeepsPs5DownloadWhenACompressedCopyExists and WatchFolder_ACopyOnlyInThePs5Lz4TestFolder_DoesNotBlockTheImport.
