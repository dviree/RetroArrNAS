---
title: "A PC release is one game, told from a folder of games by its name"
author: owner
date: 2026-10-30
branch: nas/phase-a-qbit-import
cost: { minutes: 5, tokens: 22630 }
id: l-e618a385
scope: repo
status: active
src: agent
touches:
  - "src/RetroArr.Core/Download/WatchFolderClient.cs#PcGameFolders"
  - "src/RetroArr.Core/Download/WatchFolderClient.cs#Classify"
---

## Decided
A folder whose only game folder shares a word with its name, or whose name carries a repack tag (SteamRIP, FitGirl, DODI, ...), is one PC release and is taken whole, because a SteamRIP release imported while it was still being unpacked landed three times: the release, its game folder and its Win64 folder. Downloads also wait 15 minutes without writes before import, up from 2, because unpacking 54 GB takes longer than that.

## Tried and ruled out
Telling a release by a _CommonRedist folder beside the game folder: ruled out because a plain collection folder (usenet/Graveyard Keeper 2 beside usenet/_CommonRedist) has the same shape and must stay a collection.

## Watch out
Two or more game folders in one folder still mean a collection, each its own game.

In the code:
- WatchFolderClient.PcGameFolders, PcReleaseTag, RedistFolders; SettleTime 15 min
- tests APcRelease_IsOneGame_HoweverDeepItsExeLies, Recursive_FindsGamesInsideCollections
