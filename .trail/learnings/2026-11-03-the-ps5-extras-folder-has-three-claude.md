---
title: "The PS5 extras folder has three folders, backport, updates and dlc, and what lay there loose is sorted into them right away"
author: Claude
date: 2026-11-03
branch: nas/phase-a-qbit-import
cost: { minutes: 10, tokens: 319980 }
id: l-31e503c7
scope: repo
status: active
replaces: l-7ff05588
src: agent
---

## Decided
- Inside backport-updates-dlc: backport/<release>/, updates/<PKG>, dlc/<PKG>. New imports and PKGs moved out of roms/ps5 go straight to the right one, chosen by the [Update]/[DLC] tag in the name or else by the package itself.
- What already lay loose there is sorted into the three folders on the first run, on by default. The owner asked for exactly this sort and was annoyed when it shipped as a dry run that left the folder as it was. The folder holds only what RetroArr put there, so the general rule (moving features start as a dry run) does not cover it. PS_EXTRAS_SORT=false turns it back into a dry run that only writes extras-sort-plan.log. Nothing is moved over a file of the same name.
- The scanner looks in the root and all three folders, so a game's updates and DLC still count as its copies.

## Watch out
- A loose folder in the extras root goes into backport/ and is renamed there.
- When the owner asks for a move outright, the request is the go-ahead; a dry run is for moves they did not ask for.

In the code: PlayStationImporter.ExtrasFolders, ExtrasFolderFor; PlayStationLibraryNamer.SortExtras, SortExtrasOn; MediaScannerService.PlayStationCopies.
