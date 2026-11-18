---
title: "The bin keeps each day's things in a folder named by the date, like 261104"
author: Claude
date: 2026-11-18
branch: nas/phase-a-qbit-import
cost: { minutes: 12, tokens: 1944097 }
id: l-c178e5e7
scope: repo
status: active
src: agent
touches:
  - BACKLOG.md
  - SNAPSHOT.md
  - src/RetroArr.Core.Test/Games/LeftoverCleanerTest.cs
  - src/RetroArr.Core/Games/LeftoverCleaner.cs
  - src/RetroArr.Core/Games/TrashService.cs
---

## Decided
The owner wants the bin sorted by day: what goes to the trash lies in a folder named by that day's date (yyMMdd, e.g. 261104), whole and under its own name inside it. The sidecars stay in the bin's .trash-meta.
- Entries from before, lying right in the bin, are still listed, restored and purged.
- A day folder is removed once a restore or purge leaves it empty, or when a move into it fails before anything arrives.
- A restore or purge only reaches what lies right in the bin or in a day folder directly under it. A sidecar pointing anywhere else is taken as lying right in the bin under its name.

## Watch out
The date is the NAS's local date (DateTime.Now). Tests find the day folder with TrashService.DayFolderName(DateTime.Now).

In the code: TrashService.MoveAsync/DayFolderName/LiesInTheTrash/RemoveEmptyDayFolder/ReadFlat/Purge/Restore.
