# Backlog

Found bugs, stubs and ideas. Put no personal data here: no owner or host names, NAS paths, or keys.

## > RESUME HERE
- RetroArr now runs as a dry run (FILE_ACTIONS not on). Go through files.log with the owner: the "WOULD ..." lines say
  what each job would do. Turn on only what the owner approves (FILE_ACTIONS=on in the compose override).
- The owner reports a PS5 game folder (the PPSA26344 release) vanished. Find out where it went, from the NAS:
  files.log (new) and app.log in RetroArr's log folder; the trash's .trash-meta sidecars; a find for the title ID.
  The NAS remote session was disconnected, so this needs the owner to run the commands or reconnect.
- Check on the NAS that the image with the trash-folders change runs. Its revision label should start with `19ed976`, or be newer.
- Check that the bin's flat entries went back into their folders. The first cleanup runs 10 min after start.
- Check that the PS5 game whose import failed (a part01..part08 rar set, plus an extracted copy that failed on a
  locked file) is now in roms/ps5, and that its release went to the trash.

## Bugs
- (none open)

## Ideas
- Decide with the owner whether the trash should empty itself after 14 days (TrashRetentionDays), or never (0).
- Upgrade trail from 0.28.1 to 0.29.0 (`trail upgrade`, then restart the agent).

## Done (latest first)
- Compressed PS5 game folders always end in -lz4 or -zstd (by content), on import and by the namer.
- Locked rar/7z: passwords tried as JDownloader does (archive-passwords.txt, ARCHIVE_PASSWORDS, names).
- Every job that changes files is a dry run until FILE_ACTIONS=on. Plans and real actions all go to files.log.
- Nothing an import is done with is deleted any more (trash instead), and every file action is logged in files.log.
- LeftoverCleaner no longer takes a running copy or a fresh unpack for old (folder dates count).
- The trash keeps folders whole and regroups the old flat entries.
- roms/ps5 alone decides whether a PS5 game is in the library. zstd and LZ4 copies elsewhere do not count.
- part01.rar is now read as the first rar volume, and failed watch-folder imports are tried again.
