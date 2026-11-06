---
title: zstd PS5 games are imported into the zstd library; zstd and LZ4 folders are never replaced
author: Claude
date: 2026-11-06
branch: nas/phase-a-qbit-import
cost: { minutes: 3, tokens: 22907 }
id: l-217b3313
scope: repo
status: active
replaces: l-4e76b678
src: agent
---

## Decided
- A zstd-compressed PS5 game folder (ampr_zstd-NNN.pak, ampr_emu.index, package.manifest, eboot.bin; seen on the owner's Saros download) is imported into PS_ZSTD_LIBRARY (default /nas/games/ps5zstd) as "<Game> [ID] [vN]-zstd", not into roms/ps5. The owner made that folder for them and expected the download to land there.
- Unlike the LZ4 library (only renamed, never filled, the owner fills it), the zstd library is filled by imports, because the owner asked for it.
- Folders and files named zstd/.zst or holding ampr_zstd-* count as compressed like LZ4: never replaced by an import, renamed in place keeping their -zstd ending. The zstd library also belongs in PS_LIBRARY_EXTRA so the namer names what is in it.

## Watch out
- A download still being written is taken only after the watch folder's settle time; a .pak written in the last minutes means it is not done.

In the code: PlayStationImporter.ZstdLibrary, IsZstd, ImportOneAsync; PlayStationPackageInspector.IsCompressedName; PlayStationLibraryNamer.NewNameAsync.
