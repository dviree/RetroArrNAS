---
title: PC games go to pc
author: owner
date: 2026-10-30
branch: nas/phase-a-qbit-import
cost: { minutes: 1, tokens: 3085 }
id: l-838670b4
scope: repo
status: active
src: agent
touches:
  - src/RetroArr.Core/MetadataSource/AiTitleService.cs
  - src/RetroArr.Core/Indexers/PlatformDetector.cs
---

A PC release goes to the pc folder, never to a store folder (steam, gog, epic). An AI answer naming a store becomes windows/pc.
