---
title: "A PC game's folder is the game's name, never the release name"
author: owner
date: 2026-11-01
branch: nas/phase-a-qbit-import
cost: { minutes: 3, tokens: 12235 }
id: l-f1856fda
scope: repo
status: active
src: agent
touches:
  - "src/RetroArr.Core/Download/PostDownloadProcessor.cs#PcFolderName"
---

## Decided
When no title is found for a PC release, its folder takes the AI's title, else the release name without its repack tag and separators (Mirrors-Edge-SteamRIP.com becomes Mirrors Edge), because the owner wants each PC game as one folder named for the game: release-named folders like Death-Stranding-DC-SteamRIP.com were left in roms/pc.

## Tried and ruled out
Running the general title cleaner on it: ruled out because it drops edition words, so Ni no Kuni Wrath of the White Witch Remastered lost Remastered.

In the code:
- PostDownloadProcessor.PcFolderName, _repackTag; used in AutoMoveToLibrary when no name was chosen
- test APcGameWithNoTitleFound_GetsItsReleaseNameWithoutTheRepackTag
