---
title: A Switch file with no title ID in its name or ticket is read by nscb_rust
author: owner
date: 2026-10-31
branch: nas/phase-a-qbit-import
cost: { minutes: 3, tokens: 12858 }
id: l-c6220b06
scope: repo
status: active
src: agent
touches:
  - "src/RetroArr.Core/Games/SwitchMergeService.cs#Scan"
---

## Decided
When a file's name has no title ID and its NSP has no ticket to read it from, nscb_rust --ADVfilelist reads the CONTENT ID from the file's own content, because Tomodachi Life's base had neither, stayed unrecognised in roms/switch while its update waited alone in the parts folder, and so the two were never merged.

## Watch out
Each file is read once per size (cached); a read that fails or takes over 2 minutes leaves the file unrecognised, as before.

In the code:
- SwitchMergeService.ReadTitleId, RustTitleIdReader (CONTENT ID line), set in RunOnceAsync when nscb_rust is there
- test AGameWithNoIdInItsNameOrTicket_IsReadFromItsContent
