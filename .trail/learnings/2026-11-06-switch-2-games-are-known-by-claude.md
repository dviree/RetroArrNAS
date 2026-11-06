---
title: "Switch 2 games are known by a .nup file, a title ID starting 04, or NSW2/Switch 2 in a name, and go to roms/switch2"
author: Claude
date: 2026-11-06
branch: nas/phase-a-qbit-import
cost: { minutes: 4, tokens: 25688 }
id: l-20f4aa7d
scope: repo
status: active
replaces: l-e8a5bdbc
src: agent
---

## Decided
- A Switch 2 game: a .nup file, a 16-hex title ID starting 04 ("[0400486024DEC000]"; Switch games start 01), or NSW2 / Switch 2 in its release, folder or file name, but not "Switch 2 compatible" and not "Switch 2.0 Update". Confirmed on the owner's own file: "The Legend of Zelda Ocarina of Time [0400486024DEC000] [v131072].nup", 24 GB.
- Imports put it in roms/switch2; the Switch service makes roms/switch2 and moves a Switch 2 game found in roms/switch there before any merge, and never takes one as a part of a Switch game. Never over another file; a same-size copy goes to the trash.
- .nup is a game file in the import's, the watch folder's and the scanner's lists.

## Tried and ruled out
- Guessing the title ID prefix before one was seen: a reference wiki was not reachable, so the rule waited for a real file.

In the code: SwitchMergeService.IsSwitch2, Switch2Name, MoveSwitch2Games, Scan; WatchFolderClient ExtensionTags/TitleIdTags/DetectPlatform; PostDownloadProcessor and TitleCleanerService extension lists; MediaScannerService switch2 extensions.
