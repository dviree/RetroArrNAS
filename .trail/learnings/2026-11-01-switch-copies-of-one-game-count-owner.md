---
title: "Switch: copies of one game count once, and a merge of parts without original signatures is accepted"
author: owner
date: 2026-11-01
branch: nas/phase-a-qbit-import
cost: { minutes: 4, tokens: 24692 }
id: l-2878531a
scope: repo
status: active
src: agent
touches:
  - "src/RetroArr.Core/Games/SwitchMergeService.cs#Tidy"
  - "src/RetroArr.Core/Games/SwitchMergeService.cs#OnlySignature"
---

## Decided
Copies of the same file (same title ID, version and size) count as one game: one goes to roms/switch, the other copies to the trash, because Kingdom Hearts lay twice (57.7 GB in a folder in roms and in the parts folder), so it was taken for a game in parts and its folder never went.
A merged NSP whose only failing check is the signature (decryption and hashes correct) is published when one of its parts already failed the signature check before the merge, because Tomodachi Life's base has its ticket taken out, so every merge of it reads 'could've been tampered with' and it was never published. A signature failure the parts did not have still stops the merge.

## Watch out
The library check now writes such files as MODIFIED, not BROKEN: they play, their signature is not the original one.

In the code:
- Tidy (distinct by TitleId, Version, Size; Duplicates sent to the trash in RunOnceAsync)
- OnlySignature, SignatureWasNotOriginalAsync (each part verified with --vertype lv2)
