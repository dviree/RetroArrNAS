---
title: Retarget the PS4 project before dotnet publish restores packages
author: Codex
date: 2026-11-06
branch: main
id: l-1004d98a
scope: repo
status: active
src: agent
touches:
  - Dockerfile
---

The Docker PS4 builder failed NETSDK1005: dotnet publish with -p:TargetFramework=net10.0 restored the pinned project for its original net10.0-windows target. The direct MSBuild -restore -target:Publish test had passed, so it did not reproduce the CLI restore behavior. Change the pinned csproj TargetFramework to net10.0 before invoking the ordinary dotnet publish command, assert the replacement, then publish self-contained for the target Linux architecture. A clean-obj local run of that exact CLI command and the package build/merge/extract smoke test both passed. Keep the smoke test in Docker.
