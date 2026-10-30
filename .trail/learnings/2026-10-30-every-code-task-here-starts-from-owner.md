---
title: Every code task here starts from the code map
author: owner
date: 2026-10-30
branch: nas/phase-a-qbit-import
cost: { minutes: 1, tokens: 1503 }
id: l-a5fa2d20
scope: repo
status: active
src: agent
touches:
  - CLAUDE.md
---

## Decided
The owner wants Trail-code used for every bit of work on this repo, thinking and running code alike, because finding code blind cost many wrong turns before: look code up with trail ask, trail grep, trail skeleton or trail callers before reading or changing it, run trail check before every push, and save what a session works out with trail note.

## Watch out
A session that falls back to grep or reading whole files goes against the owner's word.

In the code:
- CLAUDE.md, section Use Trail-code on every change
- .claude/settings.json installs trail at session start
