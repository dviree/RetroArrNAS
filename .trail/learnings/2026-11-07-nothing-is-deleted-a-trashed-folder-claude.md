---
title: "Nothing is deleted; a trashed folder stays whole in the bin, its files in it as they lay"
author: Claude
date: 2026-11-07
branch: nas/phase-a-qbit-import
cost: { minutes: 5, tokens: 22393 }
id: l-626609ff
scope: repo
status: active
replaces: l-94bc1003
src: agent
---

## Decided
- Nothing is ever deleted: what goes goes to the trash (the bin).
- The owner changed the layout: a trashed folder stays whole in the bin under its own name, its files inside as they lay where they came from. No loose files. (Before, a folder was laid out flat, each file loose in the bin.)
- Entries laid out flat before are put back into their folders by the leftover cleaner's run, from the original paths each file's sidecar kept. Only renames inside the bin, never over a file; restore still brings everything back whole.

In the code: TrashService.Flatten (now false), RegroupFlatEntriesAsync, ReadFlat; LeftoverCleaner.RunOnceAsync calls the regroup; tests ATrashedFolder_StaysWholeInTheTrash_AndComesBackWhole, FilesLaidOutFlatBefore_GoBackIntoTheirFolder_AndStillComeBackWhole.
