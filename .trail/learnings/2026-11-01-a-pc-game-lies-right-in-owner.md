---
title: "A PC game lies right in its title folder, no release folder inside"
author: owner
date: 2026-11-01
branch: nas/phase-a-qbit-import
cost: { minutes: 4, tokens: 26337 }
id: l-623a9453
scope: repo
status: active
src: agent
touches:
  - "src/RetroArr.Core/Download/PostDownloadProcessor.cs#AutoMoveToLibrary"
---

## Decided
A PC release is copied straight into pc/<Title>/, because the owner does not want a second folder named for the release (Sackboy - A Big Adventure/Sackboy-SteamRIP.com/...) inside the title folder. Other platforms still nest the release name when a title was found, as before.

In the code:
- PostDownloadProcessor.AutoMoveToLibrary: shouldNest only when the platform is not PC (PcPlatformId 1)
- test APcRelease_LiesRightInItsTitleFolder_NoReleaseFolderInside
