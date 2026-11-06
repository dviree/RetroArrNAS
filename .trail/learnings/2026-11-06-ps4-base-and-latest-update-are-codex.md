---
title: PS4 base and latest update are rebuilt into one validated PKG on Linux
author: Codex
date: 2026-11-06
branch: main
id: l-024e1683
scope: repo
status: active
src: agent
touches:
  - src/RetroArr.Host/Program.cs
  - Dockerfile
---

The owner explicitly requested a real PS4 base-game plus update merge, not folder consolidation. Ps4PkgMergeService is enabled by default (PS4_PKG_MERGE=false turns it off), scans only the configured PS4 library, pairs real header/SFO metadata by content ID and title ID, chooses the latest update newer than the base, and excludes DLC. Files must be quiet for ten minutes. A pinned MIT OrbisPkgTool CLI is built as portable, self-contained .NET 10 for Linux in Docker; a synthetic build/merge/extract smoke test proves the update overlay and version on Linux before shipping. Its upstream project targets Windows, but its managed merge path works when published for net10.0; do not use the unrelated split-PKG concatenation tools. Both the merged output and its staged library copy are validated; metadata, input snapshots, free space, collisions and cancellation are checked before publication. The originals go to TrashService only after verification and library path updates; they are never deleted. Source protection for torrent download folders is unchanged because this job works only on library copies. Generated packages have not been tested for installation or gameplay on a physical console; keep the trash backups. PS4_MERGE remains the older folder sorter, distinct from PS4_PKG_MERGE.
