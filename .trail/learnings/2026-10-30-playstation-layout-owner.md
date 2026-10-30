---
title: PlayStation layout
author: owner
date: 2026-10-30
branch: nas/phase-a-qbit-import
cost: { minutes: 1, tokens: 3085 }
id: l-220d87a4
scope: repo
status: active
src: agent
touches:
  - src/RetroArr.Core/Download/PlayStationImporter.cs
  - src/RetroArr.Core/Games/PlayStationLibraryNamer.cs
---

The title ID decides the console (PPSA/ECAS ps5, CUSA ps4), never the release name. PS5: images, game folders and LZ4 folders in roms/ps5, game PKGs in roms/ps5/pkg, update and DLC PKGs and backports together in PS_BACKPORT_FOLDER (backport-updates-dlc), outside the library. PS4: every PKG of a game in one folder in roms/ps4, no pkg folder. PKGs are kept beside any other format. Every PS file gets the game's name: Title [PPSA01234] [v1.2].
