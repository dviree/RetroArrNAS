---
title: Preserve platform correction when integrating upstream failed-grab tracking
author: Codex
date: 2026-11-13
branch: main
id: l-b9d0aeb8
scope: repo
status: active
src: agent
touches:
  - src/RetroArr.Core/Download/TrackedDownloads/TrackedDownloadService.cs
---

TrackedDownloadService.TrackDownload must keep both blocks after platform assignment when merging v1.0.239: the NAS watch-folder platform correction first, then upstream failed-grab bookkeeping. A corrected platform can reopen an ImportFailed job; DownloadFailed is a separate state used to suppress dead grabs. The trail check hit against the Failed helper in TrackedDownloadServiceTest is a false positive: it builds a DownloadStatus, does not rename any folders, and is unrelated to the PS4 naming learning.
