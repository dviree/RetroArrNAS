---
title: Switch is one file per game
author: owner
date: 2026-10-30
branch: nas/phase-a-qbit-import
cost: { minutes: 1, tokens: 3085 }
id: l-56c2b277
scope: repo
status: active
src: agent
touches:
  - src/RetroArr.Core/Games/SwitchMergeService.cs
---

roms/switch holds one file per game, right in it, no folders. A game in more than one file waits in the parts folder (SWITCH_PARTS_FOLDER) until base, newest update and every DLC are merged into one NSP. Merging uses nscb_rust (the tool the old system used), not the Python NSC_Builder; the merged file is verified with lv3 before the parts go to the trash. Output is NSP, not XCI.
