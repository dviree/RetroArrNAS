# Working in this repo

This is the NAS edition of RetroArr (see the "NAS edition" section of README.md).

## Use Trail-code on every change

The repo is wired for [Trail-code](https://github.com/trailhq/Trail-Code): a code map and the learnings earlier
sessions left in `.trail/learnings/`. A session start installs the `trail` command when it is missing.

- Everything in the code goes through trail, also when the file or symbol is known already. Never `sed`, `grep`,
  `cat` or a plain file read on source code:
  - find: `trail ask "<what>" --source`, every occurrence: `trail grep "<literal>"`
  - read a span to edit it: `trail ask "<Symbol>" --source --full`, a file's API: `trail skeleton <file>`
  - before changing a symbol: `trail callers <Symbol> --depth all`
  - before a push, what the diff touches: `trail blast --base origin/main`
  - When trail has no hit (docs, config, workflows are not in the map), say so, then read that file.
- Read the learnings the hooks bring up; the ones marked always-follow are the owner's rules.
- Before a push: `trail check` (the diff against the learnings), then the whole test suite.
- When a session works something out or the owner corrects how something is done, save it: `trail note`.

## Before every push

- `dotnet test src/RetroArr.Core.Test` passes.
- Push to `main` and `nas/phase-a-qbit-import`. When `main` was moved (the image workflow merges upstream into it),
  fetch and merge `origin/main`, then push again.
- No personal data: no owner or host names, NAS volume paths, names of the owner's old tools, keys.
