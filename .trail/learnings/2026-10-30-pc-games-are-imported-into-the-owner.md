---
title: PC games are imported into the pc folder; windows is only read as the same platform
author: owner
date: 2026-10-30
branch: nas/phase-a-qbit-import
cost: { minutes: 4, tokens: 17906 }
id: l-c693edd6
scope: repo
status: active
replaces: l-c594acd5
src: agent
touches:
  - src/RetroArr.Core/Games/PlatformDefinitions.cs
---

## Decided
The PC platform's folder is pc, because the owner wants PC games in pc and imports kept landing in windows (the folder upstream used). windows stays an alias, so games already in roms/windows are still found as PC games, and the many places that use windows as the platform key keep working.

## Watch out
Nothing is moved from roms/windows to roms/pc by RetroArr; that is the owner's own move.

In the code:
- PlatformDefinitions Id 1: FolderName pc, FolderAliases windows
- GetEffectiveFolderName decides the import folder; MatchesFolderName and ResolveRuleForPlatform read the alias
