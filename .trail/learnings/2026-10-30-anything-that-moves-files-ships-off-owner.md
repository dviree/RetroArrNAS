---
title: "Anything that moves files ships off, with a dry run first"
author: owner
date: 2026-10-30
branch: nas/phase-a-qbit-import
cost: { minutes: 1, tokens: 3085 }
id: l-b24551c0
scope: repo
status: active
src: agent
touches:
  - src/RetroArr.Core/Games/BiosSorter.cs
---

A feature that moves, renames or trashes the owner's files is off by default and gets a dry-run mode that only writes a plan file; it is turned on only after the owner read the plan. It never takes apart folders it did not create (emulator layouts like 86Box/roms or MAME sets must stay whole). The first BIOS sorter broke this and took the owner's BIOS folders apart.
