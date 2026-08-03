# Local game-install certification

STFC Mod Bridge has an explicitly opted-in certification harness for a
human-maintained real Star Trek Fleet Command installation. It is additional
first-party dogfood evidence, not a contributor or GitHub CI requirement.

Wave 0 implements the read-only **Inspect** profile. Later profiles will use the
same exact-target and restoration boundary to exercise provider installation,
updates, repair, switching, removal, direct game launch, and recovery. Their
permutations and prerequisites are tracked by issue #63; provider-scoped TOML
history and coordinated switching are product work tracked by #65.

## Run the implemented Inspect profile

Pass the directory that directly contains `prime.exe`:

```powershell
./scripts/test-local-game-install.ps1 `
  -GameDirectory 'E:\path\to\Star Trek Fleet Command\default\game'
```

Or configure the local shell and omit the parameter:

```powershell
$env:STFC_BRIDGE_INTEGRATION_GAME_DIR = 'E:\path\to\Star Trek Fleet Command\default\game'
./scripts/test-local-game-install.ps1
```

The runner sets `STFC_BRIDGE_RUN_LOCAL_GAME_INTEGRATION=1` only for its child
test process. A configured path alone is insufficient to activate a live test.
Ordinary solution tests and CI compile the deterministic contract tests, report
the real-install cases as skipped, and never inspect a real installation.

## Wave 0 coverage

The local suite:

- validates the exact game root through `GameInstallValidator`;
- proves the production `STFC_GAME_DIRECTORY` bounded-discovery seam finds the
  opted-in target;
- reports `version.dll` as absent or as an unmanaged/manual installation using
  the production installation inspector;
- reads `community_patch_settings.toml` when present and otherwise reports that
  no configuration is selected;
- fingerprints the target before and after every seam to detect a top-level or
  critical-file mutation.

It does not launch `prime.exe`, persist a game-folder selection, download or
deploy a mod, install/update/repair/remove anything, or write TOML. Absolute
paths, TOML values, tokens, and file hashes are not written to committed test
artifacts.

The maintained clean target currently proves the Bridge projects a valid game
installation with no `version.dll` as **Not installed** and offers the expected
**Install** action. The path and binary identities remain local-only.

## Planned explicit profiles

Each profile will have its own runner switch. Supplying a target path never
implicitly authorizes downloads, mutations, or process launch.

| Profile | Intent |
|---|---|
| Inspect | Read-only discovery, health, provenance, Diagnostics, and TOML parsing |
| Mutate | Journaled install, repair, provider switch, remove, TOML backup, and restore |
| Live providers | Real Guffawaffle manifest/trust and NetniV reviewed-hash discovery/download |
| Launch | Direct `prime.exe` launch with exact owned-PID observation and cleanup |
| Recovery lab | Injected transaction failures with production rollback/recovery proof |

The real target will run representative lifecycle journeys rather than a naive
Cartesian product. Deterministic disposable tests own exhaustive malformed
manifest, digest, phase-failure, stale-revision, retention, and concurrency
matrices.

## Human-owned target lifecycle

Until Mod Bridge has an authoritative game-version check and update contract,
the human operator owns keeping the opted-in installation current and avoiding
an integration run during a game update. A changed installation state is valid
input: for example, the suite accepts both a clean target with no `version.dll`
and a manually installed mod, then verifies the Bridge reports the observed
state truthfully.

The v1 harness owns one exact target. Multi-profile, shared-install,
cloned-install, and concurrent-client topology is separate v2 research tracked
by issue #64.

## Mutable-profile restoration boundary

Before mutable or launch coverage is enabled, the harness must:

- require a separate explicit opt-in and print its planned scenarios;
- require the game closed before any download or write;
- scope that running-game gate to the exact validated installation executable;
  another STFC installation may remain running;
- snapshot and hash the exact game and isolated launcher-owned state;
- use only production trust, transaction, cleanup, and recovery paths;
- stop only an exact process PID created by the selected launch scenario;
- return DLL, TOML, provider selection, process state, and top-level files to
  their exact baseline;
- retain redacted recovery evidence and fail if production cleanup is
  incomplete.

A bounded emergency backup may protect the human-owned target after a cleanup
defect, but emergency restoration never converts that defect into a passing
test. Provider-specific TOML histories retain the five newest verified backups
per provider and installation, with no age-based or cross-provider pruning, as
defined by #65.
