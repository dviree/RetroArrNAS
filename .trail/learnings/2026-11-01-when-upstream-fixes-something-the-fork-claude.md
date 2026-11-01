---
title: "When upstream fixes something the fork fixed differently, keep upstream's way and drop the fork's"
author: Claude
date: 2026-11-01
branch: nas/phase-a-qbit-import
cost: { minutes: 7, tokens: 202901 }
id: l-81d4a6ca
scope: repo
status: active
src: agent
touches:
  - src/RetroArr.Api.V3/DownloadClients/DownloadClientController.cs
  - CHANGELOG.md
  - package.json
  - package-lock.json
  - src/RetroArr.Core.Test/RetroArr.Core.Test.csproj
  - src/RetroArr.Core/RetroArr.Core.csproj
  - src/RetroArr.Http/RetroArr.Http.csproj
  - frontend/src/pages/Library.tsx
  - frontend/src/pages/Problems.tsx
  - src/RetroArr.Api.V3/Games/GameController.cs
---

## Decided
- Upstream now gives a new download client an id that neither history nor tracking still holds, so a deleted client's tracked downloads stay under its old id. The fork used to stop tracking a deleted client's downloads, which freed the id for reuse. Together they broke upstream's test, so the fork's code was removed and upstream's approach kept: it keeps history, and it makes later upstream merges smoother.

## Watch out
- After merging upstream, run the whole suite. When an upstream test fails only in the fork, check it on pure upstream first (a git worktree of upstream/main): a pass there means a fork change is in the way.

In the code: DownloadClientController.Create (id = max of clients, tracked and history + 1) and Delete; test DownloadClientAddTest.Create_AfterDeletingTheNewestClient_TakesAFreshId.
