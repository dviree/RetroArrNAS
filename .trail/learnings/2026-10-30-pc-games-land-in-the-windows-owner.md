---
title: "PC games land in the windows folder, though the platform is called pc"
author: owner
date: 2026-10-30
branch: nas/phase-a-qbit-import
cost: { minutes: 1, tokens: 1234 }
id: l-c594acd5
scope: repo
status: active
src: agent
touches:
  - src/RetroArr.Core/Games/PlatformDefinitions.cs
  - src/RetroArr.Core/Download/WatchFolderClient.cs
---

## Decided
Nothing changed yet, because the owner has to choose windows or pc as the one PC folder first.

## Watch out
The PC platform's slug is pc but its folder name is windows, because upstream RetroArr named it so. Every import of a PC game therefore goes to roms/windows, while games moved to roms/pc by hand sit beside it: otherwise one would expect a single PC folder, and the library can hold PC games in both.

In the code:
- PlatformDefinitions: Id 1, Name "PC (Windows)", Slug "pc", FolderName "windows"
- WatchFolderClient tags a release with only .exe files as PC; the test WindowsPrograms_ArePcGames expects the folder windows
