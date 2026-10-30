---
title: Versions are short
author: owner
date: 2026-10-30
branch: nas/phase-a-qbit-import
cost: { minutes: 1, tokens: 3085 }
id: l-321f3ec2
scope: repo
status: active
src: agent
touches:
  - "src/RetroArr.Core/Download/PlayStationImporter.cs#NormalizeVersion"
---

PS versions are written short in names: leading zeros and trailing .0 parts go, 02.002.000 is v2.2, 01.010.000 is v1.10, 01.000.002 is v1.0.2.
