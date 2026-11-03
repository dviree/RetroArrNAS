---
title: "PS5 PKGs are read from inside, and the AI is shown what is inside the files, not just their names"
author: Claude
date: 2026-11-03
branch: nas/phase-a-qbit-import
cost: { minutes: 8, tokens: 72710 }
id: l-04814c84
scope: repo
status: active
replaces: l-6c6e5815
src: agent
touches:
  - src/RetroArr.Core/MetadataSource/FileFacts.cs
---

## Decided
- A PS5 PKG (magic 7F 46 49 48) carries its content ID at 0x80040 and a plain-text param.json about 527 KB in (titleId, contentId, contentVersion, localizedParameters.titleName). RetroArr reads both; checked on the owner's Sackboy and Ghost of Yotei DLC and a game PKG.
- A DLC's titleName is the DLC's own name ("Kratos Costume", "Pride Costume", "New Digital Deluxe Edition"), never the game's. For a DLC the game's name comes from the library (the name most files with the ID share), then the AI, and only then its own name.
- Every AI call gets FileFacts: per file (a folder's largest five) its size and what its header says: PKG title/content ID/own name/version/kind, PS image param.json, ISO9660 volume, GameCube/Wii game ID and title, NDS/GBA/N64 title and game code. The prompt says to trust these over file names and to name the game a DLC belongs to. The owner asked for the AI to read inside the files.
- The 1 GB rule stays: a PS5 PKG under 1 GB is an update (name or version past 1.0 says so) or a DLC; the header does not say which.

## Watch out
- The importer decides update/DLC before the title, or a DLC is named after itself.
- Tests: tiny stand-in PKGs are games (TestDefaults sets the 1 GB limit to 0); PlayStationReadersTest.Ps5Pkg builds a PS5 PKG laid out like the real ones.

In the code: Ps5PkgReader, PlayStationPackageInspector.ForFile, MetadataSource.FileFacts, AiTitleService.IdentifyAsync(inside), PlayStationImporter.TitleForAsync/ImportOneAsync, PlayStationLibraryNamer.NewNameAsync, and the AI calls in PostDownloadProcessor, CompletedDownloadService, AiMetadataFixer, MediaScannerService.
