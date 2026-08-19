# Local game-install certification

STFC Mod Bridge has an explicitly opted-in certification harness for a
human-maintained real Star Trek Fleet Command installation. It is additional
first-party dogfood evidence, not a contributor or GitHub CI requirement.

The harness implements the read-only **Inspect** profile, restorable provider
install/remove, manual/developer DLL adoption, final-residue, and source-switch
journeys behind **Mutate** plus **Live providers**, and a provider-scoped TOML
restore/recovery journey behind the **Recovery lab** profile. Updates, repair,
direct game launch, and the remaining permutations are tracked by issue #63.
Provider-scoped TOML history and coordinated switching are tracked by #65.

## Run the Inspect profile

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

## Inspect coverage

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

Live install/remove and provider-switch dogfood requires both mutation and
network switches:

```powershell
./scripts/test-local-game-install.ps1 `
  -GameDirectory 'E:\path\to\Star Trek Fleet Command\default\game' `
  -AllowRestorableMutation `
  -UseLiveProviderReleases
```

The provider-install and switch journey within this profile requires a clean
maintained target without `version.dll`, `stfc-runtime-manifest.json`, or TOML;
it reports an explicit skip on a changed starting state while other eligible
live journeys continue. It uses
isolated launcher state, production provider trust/deployment/removal paths,
and an exact before/after target fingerprint. The clean-target campaign also
performs Guffawaffle → NetniV → Guffawaffle using distinct byte-identifiable
TOML profiles, verifies each managed DLL attribution, then removes the mod and
returns the target to its clean baseline. A different validated STFC
installation may remain running.

The same profile also runs a separately isolated manual-adoption journey when
the target already has a human-managed `version.dll`. The harness never
replaces human files with a test fixture before production adoption begins. It
proves the production health model classifies the DLL as manual and that a
missing adoption decision refuses before artifact download or game-file
mutation. Explicit adoption then installs the latest publisher-authenticated
Guffawaffle DLL through the production trust and transaction path. Production
Remove must restore the exact prior DLL. A human-managed runtime manifest, when
present, remains outside a newer signed DLL's reviewed runtime-activation
authority and is preserved byte-for-byte.

Every successful live campaign additionally verifies that no managed receipt,
nonterminal journal, transaction stage, rollback, restore, partial download,
temporary file, or exact owned game process remains. The complete top-level
game fingerprint must match its baseline before the isolated launcher state is
deleted and its absence is asserted.

The maintained target proves Bridge projects the observed installation and mod
state truthfully. The path and binary identities remain local-only.

## Run the Recovery lab profile

The configuration-history journey requires separate mutation and recovery
opt-ins but does not imply live-provider network access:

```powershell
./scripts/test-local-game-install.ps1 `
  -GameDirectory 'E:\path\to\Star Trek Fleet Command\default\game' `
  -AllowRestorableMutation `
  -ExerciseRecovery
```

The recovery lab:

- requires the exact selected game to be closed;
- requires `version.dll` to match the reviewed Guffawaffle stable artifact before
  attributing any TOML history to that provider partition;
- captures the target and isolated launcher-state baseline;
- preflights an existing TOML through the parser and exact Guffawaffle catalog,
  or creates a known-valid test-owned TOML when none exists;
- protects the source bytes in encrypted provider-scoped history without
  logging the path, values, secrets, or hashes;
- changes the TOML through the production atomic repository and completes one
  public manual restore;
- injects an interruption immediately after a second restore commits its TOML,
  then uses a fresh production coordinator to finish journal and receipt
  recovery;
- restores the original TOML bytes through the production restore path and
  verifies the complete starting fingerprint after bounded harness cleanup.

Parser-invalid or catalog-blocked existing TOML fails before game-file
mutation. An unknown, modified, manual, NetniV, or non-stable DLL also fails
before TOML inspection or mutation. A recovery or cleanup defect still fails
the run even if the bounded emergency safeguard restores the human-owned target
afterward; isolated journals and encrypted receipts are retained for recovery
inspection on any failed journey and deleted only after full verification.

## Explicit profiles

Each profile will have its own runner switch. Supplying a target path never
implicitly authorizes downloads, mutations, or process launch.

| Profile | Intent |
|---|---|
| Inspect | Implemented: read-only discovery, health, provenance, Diagnostics, and TOML parsing |
| Mutate | Partially implemented: provider install/remove, manual adoption, provider switch, TOML backup, and restore |
| Live providers | Implemented for current journeys: real Guffawaffle manifest/trust, NetniV reviewed-hash discovery/download, manual adoption, and final residue audit |
| Launch | Planned: direct `prime.exe` launch with exact owned-PID observation and cleanup |
| Recovery lab | Partially implemented: representative post-TOML-commit interruption with production recovery proof |

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
