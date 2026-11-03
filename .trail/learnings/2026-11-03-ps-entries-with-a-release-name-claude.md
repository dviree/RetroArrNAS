---
title: "PS entries with a release name get the game's name, and two PS4 folders of one game are merged into one right away"
author: Claude
date: 2026-11-03
branch: nas/phase-a-qbit-import
cost: { minutes: 3, tokens: 21689 }
id: l-7321c235
scope: repo
status: active
replaces: l-a1d105f4
src: agent
---

## Decided
- A PS name with a title ID that is not one of ours (date prefix "261028 - ", source tags like [VIKINGFILE], an ID outside brackets in a dotted release name) is renamed like a title-ID-only name. Our tags: [ID], [vN], [Update], [DLC ...]. Backports keep their release name.
- A PS4 game folder is "<Game> [CUSA01234]", no version: it holds every PKG of the game.
- Two roms/ps4 folders for one title ID are merged into the best-named one on the first run, on by default: the owner asked for one folder per game and was annoyed to find Ghostbusters still in two. A same-size file of the same name is a copy and goes to the trash; a different one stays. PS4_MERGE=false keeps it a dry run.
- Base game and update stay separate PKGs in that folder: a PS4 installs them as separate packages. pkg-merge (Tustin) only joins one package split into parts (X_0.pkg, X_1.pkg ...) by appending them; it cannot join a game and its update.

## Watch out
- Looking up the library's title for an ID must skip release-named entries, or "261028 - Stray" becomes the title.

In the code: PlayStationLibraryNamer.NeedsName, MergePs4, MergePs4Duplicates; PlayStationImporter.Ps4GameFolder, CleanReleaseName.
