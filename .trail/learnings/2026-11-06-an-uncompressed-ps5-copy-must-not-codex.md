---
title: An uncompressed PS5 copy must not suppress an import into the zstd library
author: Codex
date: 2026-11-06
branch: main
id: l-bc21f055
scope: repo
status: active
src: agent
touches:
  - "src/RetroArr.Core/Download/PlayStationImporter.cs#ImportOneAsync"
---

A zstd release was skipped by InOtherLibraries because roms/ps5 already held the same title and version in another format. ImportOneAsync now checks zstd duplicates only inside ZstdLibrary and bypasses the cross-library suppression for zstd imports. The existing ordinary library and its compressed folders are not replaced. AZstdGame_GoesToTheZstdLibrary_NotRomsPs5 covers an already present uncompressed copy as well as an empty library. This is an import routing fix, not a change to source verification or trash cleanup.
