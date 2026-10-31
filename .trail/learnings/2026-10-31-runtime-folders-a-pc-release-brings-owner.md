---
title: Runtime folders a PC release brings along are never games
author: owner
date: 2026-10-31
branch: nas/phase-a-qbit-import
cost: { minutes: 1, tokens: 3012 }
id: l-5e99210e
scope: repo
status: active
src: agent
touches:
  - src/RetroArr.Core/Download/WatchFolderClient.cs
---

## Decided
PhysX, Vorbis, Windows, DirectX, Redist, Prerequisites and Engine folders are skipped when a watch folder is looked into, because a PC release taken apart landed them in the library as empty games of their own (PhysX, Vorbis, Windows beside Sackboy). This is not telling a release by its _CommonRedist folder, which stays ruled out: these folders are only never taken as games themselves.

## Watch out
They are skipped only as units of a watch folder; inside a game that is taken whole they stay with it.

In the code:
- WatchFolderClient.NeverGames; test RuntimeAndEngineFolders_AreNeverGames
