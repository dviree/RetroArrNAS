---
title: "A PS5 PKG under 1 GB is an update or a DLC, because PS5 headers cannot be read"
author: Claude
date: 2026-11-03
branch: nas/phase-a-qbit-import
cost: { minutes: 16, tokens: 58534 }
id: l-6c6e5815
scope: repo
status: active
src: agent
---

## Decided
- The owner's rule: a PKG under 1 GB with a PS5 title ID (PPSA/ECAS) is an update or a DLC, never a game; a game is many GB. It is an update when its name says update/patch or its version is past 1.0, else a DLC. It goes to updates/<Game>/ or dlc/<Game>/ and gets the [Update] or [DLC <label>] tag.
- Why size: PS5 PKGs start with 7F 46 49 48 and RetroArr reads only PS4 PKG headers (7F 43 4E 54), so a PS5 PKG's header says nothing; its name and size are all there is.
- The title comes from names already in the library (DLC included) before the AI. The AI only sees the file name and the ID; it named Sackboy DLC (SABA...) "Sable".
- A PKG whose title ID is only in its folder's name keeps the ID in front of its name when it is moved, so it can still be named.

## Watch out
- The tests write tiny stand-in PKGs for games, so a SetUpFixture sets the 1 GB limit to 0; the tests of the rule set it back.

In the code: PlayStationImporter.ExtrasKind, SmallPkg, IsUpdateOrDlcPkg, ImportOneAsync; PlayStationLibraryNamer.NewNameAsync, MoveDlcIntoTheirFolder; test TestDefaults.
