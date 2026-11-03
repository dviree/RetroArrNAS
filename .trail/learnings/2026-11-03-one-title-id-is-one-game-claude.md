---
title: "One title ID is one game: a PS name that disagrees with a clear majority is renamed, and a copy found on any move goes to the trash"
author: Claude
date: 2026-11-03
branch: nas/phase-a-qbit-import
cost: { minutes: 14, tokens: 595847 }
id: l-59011bd1
scope: repo
status: active
src: agent
---

## Decided
- A PS entry named for a game that most entries with its title ID disagree with ("Sable [PPSA01289]" among ~55 "Sackboy A Big Adventure [PPSA01289] ...") is renamed to the majority's game, and moved to that game's folder. A clear majority is required: at least 3 entries, and at least twice the odd name's count; a tie changes nothing.
- A file in another game's folder under updates/ or dlc/ moves to its own game's folder; a game folder left empty goes.
- Every namer move (PS5 pkg to extras, loose PKG to pkg, PS4 PKG to its game folder, into a game folder) sends a same-size file whose place is taken to the trash instead of leaving it behind.
- Why: the NAS had three "Sable" files from an AI guess; two were byte copies of Sackboy DLC, one a Sackboy update. The owner wanted RetroArr to clean it up, not a hand-run script.

## Watch out
- Votes are counted once per run per ID (cache cleared at the start of each run).

In the code: PlayStationImporter.TitleVotes; PlayStationLibraryNamer.TitleDisagrees, SortIntoGameFoldersAsync, TrashIfCopy; test TheNasSackboyMess_EndsInOneSackboyFolder_CopiesInTheTrash_NoSableLeft.
