---
title: "Switch 2 ROMs stay loose, while verified source copies still go to trash"
author: Codex
date: 2026-11-06
branch: main
id: l-940e8941
scope: repo
status: active
src: agent
touches:
  - src/RetroArr.Core/Download/PostDownloadProcessor.cs
  - src/RetroArr.Core/Games/SwitchMergeService.cs
---

Switch 2 ROMs belong directly in the switch2 platform folder, without a title folder or release subfolders. PostDownloadProcessor bypasses title patterns for Switch 2 in both generic and game-targeted imports, keeps game paths on their files, and refuses flat name collisions. SwitchMergeService also flattens earlier Switch 2 imports and repoints their library entries, removing only emptied folders; root files must not become duplicates of themselves. The owner explicitly accepted the existing copy, verify, then trash source workflow after its space use was explained: preserve that workflow and torrent source protection. PS4_MERGE currently consolidates folders; it does not rebuild base plus patch into one PKG. Trail check flags the Switch 2 collision guard as reviving a PC naming rule; this is a lexical false positive because the guard is reachable only for Switch 2 and does not change PC names.
