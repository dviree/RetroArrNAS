---
title: Sessions start from SNAPSHOT.md and BACKLOG.md and update both with CLAUDE.md when a task is done
author: Claude
date: 2026-11-07
branch: nas/phase-a-qbit-import
cost: { minutes: 4, tokens: 156611 }
id: l-cb28434f
scope: repo
status: active
src: agent
touches:
  - SNAPSHOT.md
  - BACKLOG.md
  - CLAUDE.md
---

## Decided
The owner wants memory files beside trail: SNAPSHOT.md is a short blueprint of where things are, and BACKLOG.md holds open bugs, ideas and a "> RESUME HERE" block for work that spans sessions. When a task is done, update all three files (with CLAUDE.md) and commit them with the change. trail stays the way to read code; SNAPSHOT only points.

## Watch out
Both files are public: no owner or host names, NAS paths, or keys.

In the code: SNAPSHOT.md, BACKLOG.md, CLAUDE.md ("Memory files").
