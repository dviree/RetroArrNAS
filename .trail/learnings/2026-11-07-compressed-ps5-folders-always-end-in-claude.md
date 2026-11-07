---
title: "Compressed PS5 folders always end in -lz4 or -zstd by content, and locked rars get their password as JDownloader finds it"
author: Claude
date: 2026-11-07
branch: nas/phase-a-qbit-import
cost: { minutes: 9, tokens: 48648 }
id: l-9c5ecffc
scope: repo
status: active
src: agent
touches:
  - BACKLOG.md
  - README.md
  - SNAPSHOT.md
  - src/RetroArr.Core.Test/FileActionsOnForTests.cs
  - src/RetroArr.Core.Test/Games/LeftoverCleanerTest.cs
  - src/RetroArr.Core.Test/Games/PlayStationLibraryNamerTest.cs
  - src/RetroArr.Core/Download/DownloadMonitorService.cs
  - src/RetroArr.Core/Games/BiosSorter.cs
  - src/RetroArr.Core/Games/LeftoverCleaner.cs
  - src/RetroArr.Core/Games/PlayStationLibraryNamer.cs
---

## Decided
- The owner wants to see from a folder's name which compression it holds. An LZ4 or zstd PS5 game folder always ends in "-lz4" or "-zstd", decided by what is inside it (ampr_zstd-*.pak means zstd), never by the old name. This applies on import and when the PlayStation namer renames (LacksCompression makes it rename a folder that is missing the ending).
- A locked rar or 7z is opened the way MyJDownloader does it. RetroArr tries, in order:
  1. the owner's list (archive-passwords.txt in the config folder, or ARCHIVE_PASSWORDS_FILE),
  2. ARCHIVE_PASSWORDS,
  3. what the names hold: a site tag like "[DLPSGAME.COM]" and its variants, a domain, the release name with and without the tag.

  A password fits when the smallest locked file comes out whole. The log names the archive, never the password.

## Watch out
SharpCompress cannot write rar or 7z, so the tests only check the candidate list, not a real locked archive.

In the code: PlayStationImporter.CompressionSuffix, PlayStationLibraryNamer.LacksCompression/NewNameAsync, IO/ArchiveService.FindPassword/PasswordCandidates.
