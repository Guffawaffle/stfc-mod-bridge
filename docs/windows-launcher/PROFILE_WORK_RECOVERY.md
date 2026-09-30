# Named launch profiles: recovery checkpoint

Guff's 2026-09-29 direction removes installation-marker selection completely.
There is no marker-file adoption, compatibility selector, fallback, migration
path or retained marker-specific test suite. This supersedes the previous
per-install proposal and its unshipped experiment.

Bridge continues to own `launch-profiles.json`, stable profile IDs, display names,
game-folder metadata and explicit launch selection. Create and track-existing
operations write only that registry. Existing IDs are supplied by the user.
Profiles may record the same game folder as each other and Default. Editing folder
metadata does not change the profile ID. Registry revision/operation locking and
stale-window checks remain.

Named launch passes `-stfc-profile <id>` alongside the derived config and log
arguments. Runtime qualification concerns that explicit request only; the old
capability export and marker design are removed from the active contract.
Ordinary launch does not derive its account from saved profile folder metadata.

The [profile repository](https://github.com/Guffawaffle/stfc-profiles) owns the
shared ID/store library and explicit-input game adapter. Host integration,
standalone bootstrap, readiness and live shared-install qualification remain
open. Bridge keeps its independent tracking implementation.

Local removal work is on `feature/named-launch-profiles-225`, based on
`b52c2d46f6b4245afa6f191b16ccfac9cc481e10`, updating draft PR #227.
See [the current contract](SHARED_INSTALL_PROFILE_CONTRACT.md).
