# Snapshot: where the NAS edition stands

A short blueprint so a session does not have to scan the repo. For code, use trail (`trail map`, `trail ask`).
This file only says where things are. Update it when a task changes them.

## Layout
- `src/RetroArr.Core/`: the logic. `src/RetroArr.Api.V3/`: the REST controllers. `src/RetroArr.Host/`: the app host.
  `frontend/`: the React UI. `src/RetroArr.Core.Test/`: the NUnit suite (`dotnet test src/RetroArr.Core.Test`).
- `.github/workflows/nas-image.yml` merges upstream (RiDDiX/RetroArr) into `main`, then builds and publishes
  `ghcr.io/<owner>/retroarrnas:latest`. The image is labelled with `org.opencontainers.image.revision`. The workflow
  stops when the upstream merge conflicts, so you merge `origin/main` by hand and push again.

- `.github/workflows/nas-check.yml` validates code branches and pull requests before they reach main: backend
  tests, frontend tests and a production UI build; it never publishes an image.

## The download pipeline (Core/Download)
1. Download clients: `QBittorrentClient` and `WatchFolderClient`. A watch folder can be recursive, and it can move
   or leave its files. qBittorrent files are never changed.
2. `TrackedDownloads/TrackedDownloadService` + `DownloadMonitorService` track each download through these states:
   Downloading → ImportPending → Importing → Imported, or DownloadFailed / ImportFailed / ImportBlocked / Ignored.
   A client failure suppresses its failed grab for 7 days; a client retry clears that failure. A failed
   watch-folder import is tried again after 1 h, up to 3 times, while its source is still there (`RetryFailedWatchFolderImport`).
3. `PostDownloadProcessor.ProcessCompletedDownloadAsync` unpacks the download and sends it to the platform importers.
   It retires the source to the trash only once the copy in the library is verified. Nothing is deleted: unpacked
   archives are set aside in `.retroarr-unpacked` and leave with the download, unwanted files are only skipped.
4. PlayStation: `PlayStationImporter` (with `PlayStationPackageInspector` and `Ps5PkgReader`).
   - Games go to roms/ps5. Fake PKGs go to roms/ps5/pkg.
   - Updates, DLC and backports go to backport-updates-dlc/{updates,dlc,backport}/<Game>. A PKG under 1 GB is an extra.
   - zstd games go to `PS_ZSTD_LIBRARY`.
   - roms/ps5 alone decides whether a game is already in the library, one copy per type (PKG, folder, LZ4, zstd,
     .exfat, .ffpfsc) side by side (`PlayStationImporter.TypeOf`).
   - A compressed game folder ends in -lz4 or -zstd by content (`PlayStationImporter.CompressionSuffix`).
7. A watch folder's rar set waits ARCHIVE_WAIT_HOURS (3) for JDownloader's own unpack (`CompletedDownloadService.WaitBeforeUnpacking`).
8. Archives: `IO/ArchiveService` (SharpCompress); a locked rar/7z gets its password from archive-passwords.txt,
   ARCHIVE_PASSWORDS or the names (`FindPassword`).
5. Switch: `Games/SwitchMergeService` makes one file per game in roms/switch. Switch 2 games go flat into roms/switch2.
6. Platform detection: `Indexers/PlatformDetector`. What a file says inside it, for the AI: `MetadataSource/FileFacts`.

## Files and the trash (Core/Games)
- `TrashService`: nothing is deleted. A trashed folder stays whole in the bin, with a sidecar in `.trash-meta`.
  `RegroupFlatEntriesAsync` puts back into their folders any entries laid out flat by older versions.
- `LeftoverCleaner`: every 6 h it moves old work folders, RetroArr's half-written files and `CLEANUP_FOLDERS` to the
  trash. `CLEANUP=dryrun` only writes cleanup-plan.log.
- `IO/FileActions`: file handling runs by default. FILE_ACTIONS=off/dryrun disables it, or a job list limits it
  (import, cleanup, trash, switch, ps4merge, psnames, bios). Automatic bin purging requires an explicit trash entry.
- `IO/FileLog`: every move, copy, trash, restore, purge and delete, and each "WOULD ..." step of a dry run, goes to one file, files.log.
- `BiosSorter`: off by default, with a dry run first. It never takes apart folders it did not create.

## Rules that shape the code
These are the learnings in `.trail/learnings/`. Read them with `trail ask`.
