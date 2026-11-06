---
title: "After merging upstream v1.0.237: the trash keeps both layouts, the relinker reads all games, and our platform folders stayed"
author: Claude
date: 2026-11-06
branch: nas/phase-a-qbit-import
cost: { minutes: 14, tokens: 51272 }
id: l-76fa93f1
scope: repo
status: active
src: agent
---

## Decided
- Upstream's trash now reads one entry by its folder only (old layout). Our trash is flat (files right in the trash, sidecars in .trash-meta/<id>.json), so Get tries the old folder first, then the flat sidecar; List reads both. Writes use upstream's AtomicFile.
- Upstream's GetMissingAsync now takes a list of ids and returns only status fields, no paths. The missing-game relinker needs the paths, so it reads all games (GetAllLightAsync) and takes those with MissingSince set.
- RetireSourceAsync uses upstream's IO.PathGuard.IsCriticalPath (the local helper was removed upstream).
- Upstream renamed some platform folders to match Batocera/RetroBat/ES-DE (PS Vita vita -> psvita, Sega 32X 32x -> sega32x, Amiga CD32 cd32 -> amigacd32, ...). pc, ps4, ps5, switch and switch2 are unchanged.

## Watch out
- PlatformDetector.cs is CRLF upstream; an edit that turns it into LF makes every upstream change a whole-file conflict. Keep its line endings.
- The nightly image workflow stops on a merge conflict and builds nothing; check the sync job first when builds fail in seconds.

In the code: Games/TrashService.cs (Get, ReadFlat, List), Games/MissingGameRelinker.cs, Download/PostDownloadProcessor.cs RetireSourceAsync, Indexers/PlatformDetector.cs.
