---
title: "A Switch merge whose only failing check is the signature is published, not left in the parts folder"
author: Claude
date: 2026-11-01
branch: nas/phase-a-qbit-import
cost: { minutes: 12, tokens: 65117 }
id: l-18a44849
scope: repo
status: active
src: agent
touches:
  - README.md
  - src/RetroArr.Core.Test/Download/PlayStationImporterTest.cs
  - src/RetroArr.Core.Test/Games/PlayStationLibraryNamerTest.cs
  - src/RetroArr.Core/Download/PlayStationImporter.cs
  - src/RetroArr.Core/Games/PlayStationLibraryNamer.cs
---

## Decided
- The owner wants nothing left in the parts folder. A merged NSP whose lv3 verify says the content is correct and only "COULD'VE BEEN TAMPERED WITH" (the NCA headers are not Nintendo-signed any more) is published into roms/switch and noted as MODIFIED; the parts go to the trash as with any merge.
- The earlier rule (accept only when a part already failed lv2 on its signature) kept Tomodachi Life waiting: its parts passed lv2, so the merge itself changed the signature. That rule is dropped.
- The retry state key carries a rules number; raising it when what a merge accepts changes makes earlier failures retry at once instead of after 24 hours.

## Watch out
- A real failure (corrupt content, missing files, bad size) still blocks the merge and keeps the parts.

In the code: SwitchMergeService.MergeAsync (OnlySignature), MergeRules, StateKey.
