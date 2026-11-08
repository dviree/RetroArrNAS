---
title: "FILE_ACTIONS can turn on one job at a time, so the owner can allow only the PlayStation naming"
author: Claude
date: 2026-11-08
branch: nas/phase-a-qbit-import
cost: { minutes: 26, tokens: 1100595 }
id: l-f917d0e0
scope: repo
status: active
src: agent
touches:
  - src/RetroArr.Core.Test/IO/ArchivePasswordTest.cs
  - src/RetroArr.Core/IO/ArchiveService.cs
  - src/RetroArr.Core.Test/IO/FileActionsTest.cs
---

## Decided
The owner wanted the -lz4/-zstd endings in roms/ps5 without turning on imports or anything else. FILE_ACTIONS is still a dry run by default. It can be "on" (every job) or a list of jobs: import, cleanup, trash, switch, ps4merge, psnames, bios. Each job checks only its own name.

## Watch out
psnames covers all of the PlayStation namer's steps: renames, the extras sort, sorting into game folders, and moving zstd games out of the LZ4 library. Read files.log's WOULD lines first. On the owner's NAS on Nov 8 they held only two renames, both adding -zstd.

In the code: IO/FileActions.DryRunFor and the job constants. Each service calls DryRunFor(FileActions.<Job>).
