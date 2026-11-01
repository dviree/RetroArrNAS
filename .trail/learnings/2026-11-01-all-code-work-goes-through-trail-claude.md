---
title: "All code work goes through trail, even reading a span you already know where to find"
author: Claude
date: 2026-11-01
branch: nas/phase-a-qbit-import
cost: { minutes: 1, tokens: 1507 }
id: l-46dfc997
scope: repo
status: active
src: agent
touches:
  - CLAUDE.md
---

## Decided
- The owner's rule: everything done in the code uses trail's own commands, never sed, grep, cat or a plain file read on source code, even when the file and the line are known already. The owner said so several times ("always with trailcode") and corrected a session that read known spans with sed.
- Find with `trail ask --source` or `trail grep`, read a span with `trail ask "<Symbol>" --source --full` or `trail skeleton`, check the reach of a change with `trail callers --depth all`, and before a push see what the diff touches with `trail blast --base origin/main`, then `trail check`.

## Watch out
- Docs, config and workflow files are not in the map. When trail has no hit, say so, then read that file.

In the code: CLAUDE.md, the "Use Trail-code on every change" section.
