---
title: A PS file whose name has no title ID is named from its own header
author: Claude
date: 2026-11-03
branch: nas/phase-a-qbit-import
cost: { minutes: 3, tokens: 21884 }
id: l-cf504232
scope: repo
status: active
src: agent
---

## Decided
- A PKG, exFAT or FFPFSC file named without a title ID ("sable.pkg") is renamed like a title-ID-only name when its own header (PKG param.sfo, image param.json) gives the ID. Before, the namer only looked at names, so such a file stayed as it was forever.
- A DLC or update is still known by its header whatever its name, so it goes to the dlc/ or updates/ folder.

## Watch out
- A file whose header cannot be read (not a real PKG, or a fake PKG without param.sfo) keeps its name; the AI is not asked for it.

In the code: PlayStationLibraryNamer.NameSaysNothing, RunOnceAsync; test APkgWithNoIdInItsName_IsNamedFromItsHeader_AndADlcGoesToDlc.
