---
title: qBittorrent folders are never changed
author: owner
date: 2026-10-30
branch: nas/phase-a-qbit-import
cost: { minutes: 1, tokens: 3085 }
id: l-f617d4d9
scope: repo
status: active
src: agent
touches:
  - src/RetroArr.Core/IO/FileMoverService.cs
  - src/RetroArr.Core/Download/PostDownloadProcessor.cs
---

The qBittorrent download folder is mounted read-only and must never be moved, renamed or deleted from: it is still seeding. The library gets a hardlink or reflink copy (FileMoverService), a plain copy only when neither works. Only JDownloader/incoming sources may go to the trash, and only after every file is verified in the library.
