---
title: "Nothing is deleted, the trash is flat"
author: owner
date: 2026-10-30
branch: nas/phase-a-qbit-import
cost: { minutes: 1, tokens: 3085 }
id: l-94bc1003
scope: repo
status: active
src: agent
touches:
  - src/RetroArr.Core/Games/TrashService.cs
---

Removing anything means TrashService.MoveAsync, never a delete. The trash lies flat: each file right in it under its own name, a trashed folder spread into its files, the metadata for restore in the hidden .trash-meta folder. The owner looks through it by hand, so no folder per entry.
