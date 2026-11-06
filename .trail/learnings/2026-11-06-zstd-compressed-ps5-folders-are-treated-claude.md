---
title: "zstd-compressed PS5 folders are treated like LZ4 ones: the owner's, renamed in place, never replaced"
author: Claude
date: 2026-11-06
branch: nas/phase-a-qbit-import
cost: { minutes: 4, tokens: 24167 }
id: l-4e76b678
scope: repo
status: active
src: agent
---

## Decided
- The owner added a zstd library next to the LZ4 one. A PS5 game folder or file named with zstd ("-zstd", ".zst") counts as compressed exactly like LZ4: never replaced by an import, never moved out of its library, and the namer keeps its "-zstd" ending when it gives it the game's name.
- The zstd library is one more PS_LIBRARY_EXTRA entry (comma separated), like the LZ4 library: only renamed, never filled.

## Watch out
- Recognition is by name only ("zstd", ".zst", plus the existing ampr_assets marker); a zstd folder named without "zstd" is taken for an uncompressed one.

In the code: PlayStationPackageInspector.IsCompressedName (used by Inspect, HasLz4Marker, PeekArchive), PlayStationImporter.KindOf, PlayStationLibraryNamer.NewNameAsync/NeedsName.
