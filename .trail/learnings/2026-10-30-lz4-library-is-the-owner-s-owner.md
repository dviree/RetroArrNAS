---
title: "LZ4 library is the owner's"
author: owner
date: 2026-10-30
branch: nas/phase-a-qbit-import
cost: { minutes: 1, tokens: 3085 }
id: l-0b889387
scope: repo
status: active
src: agent
touches:
  - src/RetroArr.Core/Download/PlayStationImporter.cs
  - src/RetroArr.Core/Games/PlayStationLibraryNamer.cs
---

The extra PS library (PS_LIBRARY_EXTRA, the LZ4 test library) is only renamed in place, never moved from or filled; a game there does not count as in the library. An LZ4 folder in roms/ps5 means LZ4 works for that game. LZ4 folders are never replaced by an import, and RetroArr never compresses to LZ4.
