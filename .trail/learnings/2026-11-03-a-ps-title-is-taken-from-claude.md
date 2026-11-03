---
title: "A PS title is taken from the name most library files with that ID share, before the AI"
author: Claude
date: 2026-11-03
branch: nas/phase-a-qbit-import
cost: { minutes: 3, tokens: 22372 }
id: l-d12032cc
scope: repo
status: active
replaces: l-11db9187
src: agent
---

## Decided
- The importer and the namer take a PS game's name from what the library already calls that title ID, in roms, roms/ps5/pkg and every backport-updates-dlc folder (game folders included), before asking the AI. The name most entries share wins, the game's own entries first on a tie, so one wrong name cannot spread.
- Why: on the NAS the AI guessed each Sackboy DLC (PPSA01289) from its file name alone and named some "Sable" and one "Saboteur!", with high confidence (98 %), so a confidence cutoff would not catch it. The importer used to look only in roms/ps5 and took the first match.
- A small PS5 PKG is a DLC or update (see the 1 GB rule), so a DLC is never "the game, already in the library" and is not rejected. The first "Sable" file had blocked every other Sackboy DLC that way.

## Watch out
- Names that are not ours yet (NeedsName) are skipped when the namer counts.

In the code: PlayStationImporter.KnownTitle, ExtrasFoldersIn, TitleForAsync; PlayStationLibraryNamer.LibraryTitleOf; test ARawDlc_TakesTheNameMostOfItsIdGive_AndIsNeverRejectedAsTheGame.
