# Backlog

Found bugs, stubs and ideas. Put no personal data here: no owner or host names, NAS paths, or keys.

## > RESUME HERE
- Check on the NAS that the image with the trash-folders change runs. Its revision label should start with `19ed976`, or be newer.
- Check that the bin's flat entries went back into their folders. The first cleanup runs 10 min after start.
- Check that the PS5 game whose import failed (a part01..part08 rar set, plus an extracted copy that failed on a
  locked file) is now in roms/ps5, and that its release went to the trash.

## Bugs
- (none open)

## Ideas
- Upgrade trail from 0.28.1 to 0.29.0 (`trail upgrade`, then restart the agent).

## Done (latest first)
- The trash keeps folders whole and regroups the old flat entries.
- roms/ps5 alone decides whether a PS5 game is in the library. zstd and LZ4 copies elsewhere do not count.
- part01.rar is now read as the first rar volume, and failed watch-folder imports are tried again.
