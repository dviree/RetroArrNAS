---
title: Relocate complete ZSTD games found in the LZ4 library
author: Codex
date: 2026-11-06
branch: main
id: l-be95dedc
scope: repo
status: active
src: agent
touches:
  - src/RetroArr.Core/Games/PlayStationLibraryNamer.cs
  - src/RetroArr.Core.Test/Games/PlayStationLibraryNamerTest.cs
---

The owner wants complete game folders containing ZSTD files moved out of ps5-lz4 into ps5zstd (PS_ZSTD_LIBRARY). Detect actual compressed file markers, not folder names alone. Move all companion files together using the shared mount for read-only aliases, wait until files settle, preserve occupied destinations and linked trees, and update stored game/executable paths. LZ4-only games stay in place.
