---
title: "A PS DLC with no readable header takes the game's name from the DLC beside it, never from a guess"
author: Claude
date: 2026-11-03
branch: nas/phase-a-qbit-import
cost: { minutes: 12, tokens: 83674 }
id: l-11db9187
scope: repo
status: active
src: agent
---

## Decided
- Sackboy's DLC carry their own title ID (PPSA01289) while the game is PPSA01288, and the raw DLC PKGs (UP9000-PPSA01289_00-SABA....pkg) have no readable header. The namer asked the AI, which guessed "Sable" from "SABA", and three files got that name.
- The title now comes from the library first, DLC names of ours included ("Sackboy A Big Adventure [PPSA01289] [DLC ...]"), before the AI. When only DLC carry the ID and the PKG's header gives no title or version, the PKG is one more DLC: it gets "[DLC <label>]" from its content ID and goes to dlc/.
- A file named without a title ID ("sable.pkg") never gives the title; only a name that carries the ID can.
- A file whose right name is already taken by a file of the same size is a copy and goes to the trash.
- The namer tests each have their own extras folder: one test used to write into the real default folder, and a later test read a title from it.

## Watch out
- A header that does say "game" (CATEGORY gd) is believed even when only DLC carry the ID.

In the code: PlayStationLibraryNamer.LibraryTitleOf, NewNameAsync, RunOnceAsync (copy to trash), constructor (TrashService); test SetUp sets ExtrasRoot.
