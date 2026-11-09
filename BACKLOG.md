# Backlog

Found bugs, stubs and ideas. Put no personal data here: no owner or host names, NAS paths, or keys.

## > RESUME HERE
- The NAS runs as a dry run (FILE_ACTIONS not set). Go through files.log with the owner: the "WOULD ..." lines say
  what each job would do. Turn on one job at a time in the compose override (FILE_ACTIONS=psnames first: it only
  adds -zstd to two folders in roms/ps5).
- Update the NAS to the newest image and check its revision label against the head of main.
- The PPSA26344 game: its folder in roms/ps5 was renamed by the namer on Nov 7 and was gone two hours later.
  RetroArr's logs show no move or delete of it. Still to read, as root on the NAS: a root-owned unpack script and
  its log beside the library (made the same evening), and RetroArr's real trash (the /trash mount, not config/trash).
  The rar set was unpacked by hand with unrar into the watch folder's extracted/ ("...-new"); check it finished.

## Bugs
- (none open)

## Ideas
- Decide with the owner whether the trash should empty itself after 14 days (TrashRetentionDays), or never (0).
- Upgrade trail from 0.28.1 to 0.29.0 (`trail upgrade`, then restart the agent).

## Done (latest first)
- Locked archives are now tested for real (RAR5 in volumes with locked headers, data-only, solid, 7z): a solid
  locked rar never found its password; fixed. Locked headers are proven by listing, so a 100 GB set is told at once.
- Dry-run leaks in the PS namer closed: no library-list update, empty-folder removal or "Renamed" log line in a dry run.
- In a dry run a planned import is no longer checked and logged again every minute.
- A watch folder's rar set waits 3 h for JDownloader to unpack it; RetroArr unpacks it only if JDownloader has not.
- FILE_ACTIONS can turn on single jobs (FILE_ACTIONS=psnames: only the PS naming, e.g. the -lz4/-zstd endings).
- Compressed PS5 game folders always end in -lz4 or -zstd (by content), on import and by the namer.
- Locked rar/7z: passwords tried as JDownloader does (archive-passwords.txt, ARCHIVE_PASSWORDS, names).
- Every job that changes files is a dry run until FILE_ACTIONS=on. Plans and real actions all go to files.log.
- Nothing an import is done with is deleted any more (trash instead), and every file action is logged in files.log.
- LeftoverCleaner no longer takes a running copy or a fresh unpack for old (folder dates count).
- The trash keeps folders whole and regroups the old flat entries.
- roms/ps5 alone decides whether a PS5 game is in the library. zstd and LZ4 copies elsewhere do not count.
- part01.rar is now read as the first rar volume, and failed watch-folder imports are tried again.
