---
title: Prove a change before pushing
author: owner
date: 2026-10-30
branch: nas/phase-a-qbit-import
cost: { minutes: 1, tokens: 3085 }
id: l-57fa0a81
scope: repo
status: active
src: agent
touches:
  - ".github/workflows/nas-image.yml"
---

Before every push: build and run the whole test suite (dotnet test src/RetroArr.Core.Test), and push to both main and nas/phase-a-qbit-import. main is what the NAS image is built from; when the push is rejected, fetch and merge origin/main (the workflow merges upstream into it) and push again.
